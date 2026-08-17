using global::System.Buffers.Binary;
using global::System.Net.WebSockets;
using global::System.Text;
using Drone.Core;
using Drone.Core.Config;
using Drone.Core.Custody;
using Drone.Core.Protocol;

namespace Drone.Services.Remote;

/// <summary>
/// Remote connector using NMCP binary frames with NDA-encoded payloads.
/// No JSON. All data encoded as semantic triples (Subject-Predicate-Object).
/// Custody-aware: logs tool calls and connections to the custody trail.
/// </summary>
public class RemoteConnector : IAsyncDisposable
{
    private readonly RemoteConfig _config;
    private readonly ILogger _logger;
    private readonly CustodyAuditLogger? _custody;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private uint _sequenceId;
    private volatile bool _connected;
    private int _reconnectAttempts;
    private const int MaxReconnectAttempts = 10;
    private const int HeartbeatIntervalSec = 30;
    private Task? _heartbeatTask;
    private readonly CircuitBreaker _breaker = new(failureThreshold: 5, openTimeout: TimeSpan.FromSeconds(30));

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    public event Func<byte[], Task>? OnScreenFrame;
    public event Func<RemoteHost[], Task>? OnHostsUpdated;
    public event Func<NdaPayload, Task>? OnRequest;
    public event Func<string, byte[], uint, Task<byte[]>>? OnToolCall;
    public bool IsConnected => _connected;

    public RemoteConnector(RemoteConfig config, ILogger logger, CustodyAuditLogger? custody = null)
    {
        _config = config;
        _logger = logger;
        _custody = custody;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_config.ServerUrl)) throw new InvalidOperationException("Remote ServerUrl is required.");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _reconnectAttempts = 0;

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                _ws = new ClientWebSocket();
                if (!string.IsNullOrEmpty(_config.ApiKey))
                    _ws.Options.SetRequestHeader("X-Api-Key", _config.ApiKey);
                await _ws.ConnectAsync(new Uri(_config.ServerUrl), _cts.Token);
                _connected = true;
                _reconnectAttempts = 0;
                _logger.LogInformation("Remote NMCP/NDA connected at {Url}", _config.ServerUrl);
                _custody?.LogConnection("remote_connected", "remote", _config.ServerUrl, success: true);

                await SendHandshakeAsync(_cts.Token);
                StartHeartbeat(_cts.Token);
                await ReceiveLoop(_cts.Token);
                _connected = false;
                _custody?.LogConnection("remote_disconnected", "remote", _config.ServerUrl, success: false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _connected = false;
                // If circuit breaker is open, wait for recovery before retrying
                if (_breaker.IsOpen)
                {
                    _logger.LogWarning("Remote circuit breaker open. Waiting for recovery...");
                    await Task.Delay(30000, _cts.Token);
                    continue;
                }
                if (!_cts.Token.IsCancellationRequested && _reconnectAttempts < MaxReconnectAttempts)
                {
                    _reconnectAttempts++;
                    var delay = Math.Min(1000 * Math.Pow(2, _reconnectAttempts), 30000);
                    _logger.LogWarning("Remote NMCP disconnected. Reconnect {Attempt}/{Max} in {Delay}ms. Error: {Error}",
                        _reconnectAttempts, MaxReconnectAttempts, (int)delay, ex.Message);
                    await Task.Delay((int)delay, _cts.Token);
                }
                else
                {
                    _logger.LogError("Remote max reconnect ({Max}) reached. Error: {Error}", MaxReconnectAttempts, ex.Message);
                    break;
                }
            }
        }
    }

    /// <summary>Send NMCP Handshake frame with NDA-encoded identity triples.</summary>
    private async Task SendHandshakeAsync(CancellationToken ct)
    {
        var nda = new NdaPayload();
        nda.Triples.Add(new NdaTriple("drone", "id", Environment.MachineName));
        nda.Triples.Add(new NdaTriple("drone", "platform", "windows"));
        nda.Triples.Add(new NdaTriple("drone", "version", "3.0.0"));
        nda.Triples.Add(new NdaTriple("drone", "protocol", "NMCP/NDA"));

        var frame = new NmcpFrame(NmcpFrameTypes.Handshake, Interlocked.Increment(ref _sequenceId), nda.Encode());
        await SendFrameAsync(frame, ct);
    }

    /// <summary>Send a screen capture frame (raw binary in NDA raw data section).</summary>
    public async Task SendScreenFrameAsync(byte[] frameData, CancellationToken ct = default)
    {
        var nda = new NdaPayload();
        nda.Triples.Add(new NdaTriple("screen", "type", "capture"));
        nda.Triples.Add(new NdaTriple("screen", "format", "bmp"));
        nda.RawData = frameData;

        var frame = new NmcpFrame(NmcpFrameTypes.ScreenCapture, Interlocked.Increment(ref _sequenceId), nda.Encode());
        await SendFrameAsync(frame, ct);
    }


    /// <summary>Send a delta frame (motion-compensated dirty rects) via NDA triples.</summary>
    public async Task SendDeltaFrameAsync(byte[] deltaFrameData, int rectCount, int rawPixelBytes, CancellationToken ct = default)
    {
        var nda = new NdaPayload();
        nda.Triples.Add(new NdaTriple("screen", "type", "delta"));
        nda.Triples.Add(new NdaTriple("screen", "format", "bgra-delta"));
        nda.Triples.Add(new NdaTriple("screen", "rects", rectCount.ToString()));
        nda.Triples.Add(new NdaTriple("screen", "raw_bytes", rawPixelBytes.ToString()));
        nda.Triples.Add(new NdaTriple("screen", "frame_bytes", deltaFrameData.Length.ToString()));
        nda.RawData = deltaFrameData;

        var frame = new NmcpFrame(NmcpFrameTypes.ScreenCapture, Interlocked.Increment(ref _sequenceId), nda.Encode());
        await SendFrameAsync(frame, ct);
    }
    /// <summary>Request screen stream from remote controller via NDA triples.</summary>
    public async Task RequestScreenAsync(int quality = 80, int maxWidth = 1920, CancellationToken ct = default)
    {
        var nda = new NdaPayload();
        nda.Triples.Add(new NdaTriple("screen", "request", "start"));
        nda.Triples.Add(new NdaTriple("screen", "quality", quality.ToString()));
        nda.Triples.Add(new NdaTriple("screen", "maxWidth", maxWidth.ToString()));

        var frame = new NmcpFrame(NmcpFrameTypes.JsonRpcRequest, Interlocked.Increment(ref _sequenceId), nda.Encode());
        await SendFrameAsync(frame, ct);
    }

    /// <summary>Send an input event frame with NDA-encoded triples.</summary>
    public async Task SendInputAsync(string inputType, object data, CancellationToken ct = default)
    {
        var nda = new NdaPayload();
        nda.Triples.Add(new NdaTriple("input", "type", inputType));
        nda.Triples.Add(new NdaTriple("input", "data", data?.ToString() ?? ""));

        var frame = new NmcpFrame(NmcpFrameTypes.InputEvent, Interlocked.Increment(ref _sequenceId), nda.Encode());
        await SendFrameAsync(frame, ct);
    }

    /// <summary>Send system metrics as NDA triples in NMCP frame.</summary>
    public async Task SendSystemMetricsAsync(object metrics, CancellationToken ct = default)
    {
        var nda = new NdaPayload();
        nda.Triples.Add(new NdaTriple("metrics", "type", "system"));
        nda.Triples.Add(new NdaTriple("metrics", "data", metrics?.ToString() ?? ""));

        var frame = new NmcpFrame(NmcpFrameTypes.SystemMetrics, Interlocked.Increment(ref _sequenceId), nda.Encode());
        await SendFrameAsync(frame, ct);
    }

    /// <summary>Query available remote hosts via NMCP request with NDA payload.</summary>
    public async Task QueryHostsAsync(CancellationToken ct = default)
    {
        var nda = NdaPayload.SingleTriple("query", "method", "hosts");
        var frame = new NmcpFrame(NmcpFrameTypes.JsonRpcRequest, Interlocked.Increment(ref _sequenceId), nda);
        await SendFrameAsync(frame, ct);
    }

    /// <summary>Query address book via NMCP request with NDA payload.</summary>
    public async Task QueryAddressBookAsync(CancellationToken ct = default)
    {
        var nda = NdaPayload.SingleTriple("query", "method", "address_book");
        var frame = new NmcpFrame(NmcpFrameTypes.JsonRpcRequest, Interlocked.Increment(ref _sequenceId), nda);
        await SendFrameAsync(frame, ct);
    }

    /// <summary>Send raw binary data (for relay passthrough).</summary>
    public async Task SendRawBinaryAsync(byte[] data, CancellationToken ct = default)
    {
        await SafeWsSendAsync(data, WebSocketMessageType.Binary, ct);
    }

    /// <summary>Send a complete NMCP frame over WebSocket.</summary>
    private async Task SendFrameAsync(NmcpFrame frame, CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open) throw new InvalidOperationException("Not connected.");
        if (!await _sendLock.WaitAsync(SendTimeout, ct)) return;
        try
        {
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(SendTimeout);

            var header = new byte[NmcpFrame.HeaderSize];
            frame.WriteHeader(header);
            var payloadArray = frame.Payload.ToArray();
            var fullFrame = new byte[NmcpFrame.HeaderSize + payloadArray.Length];
            Buffer.BlockCopy(header, 0, fullFrame, 0, NmcpFrame.HeaderSize);
            if (payloadArray.Length > 0)
                Buffer.BlockCopy(payloadArray, 0, fullFrame, NmcpFrame.HeaderSize, payloadArray.Length);

            await _ws!.SendAsync(new ArraySegment<byte>(fullFrame), WebSocketMessageType.Binary, true, sendCts.Token);
        }
        catch (OperationCanceledException) { }
        finally { _sendLock.Release(); }
    }

    /// <summary>NMCP frame receive loop - reads binary frames, decodes NDA payloads.</summary>
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
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var data = ms.ToArray();
                if (data.Length < NmcpFrame.HeaderSize || result.MessageType != WebSocketMessageType.Binary) continue;

                if (!NmcpFrame.TryReadHeader(data.AsSpan(0, NmcpFrame.HeaderSize), out var frameType, out var payloadLen, out var seqId)) continue;
                var payload = data.Length > NmcpFrame.HeaderSize ? data[NmcpFrame.HeaderSize..] : Array.Empty<byte>();

                await HandleNmcpFrame(frameType, payload);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning("Remote NMCP receive error: {Error}", ex.Message); break; }
        }
    }

    /// <summary>Dispatch NMCP frame by type, decode NDA payload.</summary>
    private async Task HandleNmcpFrame(uint frameType, byte[] payload)
    {
        switch (frameType)
        {
            case var t when t == NmcpFrameTypes.ScreenCapture:
                if (OnScreenFrame != null) await OnScreenFrame(payload);
                break;

            case var t when t == NmcpFrameTypes.InputEvent:
                _logger.LogDebug("Received NDA input event ({Len} bytes)", payload.Length);
                break;

            case var t when t == NmcpFrameTypes.JsonRpcResponse:
                // Decode NDA payload from response
                var responseNda = NdaPayload.Decode(payload);
                var hosts = ParseHostsFromNda(responseNda);
                if (hosts.Length > 0 && OnHostsUpdated != null) await OnHostsUpdated(hosts);
                break;

            case var t when t == NmcpFrameTypes.JsonRpcRequest:
                var requestNda = NdaPayload.Decode(payload);
                if (OnRequest != null) await OnRequest(requestNda);
                break;

            case var t when t == NmcpFrameTypes.Heartbeat:
                _logger.LogDebug("Heartbeat");
                break;

            case var t when t == NmcpFrameTypes.ToolCall:
                var toolNda = NdaPayload.Decode(payload);
                var toolName = toolNda.GetValue("tool") ?? "unknown";
                var requestId = toolNda.GetValue("request_id") ?? "0";
                uint reqSeq = 0;
                uint.TryParse(requestId, out reqSeq);
                if (OnToolCall != null)
                {
                    // Log custody record for incoming remote tool call
                    _custody?.LogToolCall(toolName, $"remote_seq={reqSeq}", targetSystem: "remote",
                        correlationId: null);

                    var resultData = await OnToolCall(toolName, toolNda.RawData ?? Array.Empty<byte>(), reqSeq);
                    var resultNda = new NdaPayload();
                    resultNda.Triples.Add(new NdaTriple("tool_result", "id", requestId));
                    resultNda.Triples.Add(new NdaTriple("tool_result", "tool", toolName));
                    resultNda.RawData = resultData;
                    var resultFrame = new NmcpFrame(NmcpFrameTypes.ToolResult, reqSeq, resultNda.Encode());
                    await SendFrameAsync(resultFrame, CancellationToken.None);
                }
                break;

            default:
                _logger.LogDebug("Unknown NMCP frame type: {Type}", frameType);
                break;
        }
    }

    /// <summary>Parse host list from NDA triples.
    /// Expected triples: (host, id, "x"), (host, name, "y"), (host, platform, "z"), etc.</summary>
    private static RemoteHost[] ParseHostsFromNda(NdaPayload nda)
    {
        // Group triples by host identity
        var hostMap = new Dictionary<string, (string Name, string Address, bool Online, string Platform)>();
        string? currentId = null;

        foreach (var t in nda.Triples)
        {
            if (t.Subject == "host" && t.Predicate == "id")
            {
                currentId = t.Object;
                if (!hostMap.ContainsKey(currentId))
                    hostMap[currentId] = (currentId, "", true, "unknown");
            }
            else if (currentId != null)
            {
                var existing = hostMap[currentId];
                switch (t.Predicate)
                {
                    case "name": hostMap[currentId] = (t.Object, existing.Address, existing.Online, existing.Platform); break;
                    case "address": hostMap[currentId] = (existing.Name, t.Object, existing.Online, existing.Platform); break;
                    case "online": hostMap[currentId] = (existing.Name, existing.Address, t.Object == "true", existing.Platform); break;
                    case "platform": hostMap[currentId] = (existing.Name, existing.Address, existing.Online, t.Object); break;
                }
            }
        }

        return hostMap.Select(kv => new RemoteHost(kv.Key, kv.Value.Name, kv.Value.Address, kv.Value.Online, kv.Value.Platform)).ToArray();
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
                        await SafeWsSendAsync(header, WebSocketMessageType.Binary, ct);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.LogWarning("Remote heartbeat failed: {Error}", ex.Message); break; }
            }
        }, ct);
    }

    private async Task SafeWsSendAsync(byte[] data, WebSocketMessageType messageType, CancellationToken ct)
    {
        if (!await _sendLock.WaitAsync(SendTimeout, ct)) return;
        try
        {
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(SendTimeout);
            await _ws!.SendAsync(new ArraySegment<byte>(data), messageType, true, sendCts.Token);
        }
        catch (OperationCanceledException) { }
        finally { _sendLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_heartbeatTask != null) { try { await _heartbeatTask; } catch { /* heartbeat cancelled during disposal — expected */ } }
        if (_ws?.State == WebSocketState.Open)
        {
            try { using var c = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", c.Token); }
            catch { /* WebSocket may already be faulted — disposing anyway */ }
        }
        _ws?.Dispose();
        _sendLock.Dispose();
        _cts?.Dispose();
    }
}

public record RemoteHost(string Id, string Name, string Address, bool IsOnline, string? Platform);
