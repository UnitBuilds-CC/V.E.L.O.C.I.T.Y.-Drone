using global::System.Net;
using global::System.Text.Json;
using Drone.Core;

namespace Drone.Services.Share;

/// <summary>
/// Embedded file sharing server that implements the Velocity Share protocol.
/// Allows the drone to act as a file server for other drones/clients.
/// </summary>
public class EmbeddedFileServer : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly string _storagePath;
    private readonly string _listenUrl;
    private readonly string _apiKey;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    private const long MaxUploadSize = 100L * 1024 * 1024;

    private readonly global::System.Collections.Concurrent.ConcurrentDictionary<string, RateLimitEntry> _rateLimits = new();
    private const int MaxRequestsPerSecond = 30;

    public EmbeddedFileServer(ILogger logger, string storagePath, string listenUrl, string apiKey)
    {
        _logger = logger;
        _storagePath = storagePath;
        _listenUrl = listenUrl;
        _apiKey = apiKey;
        
        // Ensure storage directory exists
        if (!Directory.Exists(_storagePath))
            Directory.CreateDirectory(_storagePath);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new HttpListener();
        _listener.Prefixes.Add(_listenUrl);
        _listener.Start();
        _logger.LogInformation("Embedded file server started at {Url}, storage: {Path}", _listenUrl, _storagePath);

        _ = Task.Run(() => ListenLoop(_cts.Token));
        await Task.CompletedTask;
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(ctx, ct));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("File server listen error: {Error}", ex.Message);
            }
        }
    }

    private async Task HandleRequest(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            // Check API key (constant-time comparison to prevent timing attacks)
            var providedKey = ctx.Request.Headers["X-Api-Key"] ?? "";
            if (!string.IsNullOrEmpty(_apiKey))
            {
                var providedBytes = global::System.Text.Encoding.UTF8.GetBytes(providedKey);
                var expectedBytes = global::System.Text.Encoding.UTF8.GetBytes(_apiKey);
                if (!global::System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes))
                {
                    ctx.Response.StatusCode = 401;
                    await WriteJson(ctx, new { error = "Unauthorized" });
                    return;
                }
            }

            // Rate limiting
            var clientIp = ctx.Request.RemoteEndPoint?.Address.ToString() ?? "unknown";
            var entry = _rateLimits.GetOrAdd(clientIp, _ => new RateLimitEntry());
            if (!entry.TryAcquire(MaxRequestsPerSecond))
            {
                ctx.Response.StatusCode = 429;
                await WriteJson(ctx, new { error = "Rate limit exceeded" });
                return;
            }

            var path = ctx.Request.Url?.AbsolutePath ?? "";
            var method = ctx.Request.HttpMethod;

            if (method == "POST" && path == "/api/files/upload")
            {
                await HandleUpload(ctx, ct);
            }
            else if (method == "GET" && path.StartsWith("/api/files/download/"))
            {
                var filePath = Uri.UnescapeDataString(path.Substring("/api/files/download/".Length));
                if (!IsPathSafe(filePath))
                {
                    ctx.Response.StatusCode = 403;
                    await WriteJson(ctx, new { error = "Invalid path" });
                    return;
                }
                await HandleDownload(ctx, filePath);
            }
            else if (method == "GET" && path == "/api/files")
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
            else if (method == "DELETE" && path.StartsWith("/api/files/"))
            {
                var filePath = Uri.UnescapeDataString(path.Substring("/api/files/".Length));
                if (!IsPathSafe(filePath))
                {
                    ctx.Response.StatusCode = 403;
                    await WriteJson(ctx, new { error = "Invalid path" });
                    return;
                }
                await HandleDelete(ctx, filePath);
            }
            else
            {
                ctx.Response.StatusCode = 404;
                await WriteJson(ctx, new { error = "Not found" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("File server request error: {Error}", ex.Message);
            ctx.Response.StatusCode = 500;
            await WriteJson(ctx, new { error = "Internal server error" });
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

        var contentType = ctx.Request.ContentType ?? "";
        if (!contentType.Contains("multipart/form-data"))
        {
            ctx.Response.StatusCode = 400;
            await WriteJson(ctx, new { error = "Expected multipart/form-data" });
            return;
        }

        if (ctx.Request.ContentLength64 > MaxUploadSize)
        {
            ctx.Response.StatusCode = 413;
            await WriteJson(ctx, new { error = $"Upload exceeds maximum size of {MaxUploadSize} bytes" });
            return;
        }

        // Parse multipart form data
        string? remotePath = null;
        byte[]? fileData = null;

        var boundary = contentType.Split("boundary=")[1].Trim('"');
        using var ms = new MemoryStream();
        var readBuffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;
        while ((bytesRead = await ctx.Request.InputStream.ReadAsync(readBuffer, ct)) > 0)
        {
            totalRead += bytesRead;
            if (totalRead > MaxUploadSize)
            {
                ctx.Response.StatusCode = 413;
                await WriteJson(ctx, new { error = $"Upload exceeds maximum size of {MaxUploadSize} bytes" });
                return;
            }
            ms.Write(readBuffer, 0, bytesRead);
        }
        var body = ms.ToArray();
        var bodyStr = global::System.Text.Encoding.UTF8.GetString(body);
        var parts = bodyStr.Split("--" + boundary);

        foreach (var part in parts)
        {
            if (part.Contains("name=\"path\""))
            {
                var lines = part.Split("\r\n\r\n", 2);
                if (lines.Length > 1)
                    remotePath = lines[1].Trim().TrimEnd('-').Trim();
            }
            else if (part.Contains("name=\"file\""))
            {
                var headerEnd = part.IndexOf("\r\n\r\n");
                if (headerEnd > 0)
                {
                    var dataStart = headerEnd + 4;
                    var dataEnd = part.LastIndexOf("\r\n--");
                    if (dataEnd > dataStart)
                    {
                        var dataStr = part.Substring(dataStart, dataEnd - dataStart);
                        fileData = global::System.Text.Encoding.UTF8.GetBytes(dataStr);
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

        // Path traversal protection
        if (!IsPathSafe(remotePath))
        {
            ctx.Response.StatusCode = 403;
            await WriteJson(ctx, new { error = "Invalid path" });
            return;
        }

        // Save file
        var fullPath = Path.Combine(_storagePath, remotePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllBytesAsync(fullPath, fileData, ct);
        _logger.LogInformation("File uploaded: {Path} ({Size} bytes)", remotePath, fileData.Length);

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
        _logger.LogInformation("File downloaded: {Path} ({Size} bytes)", filePath, fileData.Length);
    }

    private async Task HandleList(HttpListenerContext ctx, string? queryPath)
    {
        var searchPath = string.IsNullOrEmpty(queryPath) ? _storagePath : Path.Combine(_storagePath, queryPath.Replace("/", Path.DirectorySeparatorChar.ToString()));
        
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
                modifiedAt = File.GetLastWriteTimeUtc(f).ToString("o"),
                contentType = "application/octet-stream"
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
        _logger.LogInformation("File deleted: {Path}", filePath);
        await WriteJson(ctx, new { success = true });
    }

    private async Task WriteJson<T>(HttpListenerContext ctx, T data)
    {
        var json = JsonSerializer.Serialize(data);
        ctx.Response.ContentType = "application/json";
        var bytes = global::System.Text.Encoding.UTF8.GetBytes(json);
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener?.Close();
        await Task.CompletedTask;
    }

    /// <summary>Validate that a relative path does not escape the storage directory (path traversal protection).</summary>
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

            // Check each component for symlinks/reparse points
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
}

internal class RateLimitEntry
{
    private int _tokens;
    private long _lastRefillTicks;
    private readonly object _lock = new();
    private readonly int _maxTokens;

    public RateLimitEntry(int maxTokens = 30)
    {
        _maxTokens = maxTokens;
        _tokens = maxTokens;
        _lastRefillTicks = global::System.DateTime.UtcNow.Ticks;
    }

    public bool TryAcquire(int refillRatePerSecond)
    {
        lock (_lock)
        {
            var now = global::System.DateTime.UtcNow.Ticks;
            var elapsed = (now - _lastRefillTicks) / (double)global::System.TimeSpan.TicksPerSecond;
            _tokens = (int)global::System.Math.Min(_maxTokens, _tokens + elapsed * refillRatePerSecond);
            _lastRefillTicks = now;

            if (_tokens >= 1)
            {
                _tokens--;
                return true;
            }
            return false;
        }
    }
}
