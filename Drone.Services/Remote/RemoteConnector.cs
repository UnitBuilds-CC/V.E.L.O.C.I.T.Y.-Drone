using global::System.Net.WebSockets;
using global::System.Text;
using global::System.Text.Json;
using Drone.Core;
using Drone.Core.Config;

namespace Drone.Services.Remote;

public class RemoteConnector : IAsyncDisposable
{
    private readonly RemoteConfig _config;
    private readonly ILogger _logger;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;

    public event Func<byte[], Task>? OnScreenFrame;
    public event Func<RemoteHost[], Task>? OnHostsUpdated;
    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public RemoteConnector(RemoteConfig config, ILogger logger) { _config = config; _logger = logger; }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_config.ServerUrl)) throw new InvalidOperationException("Remote ServerUrl is required.");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("X-Api-Key", _config.ApiKey ?? "");
        await _ws.ConnectAsync(new Uri(_config.ServerUrl), _cts.Token);
        _logger.LogInformation("Connected to Remote server at " + _config.ServerUrl);
        _ = Task.Run(() => ReceiveLoop(_cts.Token));
    }

    public async Task SendInputAsync(string inputType, object data, CancellationToken ct = default)
    { var msg = JsonSerializer.Serialize(new { type = "input", inputType, data }); await SendAsync(msg, ct); }

    public async Task RequestScreenAsync(int quality = 80, int maxWidth = 1920, CancellationToken ct = default)
    { var msg = JsonSerializer.Serialize(new { type = "screen_request", quality, maxWidth }); await SendAsync(msg, ct); }

    public async Task QueryHostsAsync(CancellationToken ct = default)
    { var msg = JsonSerializer.Serialize(new { type = "query_hosts" }); await SendAsync(msg, ct); }

    public async Task QueryAddressBookAsync(CancellationToken ct = default)
    { var msg = JsonSerializer.Serialize(new { type = "query_address_book" }); await SendAsync(msg, ct); }

    private async Task SendAsync(string json, CancellationToken ct)
    { if (_ws?.State != WebSocketState.Open) throw new InvalidOperationException("Not connected."); await _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct); }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[4 * 1024 * 1024];
        using var ms = new MemoryStream();
        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            try
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do { result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct); if (result.MessageType == WebSocketMessageType.Close) return; ms.Write(buffer, 0, result.Count); } while (!result.EndOfMessage);
                if (result.MessageType == WebSocketMessageType.Binary) { if (OnScreenFrame != null) await OnScreenFrame(ms.ToArray()); }
                else if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(ms.ToArray());
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var msgType = root.GetProperty("type").GetString();
                    if (msgType == "hosts" && OnHostsUpdated != null) { var hosts = JsonSerializer.Deserialize<RemoteHost[]>(root.GetProperty("hosts").GetRawText()) ?? []; await OnHostsUpdated(hosts); }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning("Remote receive error: " + ex.Message); }
        }
    }

    public async ValueTask DisposeAsync()
    { _cts?.Cancel(); if (_ws?.State == WebSocketState.Open) { try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None); } catch { } } _ws?.Dispose(); _cts?.Dispose(); }
}

public record RemoteHost(string Id, string Name, string Address, bool IsOnline, string? Platform);
