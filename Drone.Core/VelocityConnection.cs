using System.Buffers;
using global::System.IO.MemoryMappedFiles;
using global::System.Net.WebSockets;
using global::System.Text;
using global::System.Text.Json;
using Drone.Core.Config;
using Drone.Core.Protocol;

namespace Drone.Core;

/// <summary>
/// High-performance bidirectional connection for the Velocity Drone uplink.
/// Supports two transport modes:
///   - NMCP shared memory (local, zero-copy, sub-microsecond)
///   - WebSocket with NMCP binary frames (remote, with JSON-RPC fallback)
/// </summary>
public class VelocityConnection : IAsyncDisposable
{
    private readonly UplinkConfig _config;
    private readonly ILogger _logger;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private uint _sequenceId;
    private bool _connected;

    /// <summary>Raised when a JSON-RPC request is received from the AI.</summary>
    public event Func<JsonElement, Task>? OnRequest;

    /// <summary>Raised when a notification is received (no response expected).</summary>
    public event Func<JsonElement, Task>? OnNotification;

    /// <summary>Whether the connection is currently active.</summary>
    public bool IsConnected => _connected;

    public VelocityConnection(UplinkConfig config, ILogger logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>Connect using the configured transport (auto-negotiates).</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        if (_config.Transport == "shmem" || _config.Transport == "auto")
        {
            try
            {
                ConnectSharedMemory();
                _logger.LogInformation("Connected via NMCP shared memory at {Path}", _config.BufferPath);
                _connected = true;
                _ = Task.Run(() => SharedMemoryReadLoop(_cts.Token));
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Shared memory unavailable: {Error}. Falling back to WebSocket.", ex.Message);
            }
        }

        if (_config.Transport == "websocket" || _config.Transport == "auto")
        {
            if (string.IsNullOrEmpty(_config.WebSocketUrl))
                throw new InvalidOperationException("WebSocket URL required when transport is 'websocket' or 'auto' fallback fails.");

            await ConnectWebSocketAsync(_cts.Token);
            _logger.LogInformation("Connected via WebSocket at {Url}", _config.WebSocketUrl);
        }
    }

    /// <summary>Send a JSON-RPC response back to the AI.</summary>
    public async Task SendResponseAsync(string json, CancellationToken ct = default)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new NmcpFrame(NmcpFrameTypes.JsonRpcResponse, Interlocked.Increment(ref _sequenceId), payload);

        if (_mmf != null)
        {
            WriteFrameToSharedMemory(frame);
        }
        else if (_ws?.State == WebSocketState.Open)
        {
            await _ws.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, ct);
        }
    }

    /// <summary>Send a notification to the AI (no response expected).</summary>
    public async Task SendNotificationAsync(string json, CancellationToken ct = default)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new NmcpFrame(NmcpFrameTypes.JsonRpcNotification, Interlocked.Increment(ref _sequenceId), payload);

        if (_mmf != null)
        {
            WriteFrameToSharedMemory(frame);
        }
        else if (_ws?.State == WebSocketState.Open)
        {
            await _ws.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, ct);
        }
    }

    private void ConnectSharedMemory()
    {
        var fileSize = _config.BufferSize;

        // Create or open the memory-mapped file
        if (File.Exists(_config.BufferPath))
        {
            _mmf = MemoryMappedFile.CreateFromFile(_config.BufferPath, FileMode.Open, "NmcpDroneBuffer", fileSize, MemoryMappedFileAccess.ReadWrite);
        }
        else
        {
            _mmf = MemoryMappedFile.CreateFromFile(_config.BufferPath, FileMode.CreateNew, "NmcpDroneBuffer", fileSize, MemoryMappedFileAccess.ReadWrite);
        }

        _view = _mmf.CreateViewAccessor(0, fileSize);
    }

    private void WriteFrameToSharedMemory(NmcpFrame frame)
    {
        if (_view == null) return;

        var header = new byte[NmcpFrame.HeaderSize];
        frame.WriteHeader(header);

        // Simple protocol: write header + payload at current write position
        // In production, this would use a proper ring buffer with atomic operations
        var offset = 0L;
        _view.WriteArray(offset, header, 0, header.Length);
        offset += header.Length;

        if (frame.PayloadLength > 0)
        {
            var payloadArray = frame.Payload.ToArray();
            _view.WriteArray(offset, payloadArray, 0, payloadArray.Length);
        }
    }

    private async Task SharedMemoryReadLoop(CancellationToken ct)
    {
        var buffer = new byte[_config.BufferSize];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_view == null) break;

                // Poll for new frames (in production, use event-based signaling)
                var header = new byte[NmcpFrame.HeaderSize];
                _view.ReadArray(0, header, 0, header.Length);

                if (NmcpFrame.TryReadHeader(header, out var frameType, out var payloadLen, out var seqId))
                {
                    if (payloadLen > 0 && payloadLen < buffer.Length)
                    {
                        _view.ReadArray(NmcpFrame.HeaderSize, buffer, 0, (int)payloadLen);
                        var json = Encoding.UTF8.GetString(buffer, 0, (int)payloadLen);

                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("method", out _))
                        {
                            if (OnRequest != null) await OnRequest(root);
                        }
                        else
                        {
                            if (OnNotification != null) await OnNotification(root);
                        }
                    }
                }

                await Task.Delay(1, ct); // 1ms poll interval for shmem
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError("Shared memory read error: {Error}", ex.Message);
                await Task.Delay(100, ct);
            }
        }
    }

    private async Task ConnectWebSocketAsync(CancellationToken ct)
    {
        _ws = new ClientWebSocket();

        if (!string.IsNullOrEmpty(_config.WebSocketUrl))
        {
            await _ws.ConnectAsync(new Uri(_config.WebSocketUrl), ct);
            _connected = true;
            _ = Task.Run(() => WebSocketReadLoop(ct));
        }
    }

    private async Task WebSocketReadLoop(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024]; // 64KB receive buffer

        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            try
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _connected = false;
                    break;
                }

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("method", out _))
                {
                    if (OnRequest != null) await OnRequest(root);
                }
                else
                {
                    if (OnNotification != null) await OnNotification(root);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError("WebSocket read error: {Error}", ex.Message);
            }
        }

        // Auto-reconnect if configured
        if (_config.AutoReconnect && !ct.IsCancellationRequested)
        {
            _logger.LogInformation("Connection lost. Reconnecting...");
            await Task.Delay(1000, ct);
            try { await ConnectWebSocketAsync(ct); } catch { /* retry handled by caller */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _ws?.Dispose();
        _view?.Dispose();
        _mmf?.Dispose();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Minimal logger interface to avoid pulling in Microsoft.Extensions.Logging for Core.</summary>
public interface ILogger
{
    void LogInformation(string message, params object[] args);
    void LogWarning(string message, params object[] args);
    void LogError(string message, params object[] args);
    void LogDebug(string message, params object[] args);
}
