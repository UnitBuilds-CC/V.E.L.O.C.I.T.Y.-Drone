using global::System.Net.WebSockets;
using global::System.Text;
using global::System.Text.Json;
using Drone.Core;
using Drone.Core.Config;

namespace Drone.Services.Messenger;

public class MessengerConnector : IAsyncDisposable
{
    private readonly MessengerConfig _config;
    private readonly string _droneId;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private volatile bool _connected;
    private int _reconnectAttempts;
    private readonly CircuitBreaker _breaker = new(failureThreshold: 5, openTimeout: TimeSpan.FromSeconds(30));

    /// <summary>Lock to serialize concurrent WebSocket send operations.</summary>
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>Timeout for send operations to prevent hangs.</summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    public event Func<string, string, string, Task>? OnMessageReceived;
    public event Func<string, string, string, Task>? OnMediaReceived;
    public event Func<string, JsonElement, Task>? OnCallSignal;
    public event Func<bool, Task>? OnConnectionChanged;
    public event Func<JsonElement, Task>? OnControlResponse;

    public bool IsConnected => _connected;

    public MessengerConnector(MessengerConfig config, string droneId, ILogger logger)
    {
        _config = config;
        _droneId = droneId;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_config.ServerUrl)) throw new InvalidOperationException("Messenger ServerUrl is required.");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _reconnectAttempts = 0;
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                _ws = new ClientWebSocket();
                var separator = _config.ServerUrl.Contains("?") ? "&" : "?";
                var username = _droneId;
                var connectUrl = $"{_config.ServerUrl}{separator}username={Uri.EscapeDataString(username)}&device_id=drone";
                await _ws.ConnectAsync(new Uri(connectUrl), _cts.Token);
                var auth = JsonSerializer.Serialize(new { type = "auth", username, secret = _config.ConnectionSecret });
                await SafeSendAsync(Encoding.UTF8.GetBytes(auth), _cts.Token);
                _connected = true; _reconnectAttempts = 0;
                _logger.LogInformation("Connected to Messenger at {Url}", SanitizeUrl(_config.ServerUrl));
                if (OnConnectionChanged != null) await OnConnectionChanged(true);
                await ReceiveLoopAsync(_cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _connected = false;
                if (OnConnectionChanged != null) await OnConnectionChanged(false);
                if (!_config.AutoReconnect || _cts.Token.IsCancellationRequested) break;
                _reconnectAttempts++;
                // If circuit breaker is open, wait for its timeout before retrying
                if (_breaker.IsOpen)
                {
                    _logger.LogWarning("Messenger circuit breaker open. Waiting for recovery...");
                    await Task.Delay(30000, _cts.Token);
                    continue;
                }
                var delay = Math.Min(1000 * Math.Pow(2, _reconnectAttempts), 30000);
                _logger.LogWarning("Messenger disconnected. Reconnect attempt {Attempt} in {Delay}ms. Error: {Error}", _reconnectAttempts, (int)delay, ex.Message);
                await Task.Delay((int)delay, _cts.Token);
            }
        }
    }

    public async Task SendMessageAsync(string to, string content, CancellationToken ct = default)
    {
        var msg = JsonSerializer.Serialize(new { type = "chat", to, content });
        await SafeSendAsync(Encoding.UTF8.GetBytes(msg), ct);
    }

    public async Task SendGroupMessageAsync(string groupId, string content, CancellationToken ct = default)
    {
        var msg = JsonSerializer.Serialize(new { type = "group_message", group = groupId, content });
        await SafeSendAsync(Encoding.UTF8.GetBytes(msg), ct);
    }

    public async Task<string> UploadMediaAsync(string filePath, string mediaType, CancellationToken ct = default)
    {
        var fileBytes = await File.ReadAllBytesAsync(filePath, ct);
        var base64 = Convert.ToBase64String(fileBytes);
        var fileName = Path.GetFileName(filePath);
        var msg = JsonSerializer.Serialize(new { type = "media_upload", fileName, mediaType, data = base64 });
        await SafeSendAsync(Encoding.UTF8.GetBytes(msg), ct);
        return fileName;
    }

    public async Task DownloadMediaAsync(string url, string localPath, CancellationToken ct = default)
    {
        var data = await _http.GetByteArrayAsync(url, ct);
        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(localPath, data, ct);
        _logger.LogInformation("Downloaded media to {Path}", localPath);
    }

    public async Task SendCallSignalAsync(string to, string signalType, object payload, CancellationToken ct = default)
    {
        var msg = JsonSerializer.Serialize(new { type = "call_signal", to, signalType, payload });
        await SafeSendAsync(Encoding.UTF8.GetBytes(msg), ct);
    }

    public async Task SendControlMessageAsync(string command, string payload, CancellationToken ct = default)
    {
        var msg = JsonSerializer.Serialize(new { type = "control", command, payload });
        await SafeSendAsync(Encoding.UTF8.GetBytes(msg), ct);
    }

    /// <summary>Thread-safe WebSocket send with timeout.</summary>
    private async Task SafeSendAsync(byte[] data, CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open) throw new InvalidOperationException("Not connected to Messenger.");
        if (!await _sendLock.WaitAsync(SendTimeout, ct)) return;
        try
        {
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(SendTimeout);
            await _ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, sendCts.Token);
        }
        catch (OperationCanceledException) { /* send timeout or cancel */ }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        const long MaxMessageSize = 10 * 1024 * 1024;
        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, result.Count);
                if (ms.Length > MaxMessageSize)
                {
                    _logger.LogWarning("Messenger message exceeded {Max}MB limit — dropping", MaxMessageSize / 1024 / 1024);
                    break;
                }
            } while (!result.EndOfMessage);
            if (ms.Length > MaxMessageSize) continue;
            var json = Encoding.UTF8.GetString(ms.ToArray());
            if (string.IsNullOrWhiteSpace(json)) continue;
            _logger.LogInformation("[Messenger] Received frame: {Len} bytes", json.Length);
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) continue;
                var msgType = typeProp.GetString();
                switch (msgType)
                {
                    case "chat":
                        if (OnMessageReceived != null)
                            await OnMessageReceived(
                                root.TryGetProperty("from", out var from) ? from.GetString() ?? "" : "",
                                root.TryGetProperty("content", out var content) ? content.GetString() ?? "" : "",
                                root.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "");
                        break;
                    case "media":
                        if (OnMediaReceived != null)
                            await OnMediaReceived(
                                root.TryGetProperty("from", out var mFrom) ? mFrom.GetString() ?? "" : "",
                                root.TryGetProperty("url", out var url) ? url.GetString() ?? "" : "",
                                root.TryGetProperty("mediaType", out var mt) ? mt.GetString() ?? "" : "");
                        break;
                    case "call_signal":
                        if (OnCallSignal != null && root.TryGetProperty("from", out var cFrom) && root.TryGetProperty("payload", out var payload))
                            await OnCallSignal(cFrom.GetString() ?? "", payload);
                        break;
                    case "control_response":
                        if (OnControlResponse != null)
                            await OnControlResponse(root);
                        break;
                }
            }
            catch (Exception ex) { _logger.LogWarning("Failed to parse Messenger message: {Error}", ex.Message); }
        }
    }

    /// <summary>Remove query parameters (secrets) from URL for logging.</summary>
    private static string SanitizeUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "(not configured)";
        try
        {
            var uri = new Uri(url);
            if (string.IsNullOrEmpty(uri.Query)) return url;
            return url.Replace(uri.Query, "?***");
        }
        catch { return "(invalid url)"; }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_ws?.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None); }
            catch { /* WebSocket may already be faulted — disposing anyway */ }
        }
        _ws?.Dispose();
        _http.Dispose();
        _sendLock.Dispose();
        _cts?.Dispose();
    }
}


