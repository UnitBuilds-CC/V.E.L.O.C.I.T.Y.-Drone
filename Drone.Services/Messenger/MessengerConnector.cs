using global::System.Net.WebSockets;
using global::System.Text;
using global::System.Text.Json;
using Drone.Core;
using Drone.Core.Config;

namespace Drone.Services.Messenger;

public class MessengerConnector : IAsyncDisposable
{
    private readonly MessengerConfig _config;
    private readonly ILogger _logger;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private bool _connected;
    private int _reconnectAttempts;

    public event Func<string, string, string, Task>? OnMessageReceived;
    public event Func<string, string, string, Task>? OnMediaReceived;
    public event Func<string, JsonElement, Task>? OnCallSignal;
    public event Func<bool, Task>? OnConnectionChanged;
    public event Func<JsonElement, Task>? OnControlResponse;

    public bool IsConnected => _connected;

    public MessengerConnector(MessengerConfig config, ILogger logger) { _config = config; _logger = logger; }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_config.ServerUrl)) throw new InvalidOperationException("Messenger ServerUrl is required.");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                _ws = new ClientWebSocket();
                await _ws.ConnectAsync(new Uri(_config.ServerUrl), _cts.Token);
                var auth = JsonSerializer.Serialize(new { type = "auth", username = "Drone", secret = _config.ConnectionSecret });
                await _ws.SendAsync(Encoding.UTF8.GetBytes(auth), WebSocketMessageType.Text, true, _cts.Token);
                _connected = true; _reconnectAttempts = 0;
                _logger.LogInformation("Connected to Messenger at " + _config.ServerUrl);
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
                var delay = Math.Min(1000 * Math.Pow(2, _reconnectAttempts), 30000);
                _logger.LogWarning("Messenger disconnected. Reconnect attempt " + _reconnectAttempts + " in " + (int)delay + "ms. Error: " + ex.Message);
                await Task.Delay((int)delay, _cts.Token);
            }
        }
    }

    public async Task SendMessageAsync(string to, string content, CancellationToken ct = default)
    {
        var msg = JsonSerializer.Serialize(new { type = "message", to, content });
        await SendAsync(msg, ct);
    }

    public async Task SendGroupMessageAsync(string groupId, string content, CancellationToken ct = default)
    {
        var msg = JsonSerializer.Serialize(new { type = "group_message", group = groupId, content });
        await SendAsync(msg, ct);
    }

    public async Task<string> UploadMediaAsync(string filePath, string mediaType, CancellationToken ct = default)
    {
        var fileBytes = await File.ReadAllBytesAsync(filePath, ct);
        var base64 = Convert.ToBase64String(fileBytes);
        var fileName = Path.GetFileName(filePath);
        var msg = JsonSerializer.Serialize(new { type = "media_upload", fileName, mediaType, data = base64 });
        await SendAsync(msg, ct);
        return fileName;
    }

    public async Task DownloadMediaAsync(string url, string localPath, CancellationToken ct = default)
    {
        using var http = new HttpClient();
        var data = await http.GetByteArrayAsync(url, ct);
        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(localPath, data, ct);
        _logger.LogInformation("Downloaded media to " + localPath);
    }

    public async Task SendCallSignalAsync(string to, string signalType, object payload, CancellationToken ct = default)
    {
        var msg = JsonSerializer.Serialize(new { type = "call_signal", to, signalType, payload });
        await SendAsync(msg, ct);
    }

    public async Task SendControlMessageAsync(string command, string payload, CancellationToken ct = default)
    {
        var msg = JsonSerializer.Serialize(new { type = "control", command, payload });
        await SendAsync(msg, ct);
    }

    private async Task SendAsync(string json, CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open) throw new InvalidOperationException("Not connected to Messenger.");
        await _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            var json = Encoding.UTF8.GetString(ms.ToArray());
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var msgType = root.GetProperty("type").GetString();
                switch (msgType)
                {
                    case "message":
                        if (OnMessageReceived != null)
                            await OnMessageReceived(
                                root.GetProperty("from").GetString() ?? "",
                                root.GetProperty("content").GetString() ?? "",
                                root.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "");
                        break;
                    case "media":
                        if (OnMediaReceived != null)
                            await OnMediaReceived(
                                root.GetProperty("from").GetString() ?? "",
                                root.GetProperty("url").GetString() ?? "",
                                root.TryGetProperty("mediaType", out var mt) ? mt.GetString() ?? "" : "");
                        break;
                    case "call_signal":
                        if (OnCallSignal != null)
                            await OnCallSignal(
                                root.GetProperty("from").GetString() ?? "",
                                root.GetProperty("payload"));
                        break;
                    case "control_response":
                        if (OnControlResponse != null)
                            await OnControlResponse(root);
                        break;
                }
            }
            catch (Exception ex) { _logger.LogWarning("Failed to parse Messenger message: " + ex.Message); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_ws?.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None); }
            catch { }
        }
        _ws?.Dispose();
        _cts?.Dispose();
    }
}
