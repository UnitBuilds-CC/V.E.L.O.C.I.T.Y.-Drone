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
///   - NMCP shared memory (local, zero-copy, atomic state machine, 100us polling)
///   - WebSocket with NMCP binary frames (remote, with auto-reconnect + heartbeat)
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
    private volatile bool _connected;
    private int _reconnectAttempts;
    private Task? _heartbeatTask;
    private readonly CircuitBreaker _breaker = new(failureThreshold: 5, openTimeout: TimeSpan.FromSeconds(30));

    /// <summary>Lock to serialize concurrent WebSocket sends (heartbeat vs response/notification).</summary>
    private readonly SemaphoreSlim _wsSendLock = new(1, 1);

    /// <summary>Timeout for WebSocket send operations to prevent slow-client hangs.</summary>
    private static readonly TimeSpan WsSendTimeout = TimeSpan.FromSeconds(10);

    // --- Atomic shared memory layout (matches McpServer / V.E.L.O.C.I.T.Y.-MCP) ---
    // Request channel:  [0] = state byte, [1..4] = payload length (int32 LE), [5..4099] = payload (4096 bytes)
    // Response channel: [4100] = state byte, [4101..4104] = payload length (int32 LE), [4105..65535] = payload (61431 bytes)
    private const int ShmemTotalSize = 65536;
    private const int ReqStateOffset = 0;
    private const int ReqLenOffset = 1;
    private const int ReqPayloadOffset = 5;
    private const int ReqPayloadSize = 4096;
    private const int ResStateOffset = 4100;
    private const int ResLenOffset = 4101;
    private const int ResPayloadOffset = 4105;
    private const int ResPayloadSize = ShmemTotalSize - ResPayloadOffset;

    // Atomic state machine
    private const byte StateIdle = 0;
    private const byte StateReqReady = 1;
    private const byte StateProcessing = 2;
    private const byte StateResReady = 3;
    private const byte StateError = 4;

    /// <summary>Polling spin-wait iterations before yielding.</summary>
    private const int ShmemSpinWaitIterations = 30;

    /// <summary>Heartbeat interval in seconds. Sends a heartbeat frame to keep the connection alive.</summary>
    private const int HeartbeatIntervalSec = 30;

    public event Func<JsonElement, Task>? OnRequest;
    public event Func<JsonElement, Task>? OnNotification;
    public bool IsConnected => _connected;

    public VelocityConnection(UplinkConfig config, ILogger logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _reconnectAttempts = 0;

        if (_config.Transport == "shmem" || _config.Transport == "auto")
        {
            try
            {
                ConnectSharedMemory();
                _logger.LogInformation("Connected via NMCP shared memory at {Path} (atomic shmem)", _config.BufferPath);
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

            await ConnectWebSocketAsync();
            _logger.LogInformation("Connected via WebSocket at {Url}", _config.WebSocketUrl);
        }
    }

    public async Task SendResponseAsync(string json, CancellationToken ct = default)
    {
        var payload = Encoding.UTF8.GetBytes(json);

        if (_mmf != null)
            WriteRequestToShmem(payload);
        else if (_ws?.State == WebSocketState.Open)
        {
            var frame = new NmcpFrame(NmcpFrameTypes.JsonRpcResponse, Interlocked.Increment(ref _sequenceId), payload);
            await SafeWsSendAsync(frame.Payload.ToArray(), WebSocketMessageType.Text, ct);
        }
    }

    public async Task SendNotificationAsync(string json, CancellationToken ct = default)
    {
        var payload = Encoding.UTF8.GetBytes(json);

        if (_mmf != null)
            WriteRequestToShmem(payload);
        else if (_ws?.State == WebSocketState.Open)
        {
            var frame = new NmcpFrame(NmcpFrameTypes.JsonRpcNotification, Interlocked.Increment(ref _sequenceId), payload);
            await SafeWsSendAsync(frame.Payload.ToArray(), WebSocketMessageType.Text, ct);
        }
    }

    /// <summary>Thread-safe WebSocket send with timeout — serializes heartbeat and data sends.</summary>
    private async Task SafeWsSendAsync(byte[] data, WebSocketMessageType messageType, CancellationToken ct)
    {
        if (!await _wsSendLock.WaitAsync(WsSendTimeout, ct)) return;
        try
        {
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(WsSendTimeout);
            await _ws!.SendAsync(new ArraySegment<byte>(data), messageType, true, sendCts.Token);
        }
        catch (OperationCanceledException) { /* send timeout or cancel */ }
        finally
        {
            _wsSendLock.Release();
        }
    }

    private void ConnectSharedMemory()
    {
        var fileSize = _config.BufferSize > 0 ? _config.BufferSize : ShmemTotalSize;
        if (File.Exists(_config.BufferPath))
            _mmf = MemoryMappedFile.CreateFromFile(_config.BufferPath, FileMode.Open, "NmcpDroneBuffer", fileSize, MemoryMappedFileAccess.ReadWrite);
        else
            _mmf = MemoryMappedFile.CreateFromFile(_config.BufferPath, FileMode.CreateNew, "NmcpDroneBuffer", fileSize, MemoryMappedFileAccess.ReadWrite);

        _view = _mmf.CreateViewAccessor(0, fileSize);
        // Initialize both channels to IDLE
        _view.Write(ReqStateOffset, StateIdle);
        _view.Write(ResStateOffset, StateIdle);
    }

    /// <summary>
    /// Write a request to shared memory using the atomic state machine protocol.
    /// Client writes payload, sets REQ_READY, then waits for RES_READY.
    /// </summary>
    private void WriteRequestToShmem(byte[] payload)
    {
        if (_view == null) return;
        if (payload.Length > ReqPayloadSize) return; // Payload too large for shmem

        // Wait for IDLE state (server finished previous request)
        var spinWait = new SpinWait();
        while (_view.ReadByte(ReqStateOffset) != StateIdle)
        {
            spinWait.SpinOnce();
            if (spinWait.Count > ShmemSpinWaitIterations * 10)
            {
                // Timeout waiting for server — bail out
                return;
            }
        }

        // Write payload
        _view.Write(ReqLenOffset, payload.Length);
        if (payload.Length > 0)
            _view.WriteArray(ReqPayloadOffset, payload, 0, payload.Length);

        // TOCTOU guard: re-verify state hasn't changed since we checked it
        if (_view.ReadByte(ReqStateOffset) != StateIdle)
            return; // Another writer got in — abort, caller will retry

        // Signal REQ_READY
        _view.Write(ReqStateOffset, StateReqReady);
    }

    private async Task SharedMemoryReadLoop(CancellationToken ct)
    {
        var spinWait = new SpinWait();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_view == null) break;

                // Poll for RES_READY state
                var resState = _view.ReadByte(ResStateOffset);

                if (resState == StateResReady)
                {
                    // Read response payload
                    var resLen = _view.ReadInt32(ResLenOffset);
                    if (resLen > 0 && resLen <= ResPayloadSize)
                    {
                        var buffer = new byte[resLen];
                        _view.ReadArray(ResPayloadOffset, buffer, 0, resLen);

                        var json = Encoding.UTF8.GetString(buffer);

                        try
                        {
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
                        catch (JsonException ex)
                        {
                            _logger.LogError("Shared memory JSON parse error: {Error}", ex.Message);
                        }
                    }

                    // Reset response channel to IDLE
                    _view.Write(ResStateOffset, StateIdle);
                }
                else if (resState == StateError)
                {
                    // Server signaled error — reset and continue
                    _view.Write(ResStateOffset, StateIdle);
                }

                // 100 microsecond polling via spin-wait
                spinWait.SpinOnce();
                if (spinWait.Count > ShmemSpinWaitIterations)
                {
                    await Task.Delay(0, ct);
                    spinWait.Reset();
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError("Shared memory read error: {Error}", ex.Message);
                await Task.Delay(100, ct);
            }
        }
    }

    private async Task ConnectWebSocketAsync()
    {
        _ws = new ClientWebSocket();
        if (!string.IsNullOrEmpty(_config.WebSocketUrl))
        {
            await _ws.ConnectAsync(new Uri(_config.WebSocketUrl), _cts!.Token);
            _connected = true;
            _reconnectAttempts = 0;
            // Use _cts.Token consistently so the read loop respects the linked lifetime
            _ = Task.Run(() => WebSocketReadLoop(_cts.Token));
            // Start heartbeat to keep connection alive and detect dead peers
            StartHeartbeat(_cts.Token);
        }
    }

    private async Task WebSocketReadLoop(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        const long MaxMessageSize = 10 * 1024 * 1024;

        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            try
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _connected = false;
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                    if (ms.Length > MaxMessageSize)
                    {
                        _logger.LogWarning("WebSocket message exceeded {Max}MB limit — dropping", MaxMessageSize / 1024 / 1024);
                        break;
                    }
                } while (!result.EndOfMessage);

                if (ms.Length > MaxMessageSize) continue;

                var json = Encoding.UTF8.GetString(ms.ToArray());
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

        // Auto-reconnect with attempt limit
        if (_config.AutoReconnect && !ct.IsCancellationRequested && _reconnectAttempts < _config.MaxReconnectAttempts)
        {
            _reconnectAttempts++;
            // If circuit breaker is open, wait for recovery before retrying
            if (_breaker.IsOpen)
            {
                _logger.LogWarning("Uplink circuit breaker open. Waiting for recovery...");
                await Task.Delay(30000, ct);
            }
            else
            {
                _logger.LogInformation("Connection lost. Reconnect attempt {Attempt}/{Max}...", _reconnectAttempts, _config.MaxReconnectAttempts);
                await Task.Delay(1000 * _reconnectAttempts, ct);
            }
            try { await ConnectWebSocketAsync(); }
            catch (Exception ex) { _logger.LogWarning("Reconnect failed: {Error}", ex.Message); }
        }
        else if (_reconnectAttempts >= _config.MaxReconnectAttempts)
        {
            _logger.LogError("Max reconnect attempts ({Max}) reached. Giving up.", _config.MaxReconnectAttempts);
        }
    }

    private void StartHeartbeat(CancellationToken ct)
    {
        _heartbeatTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(HeartbeatIntervalSec), ct);
                    if (_ws?.State == WebSocketState.Open)
                    {
                        var heartbeat = new NmcpFrame(NmcpFrameTypes.Heartbeat, Interlocked.Increment(ref _sequenceId), Array.Empty<byte>());
                        var header = new byte[NmcpFrame.HeaderSize];
                        heartbeat.WriteHeader(header);
                        // Use the send lock to prevent concurrent sends with response/notification
                        await SafeWsSendAsync(header, WebSocketMessageType.Binary, ct);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning("Heartbeat failed: {Error}", ex.Message);
                    break;
                }
            }
        }, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        // Wait for heartbeat to finish before disposing WebSocket
        if (_heartbeatTask != null)
        {
            try { await _heartbeatTask; } catch { /* heartbeat cancelled during disposal — expected */ }
        }
        if (_ws?.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None); }
            catch { /* WebSocket may already be faulted — disposing anyway */ }
        }
        _ws?.Dispose();
        _view?.Dispose();
        _mmf?.Dispose();
        _wsSendLock.Dispose();
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
