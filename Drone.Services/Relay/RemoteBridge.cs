using global::System.Collections.Concurrent;
using global::System.Net.WebSockets;
using global::System.Text;
using Drone.Core;
using Drone.Core.Config;

namespace Drone.Services.Relay;

/// <summary>
/// Bridges NMCP binary frames between two drones for remote control.
/// One drone connects as "controller" (sends input, receives screen),
/// the other as "target" (receives input, sends screen).
/// </summary>
public class RemoteBridge : IAsyncDisposable
{
    private readonly RelayConfig _config;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, WebSocket> _targets = new();
    private readonly ConcurrentDictionary<string, WebSocket> _controllers = new();
    private CancellationTokenSource? _cts;

    public int TargetCount => _targets.Count;
    public int ControllerCount => _controllers.Count;

    public RemoteBridge(RelayConfig config, ILogger logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task HandleConnection(string droneId, string role, string? target, WebSocket ws, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        if (role == "target")
        {
            _targets.TryAdd(droneId, ws);
            _logger.LogInformation("Remote bridge: {DroneId} registered as target", droneId);
        }
        else
        {
            _controllers.TryAdd(droneId, ws);
            _logger.LogInformation("Remote bridge: {DroneId} connected as controller (target: {Target})", droneId, target ?? "any");
        }

        var buffer = new byte[65536]; // 64KB for screen frames
        try
        {
            while (ws.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye", _cts.Token);
                    break;
                }

                if (result.EndOfMessage)
                {
                    if (role == "controller" && !string.IsNullOrEmpty(target))
                    {
                        // Forward from controller to target
                        await ForwardToTarget(target, buffer, result.Count, result.MessageType, _cts.Token);
                    }
                    else if (role == "target")
                    {
                        // Forward from target to all controllers targeting this drone
                        await ForwardToControllers(droneId, buffer, result.Count, result.MessageType, _cts.Token);
                    }
                }
            }
        }
        catch (WebSocketException) { /* client disconnected */ }
        catch (OperationCanceledException) { /* shutdown */ }
        finally
        {
            if (role == "target")
                _targets.TryRemove(droneId, out _);
            else
                _controllers.TryRemove(droneId, out _);

            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye", CancellationToken.None); }
                catch { /* ignore */ }
            }
            ws.Dispose();
        }
    }

    private async Task ForwardToTarget(string targetId, byte[] buffer, int count, WebSocketMessageType msgType, CancellationToken ct)
    {
        if (_targets.TryGetValue(targetId, out var targetWs) && targetWs.State == WebSocketState.Open)
        {
            await targetWs.SendAsync(new ArraySegment<byte>(buffer, 0, count), msgType, true, ct);
        }
        else
        {
            _logger.LogWarning("Remote bridge: target {Target} not connected", targetId);
        }
    }

    private async Task ForwardToControllers(string targetId, byte[] buffer, int count, WebSocketMessageType msgType, CancellationToken ct)
    {
        // Forward to all controllers (in a real implementation, we'd filter by which controller targets this drone)
        foreach (var (controllerId, ws) in _controllers)
        {
            if (ws.State == WebSocketState.Open)
            {
                try
                {
                    await ws.SendAsync(new ArraySegment<byte>(buffer, 0, count), msgType, true, ct);
                }
                catch { /* controller disconnected */ }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();

        foreach (var (_, ws) in _targets)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bridge shutting down", CancellationToken.None); }
            catch { /* ignore */ }
            ws.Dispose();
        }

        foreach (var (_, ws) in _controllers)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bridge shutting down", CancellationToken.None); }
            catch { /* ignore */ }
            ws.Dispose();
        }

        _targets.Clear();
        _controllers.Clear();
        _cts?.Dispose();
    }
}
