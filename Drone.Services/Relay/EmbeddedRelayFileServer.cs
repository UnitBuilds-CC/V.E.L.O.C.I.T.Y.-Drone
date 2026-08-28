using global::System.Collections.Concurrent;
using global::System.Net;
using global::System.Net.WebSockets;
using global::System.Text;
using global::System.Text.Json;
using Drone.Core;
using Drone.Core.Config;

namespace Drone.Services.Relay;

/// <summary>
/// File share service mounted on the relay server at /relay/share/.
/// Handles HTTP file operations and WebSocket change notifications.
/// </summary>
public class EmbeddedRelayFileServer : IAsyncDisposable
{
    private readonly RelayConfig _config;
    private readonly ILogger _logger;
    private readonly string _storagePath;
    private readonly ConcurrentDictionary<string, WebSocket> _notificationClients = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _notificationSendLocks = new();

    public EmbeddedRelayFileServer(RelayConfig config, ILogger logger)
    {
        _config = config;
        _logger = logger;
        _storagePath = config.StoragePath;

        if (!Directory.Exists(_storagePath))
            Directory.CreateDirectory(_storagePath);
    }

    public async Task HandleRequest(HttpListenerContext ctx, CancellationToken ct)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "";
        var method = ctx.Request.HttpMethod;

        // Strip /relay/share prefix to get the file API path
        var apiPath = path.StartsWith("/relay/share") ? path.Substring("/relay/share".Length) : path;
        if (string.IsNullOrEmpty(apiPath)) apiPath = "/";

        try
        {
            if (method == "POST" && apiPath == "/api/files/upload")
            {
                await HandleUpload(ctx, ct);
            }
            else if (method == "GET" && apiPath.StartsWith("/api/files/download/"))
            {
                var filePath = Uri.UnescapeDataString(apiPath.Substring("/api/files/download/".Length));
                if (!IsPathSafe(filePath))
                {
                    ctx.Response.StatusCode = 403;
                    await WriteJson(ctx, new { error = "Invalid path" });
                    return;
                }
                await HandleDownload(ctx, filePath);
            }
            else if (method == "GET" && apiPath == "/api/files")
            {
                var queryPath = ctx.Request.QueryString["path"];
                if (queryPath != null && !IsPathSafe(queryPath))
                {
                    ctx.Response.StatusCode = 403;
                    await WriteJson(ctx, new { error = "Invalid path" });
                    return;
                }
                await HandleList(ctx, queryPath);
            }
            else if (method == "DELETE" && apiPath.StartsWith("/api/files/"))
            {
                var filePath = Uri.UnescapeDataString(apiPath.Substring("/api/files/".Length));
                if (IsPathSafe(filePath))
                    await HandleDelete(ctx, filePath);
                else
                {
                    ctx.Response.StatusCode = 403;
                    await WriteJson(ctx, new { error = "Invalid path" });
                }
            }
            else
            {
                ctx.Response.StatusCode = 404;
                await WriteJson(ctx, new { error = "Not found", path = apiPath });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Relay file server error: {Error}", ex.Message);
            ctx.Response.StatusCode = 500;
            await WriteJson(ctx, new { error = "Internal server error" });
        }
    }

    public async Task HandleNotifications(string droneId, WebSocket ws, CancellationToken ct)
    {
        _notificationClients.TryAdd(droneId, ws);
        _notificationSendLocks[droneId] = new SemaphoreSlim(1, 1);
        _logger.LogInformation("Share notifications: {DroneId} connected", droneId);

        var buffer = new byte[1024];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch { /* client disconnected */ }
        finally
        {
            _notificationClients.TryRemove(droneId, out _);
            if (_notificationSendLocks.TryRemove(droneId, out var sendLock))
                sendLock.Dispose();
            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye", CancellationToken.None); }
                catch { /* ignore */ }
            }
            ws.Dispose();
        }
    }

    private async Task NotifyFileChange(string eventType, string filePath, long size = 0)
    {
        var msg = JsonSerializer.Serialize(new { type = eventType, path = filePath, size, timestamp = DateTime.UtcNow });
        var bytes = Encoding.UTF8.GetBytes(msg);

        foreach (var (id, ws) in _notificationClients)
        {
            if (ws.State == WebSocketState.Open && _notificationSendLocks.TryGetValue(id, out var sendLock))
            {
                if (!await sendLock.WaitAsync(TimeSpan.FromSeconds(5))) continue;
                try { await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); }
                catch { /* client gone */ }
                finally { sendLock.Release(); }
            }
        }
    }

    private async Task HandleUpload(HttpListenerContext ctx, CancellationToken ct)
    {
        if (!ctx.Request.HasEntityBody)
        {
            ctx.Response.StatusCode = 400;
            await WriteJson(ctx, new { error = "No body" });
            return;
        }

        // Enforce upload size limit before reading body
        if (ctx.Request.ContentLength64 > _config.MaxUploadSize)
        {
            ctx.Response.StatusCode = 413;
            await WriteJson(ctx, new { error = $"Upload exceeds maximum size of {_config.MaxUploadSize} bytes" });
            return;
        }

        var contentType = ctx.Request.ContentType ?? "";
        if (!contentType.Contains("multipart/form-data"))
        {
            ctx.Response.StatusCode = 400;
            await WriteJson(ctx, new { error = "Expected multipart/form-data" });
            return;
        }

        string? remotePath = null;
        byte[]? fileData = null;

        // Extract boundary — handle both quoted and unquoted, with possible semicolons
        var boundaryIdx = contentType.IndexOf("boundary=");
        if (boundaryIdx < 0)
        {
            ctx.Response.StatusCode = 400;
            await WriteJson(ctx, new { error = "Missing boundary" });
            return;
        }
        var boundary = contentType.Substring(boundaryIdx + "boundary=".Length).Trim('"').Split(';')[0].Trim();

        using var ms = new MemoryStream();
        // Read with size limit — use a bounded copy to prevent OOM even if ContentLength is lied about
        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;
        while ((bytesRead = await ctx.Request.InputStream.ReadAsync(buffer, ct)) > 0)
        {
            totalRead += bytesRead;
            if (totalRead > _config.MaxUploadSize)
            {
                ctx.Response.StatusCode = 413;
                await WriteJson(ctx, new { error = $"Upload exceeds maximum size of {_config.MaxUploadSize} bytes" });
                return;
            }
            ms.Write(buffer, 0, bytesRead);
        }
        var body = ms.ToArray();
        var bodyStr = Encoding.UTF8.GetString(body);

        // Normalize line endings for consistent parsing
        bodyStr = bodyStr.Replace("\r\n", "\n");
        var parts = bodyStr.Split("--" + boundary);

        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part) || part.Trim() == "--")
                continue;

            if (part.Contains("name=\"path\""))
            {
                var headerEnd = part.IndexOf("\n\n");
                if (headerEnd > 0)
                {
                    remotePath = part.Substring(headerEnd + 2).Trim().TrimEnd('-').Trim();
                }
            }
            else if (part.Contains("name=\"file\""))
            {
                var headerEnd = part.IndexOf("\n\n");
                if (headerEnd > 0)
                {
                    var dataStart = headerEnd + 2;
                    var dataEnd = part.LastIndexOf("\n--");
                    if (dataEnd > dataStart)
                    {
                        var dataStr = part.Substring(dataStart, dataEnd - dataStart);
                        fileData = Encoding.UTF8.GetBytes(dataStr);
                    }
                    else
                    {
                        // File data extends to end of part (no trailing boundary marker)
                        var dataStr = part.Substring(dataStart).TrimEnd('\n');
                        fileData = Encoding.UTF8.GetBytes(dataStr);
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(remotePath) || fileData == null)
        {
            ctx.Response.StatusCode = 400;
            await WriteJson(ctx, new { error = "Missing path or file" });
            return;
        }

        if (!IsPathSafe(remotePath))
        {
            ctx.Response.StatusCode = 403;
            await WriteJson(ctx, new { error = "Invalid path" });
            return;
        }

        // Enforce storage quota before writing
        var currentStorageSize = GetDirectorySize(_storagePath);
        if (currentStorageSize + fileData.Length > _config.StorageQuotaBytes)
        {
            ctx.Response.StatusCode = 507;
            await WriteJson(ctx, new { error = "Storage quota exceeded", quota = _config.StorageQuotaBytes, used = currentStorageSize });
            return;
        }

        var fullPath = Path.Combine(_storagePath, remotePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllBytesAsync(fullPath, fileData, ct);
        _logger.LogInformation("Relay file uploaded: {Path} ({Size} bytes)", remotePath, fileData.Length);

        await NotifyFileChange("created", remotePath, fileData.Length);
        await WriteJson(ctx, new { success = true, path = remotePath, size = fileData.Length });
    }

    private async Task HandleDownload(HttpListenerContext ctx, string filePath)
    {
        var fullPath = Path.Combine(_storagePath, filePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
        if (!File.Exists(fullPath))
        {
            ctx.Response.StatusCode = 404;
            await WriteJson(ctx, new { error = "File not found" });
            return;
        }

        var fileData = await File.ReadAllBytesAsync(fullPath);
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.ContentLength64 = fileData.Length;
        ctx.Response.AddHeader("Content-Disposition", $"attachment; filename=\"{Path.GetFileName(fullPath)}\"");
        await ctx.Response.OutputStream.WriteAsync(fileData);
    }

    private async Task HandleList(HttpListenerContext ctx, string? queryPath)
    {
        var searchPath = string.IsNullOrEmpty(queryPath)
            ? _storagePath
            : Path.Combine(_storagePath, queryPath.Replace("/", Path.DirectorySeparatorChar.ToString()));

        if (!Directory.Exists(searchPath))
        {
            ctx.Response.StatusCode = 404;
            await WriteJson(ctx, new { error = "Directory not found" });
            return;
        }

        var files = Directory.GetFiles(searchPath, "*", SearchOption.AllDirectories)
            .Select(f => new
            {
                path = Path.GetRelativePath(_storagePath, f).Replace("\\", "/"),
                name = Path.GetFileName(f),
                size = new FileInfo(f).Length,
                modifiedAt = File.GetLastWriteTimeUtc(f).ToString("o")
            })
            .ToArray();

        await WriteJson(ctx, files);
    }

    private async Task HandleDelete(HttpListenerContext ctx, string filePath)
    {
        var fullPath = Path.Combine(_storagePath, filePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
        if (!File.Exists(fullPath))
        {
            ctx.Response.StatusCode = 404;
            await WriteJson(ctx, new { error = "File not found" });
            return;
        }

        File.Delete(fullPath);
        _logger.LogInformation("Relay file deleted: {Path}", filePath);

        await NotifyFileChange("deleted", filePath);
        await WriteJson(ctx, new { success = true });
    }

    private static async Task WriteJson(HttpListenerContext ctx, object data)
    {
        var json = JsonSerializer.Serialize(data);
        ctx.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(json);
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private bool IsPathSafe(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        if (relativePath.Contains('~') || relativePath.Contains('\0')) return false;
        try
        {
            var combined = Path.Combine(_storagePath, relativePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
            var fullPath = Path.GetFullPath(combined);
            var storageRoot = Path.GetFullPath(_storagePath);
            if (!fullPath.StartsWith(storageRoot, StringComparison.OrdinalIgnoreCase))
                return false;

            var current = storageRoot;
            var relative = fullPath.Substring(storageRoot.Length).TrimStart(Path.DirectorySeparatorChar);
            foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (Directory.Exists(current))
                {
                    var info = new DirectoryInfo(current);
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        return false;
                }
                else if (File.Exists(current))
                {
                    var info = new FileInfo(current);
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        return false;
                }
            }
            return true;
        }
        catch { return false; }
    }

    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        return Directory.GetFiles(path, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, ws) in _notificationClients)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutting down", CancellationToken.None); }
            catch { /* ignore */ }
            ws.Dispose();
        }
        _notificationClients.Clear();
    }
}
