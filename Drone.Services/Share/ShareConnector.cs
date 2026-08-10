using global::System.Net.Http.Headers;
using global::System.Net.WebSockets;
using global::System.Text;
using global::System.Text.Json;
using Drone.Core;
using Drone.Core.Config;

namespace Drone.Services.Share;

public class ShareConnector : IAsyncDisposable
{
    private readonly ShareConfig _config;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;

    public event Func<string, string, Task>? OnFileChanged;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public ShareConnector(ShareConfig config, ILogger logger)
    {
        _config = config; _logger = logger;
        _http = new HttpClient();
        if (!string.IsNullOrEmpty(config.ServerUrl)) _http.BaseAddress = new Uri(config.ServerUrl);
        _http.DefaultRequestHeaders.Add("X-Api-Key", config.AdminApiKey ?? "");
    }

    public async Task ConnectNotificationsAsync(string wsUrl, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("X-Api-Key", _config.WebSocketToken ?? "");
        try { await _ws.ConnectAsync(new Uri(wsUrl), _cts.Token); _logger.LogInformation("Share notifications connected at " + wsUrl); _ = Task.Run(() => NotificationLoop(_cts.Token)); }
        catch (Exception ex) { _logger.LogWarning("Share notification connection failed: " + ex.Message); }
    }

    public async Task<bool> UploadFileAsync(string localPath, string remotePath, CancellationToken ct = default)
    {
        try { using var content = new MultipartFormDataContent(); var fileBytes = await File.ReadAllBytesAsync(localPath, ct); var fc = new ByteArrayContent(fileBytes); fc.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream"); content.Add(fc, "file", Path.GetFileName(localPath)); content.Add(new StringContent(remotePath), "path"); var response = await _http.PostAsync("/api/files/upload", content, ct); return response.IsSuccessStatusCode; }
        catch (Exception ex) { _logger.LogError("Upload failed: " + ex.Message); return false; }
    }

    public async Task<bool> DownloadFileAsync(string remotePath, string localPath, CancellationToken ct = default)
    {
        try { var response = await _http.GetAsync("/api/files/download/" + Uri.EscapeDataString(remotePath), ct); response.EnsureSuccessStatusCode(); var dir = Path.GetDirectoryName(localPath); if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir); using var fs = File.Create(localPath); await response.Content.CopyToAsync(fs, ct); return true; }
        catch (Exception ex) { _logger.LogError("Download failed: " + ex.Message); return false; }
    }

    public async Task<ShareFileInfo[]> ListFilesAsync(string? path = null, CancellationToken ct = default)
    {
        try { var url = string.IsNullOrEmpty(path) ? "/api/files" : "/api/files?path=" + Uri.EscapeDataString(path); var response = await _http.GetAsync(url, ct); response.EnsureSuccessStatusCode(); var json = await response.Content.ReadAsStringAsync(ct); return JsonSerializer.Deserialize<ShareFileInfo[]>(json) ?? []; }
        catch (Exception ex) { _logger.LogError("List files failed: " + ex.Message); return []; }
    }

    public async Task<bool> DeleteFileAsync(string remotePath, CancellationToken ct = default)
    {
        try { var response = await _http.DeleteAsync("/api/files/" + Uri.EscapeDataString(remotePath), ct); return response.IsSuccessStatusCode; }
        catch (Exception ex) { _logger.LogError("Delete failed: " + ex.Message); return false; }
    }

    public async Task<int> SyncFolderAsync(string localFolder, string remoteFolder, CancellationToken ct = default)
    {
        var uploaded = 0;
        var remoteFiles = await ListFilesAsync(remoteFolder, ct);
        var remoteSet = remoteFiles.ToDictionary(f => f.Path, f => f.ModifiedAt);
        foreach (var localFile in Directory.GetFiles(localFolder, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(localFolder, localFile).Replace('\\', '/');
            var remotePath = remoteFolder + "/" + relativePath;
            var localModified = File.GetLastWriteTimeUtc(localFile);
            if (!remoteSet.TryGetValue(remotePath, out var remoteModified) || localModified > remoteModified)
            { if (await UploadFileAsync(localFile, remotePath, ct)) uploaded++; }
        }
        _logger.LogInformation("Synced " + uploaded + " files from " + localFolder + " to " + remoteFolder);
        return uploaded;
    }

    private async Task NotificationLoop(CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            try
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("path", out var pathProp) && root.TryGetProperty("changeType", out var ctProp))
                { if (OnFileChanged != null) await OnFileChanged(pathProp.GetString() ?? "", ctProp.GetString() ?? ""); }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning("Share notification error: " + ex.Message); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_ws?.State == WebSocketState.Open) { try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None); } catch { } }
        _ws?.Dispose(); _cts?.Dispose(); _http.Dispose();
    }
}

public record ShareFileInfo(string Path, string Name, long Size, DateTime ModifiedAt, string ContentType);
