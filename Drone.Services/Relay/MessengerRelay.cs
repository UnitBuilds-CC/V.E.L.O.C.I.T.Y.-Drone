using global::System.Collections.Concurrent;
using global::System.Net.WebSockets;
using global::System.Text;
using global::System.Text.Json;
using Drone.Core;
using Drone.Core.Config;

namespace Drone.Services.Relay;

/// <summary>
/// WebSocket-based message relay that routes messages between connected drones.
/// Drones connect with ?username=X and send JSON messages to be routed to recipients.
/// </summary>
public class MessengerRelay : IAsyncDisposable
{
    private readonly RelayConfig _config;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _clientCts = new();
    private readonly ConcurrentDictionary<string, TokenBucket> _rateLimiters = new();
    private Task? _heartbeatTask;

    private const int HeartbeatIntervalSec = 30;
    private const int MaxMessageSize = 256 * 1024; // 256 KB max message size
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromMilliseconds(500);

    public int ClientCount => _clients.Count;
    public IReadOnlyCollection<string> ConnectedClients => _clients.Keys.ToList().AsReadOnly();

    public MessengerRelay(RelayConfig config, ILogger logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task HandleConnection(string username, WebSocket ws, CancellationToken ct)
    {
        var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Evict existing connection with same username (prevents ghost connections)
        if (_clientCts.TryRemove(username, out var oldCts))
        {
            _logger.LogInformation("Messenger relay: {Username} reconnected — evicting old session", username);
            try { oldCts.Cancel(); } catch (ObjectDisposedException) { }
            oldCts.Dispose();
        }
        if (_clients.TryRemove(username, out var oldWs))
        {
            try
            {
                await oldWs.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "Replaced by new connection", CancellationToken.None);
            }
            catch { /* old connection may already be gone */ }
            oldWs.Dispose();
        }

        _clients.TryAdd(username, ws);
        _clientCts.TryAdd(username, connectionCts);

        // Start heartbeat if not already running
        if (_heartbeatTask == null || _heartbeatTask.IsCompleted)
        {
            _heartbeatTask = Task.Run(() => HeartbeatLoop(connectionCts.Token));
        }

        var chunkBuffer = new byte[16384];
        using var messageBuffer = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open && !connectionCts.Token.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(chunkBuffer), connectionCts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye", connectionCts.Token);
                    break;
                }

                // Accumulate fragments until EndOfMessage
                messageBuffer.Write(chunkBuffer, 0, result.Count);

                if (messageBuffer.Length > MaxMessageSize)
                {
                    _logger.LogWarning("Messenger relay: {Username} sent message exceeding {Max} bytes — dropping", username, MaxMessageSize);
                    messageBuffer.SetLength(0);
                    continue;
                }

                if (result.EndOfMessage)
                {
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var message = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
                        await RouteMessage(username, message, connectionCts.Token);
                    }
                    messageBuffer.SetLength(0);
                }
            }
        }
        catch (WebSocketException) { /* client disconnected */ }
        catch (OperationCanceledException) { /* shutdown */ }
        finally
        {
            _clients.TryRemove(username, out _);
            _clientCts.TryRemove(username, out var myCts);
            _rateLimiters.TryRemove(username, out _);
            myCts?.Dispose();
            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye", CancellationToken.None); }
                catch { /* ignore */ }
            }
            ws.Dispose();
            connectionCts.Dispose();
        }
    }

    private async Task RouteMessage(string fromUsername, string messageJson, CancellationToken ct)
    {
        // Rate limit check
        if (_config.MaxMessagesPerSecond > 0)
        {
            var bucket = _rateLimiters.GetOrAdd(fromUsername, _ => new TokenBucket(_config.MaxMessagesPerSecond, _config.MaxMessagesPerSecond));
            if (!bucket.TryConsume())
            {
                _logger.LogWarning("Messenger relay: {Username} rate limited (>{Limit} msg/s)", fromUsername, _config.MaxMessagesPerSecond);
                await SendToSender(fromUsername, new { type = "error", error = "rate_limited", retryAfterMs = 1000.0 / _config.MaxMessagesPerSecond }, ct);
                return;
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(messageJson);
            var root = doc.RootElement;
            var type = root.GetPropertyOrDefault("type", "direct");
            var to = root.GetPropertyOrDefault("to", "");

            if (type == "direct" && !string.IsNullOrEmpty(to))
            {
                // Direct message to specific drone
                if (_clients.TryGetValue(to, out var targetWs) && targetWs.State == WebSocketState.Open)
                {
                    var envelope = JsonSerializer.Serialize(new
                    {
                        type = "message",
                        from = fromUsername,
                        content = root.TryGetProperty("content", out var c) ? c : JsonDocument.Parse("{}").RootElement
                    });
                    var bytes = Encoding.UTF8.GetBytes(envelope);
                    await targetWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
                }
                else
                {
                    // Recipient not found — send error back to sender
                    await SendToSender(fromUsername, new { type = "error", error = "recipient_not_found", to }, ct);
                }
            }
            else if (type == "broadcast")
            {
                // Broadcast to all connected drones except sender
                var envelope = JsonSerializer.Serialize(new
                {
                    type = "broadcast",
                    from = fromUsername,
                    content = root.TryGetProperty("content", out var c) ? c : JsonDocument.Parse("{}").RootElement
                });
                var bytes = Encoding.UTF8.GetBytes(envelope);
                foreach (var (name, ws) in _clients)
                {
                    if (name != fromUsername && ws.State == WebSocketState.Open)
                    {
                        try { await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct); }
                        catch { /* skip failed sends */ }
                    }
                }
            }
            else if (type == "contacts")
            {
                // Return list of connected drones
                var contacts = _clients.Keys.Where(k => k != fromUsername).ToArray();
                await SendToSender(fromUsername, new { type = "contacts", contacts }, ct);
            }
            else if (type == "ping")
            {
                await SendToSender(fromUsername, new { type = "pong", timestamp = DateTime.UtcNow }, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Messenger relay route error from {From}: {Error}", fromUsername, ex.Message);
        }
    }

    private async Task SendToSender(string username, object message, CancellationToken ct)
    {
        if (_clients.TryGetValue(username, out var ws) && ws.State == WebSocketState.Open)
        {
            var json = JsonSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }
    }

    private async Task HeartbeatLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(HeartbeatIntervalSec), ct);
            var ping = JsonSerializer.Serialize(new { type = "ping" });
            var bytes = Encoding.UTF8.GetBytes(ping);

            foreach (var (name, ws) in _clients)
            {
                if (ws.State == WebSocketState.Open)
                {
                    try { await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct); }
                    catch { /* client gone */ }
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, cts) in _clientCts)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            cts.Dispose();
        }
        _clientCts.Clear();

        foreach (var (_, ws) in _clients)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutting down", CancellationToken.None); }
            catch { /* ignore */ }
            ws.Dispose();
        }
        _clients.Clear();
    }
}

internal static class JsonElementExtensions
{
    public static string GetPropertyOrDefault(this JsonElement element, string propertyName, string defaultValue)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? defaultValue
            : defaultValue;
    }
}

/// <summary>
/// Thread-safe token bucket rate limiter.
/// Tokens refill at a fixed rate; each message consumes one token.
/// </summary>
internal sealed class TokenBucket
{
    private readonly double _refillRate; // tokens per second
    private readonly int _maxTokens;
    private double _tokens;
    private long _lastRefillTicks;
    private readonly object _lock = new();

    public TokenBucket(int refillRatePerSecond, int maxTokens)
    {
        _refillRate = refillRatePerSecond;
        _maxTokens = maxTokens;
        _tokens = maxTokens;
        _lastRefillTicks = DateTime.UtcNow.Ticks;
    }

    public bool TryConsume()
    {
        lock (_lock)
        {
            Refill();
            if (_tokens >= 1.0)
            {
                _tokens -= 1.0;
                return true;
            }
            return false;
        }
    }

    private void Refill()
    {
        var now = DateTime.UtcNow.Ticks;
        var elapsed = (now - _lastRefillTicks) / (double)TimeSpan.TicksPerSecond;
        _tokens = Math.Min(_maxTokens, _tokens + elapsed * _refillRate);
        _lastRefillTicks = now;
    }
}
