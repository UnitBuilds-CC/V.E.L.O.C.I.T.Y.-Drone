using global::System.Net;
using global::System.Net.WebSockets;
using global::System.Text;
using global::System.Text.Json;
using Drone.Core;
using Drone.Core.Custody;
using Drone.Core.Protocol;
using Drone.Native;

namespace Drone.Custody;

/// <summary>
/// WebSocket server that accepts CustodyReport frames from drones,
/// validates hash chains, maintains timelines, and provides query/streaming.
/// Supports both JSON (type 40) and binary NMCP Merkle frames (type 44).
/// </summary>
public class CustodyServerHost : IAsyncDisposable
{
    private readonly CustodyLogStore _store;
    private readonly CustodyQueryEngine _queryEngine;
    private readonly ILogger _logger;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly List<WebSocket> _streamClients = new();
    private readonly object _clientsLock = new();

    /// <summary>Maximum concurrent drone connections.</summary>
    private const int MaxConnections = 256;

    /// <summary>NMCP Merkle frame magic bytes: "NMCP" (0x4E4D4350).</summary>
    private static readonly byte[] NmcpMerkleMagic = { 0x4E, 0x4D, 0x43, 0x50 };

    /// <summary>Per-drone expected sequence for chain validation on receipt.</summary>
    private readonly Dictionary<string, (long Seq, string Hash)> _droneChainState = new();
    private readonly object _chainLock = new();

    public CustodyServerHost(CustodyLogStore store, ILogger logger)
    {
        _store = store;
        _queryEngine = new CustodyQueryEngine(store);
        _logger = logger;
    }

    /// <summary>The query engine for programmatic access to custody data.</summary>
    public CustodyQueryEngine QueryEngine => _queryEngine;

    /// <summary>Total records stored.</summary>
    public int TotalRecords => _store.TotalRecords;

    /// <summary>Number of connected drones.</summary>
    public int ConnectedDrones => _store.DroneCount;

    /// <summary>Number of real-time stream clients.</summary>
    public int StreamClientCount
    {
        get { lock (_clientsLock) return _streamClients.Count(w => w.State == WebSocketState.Open); }
    }

    /// <summary>Start the custody server.</summary>
    public async Task StartAsync(string url, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new HttpListener();
        _listener.Prefixes.Add(url.EndsWith("/") ? url : url + "/");

        try { _listener.Start(); }
        catch (Exception ex)
        {
            _logger.LogError("Failed to start CustodyServer: {Error}", ex.Message);
            throw;
        }

        _logger.LogInformation("CustodyServer listening at {Url}", url);

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();

                if (context.Request.IsWebSocketRequest)
                {
                    await HandleWebSocketAsync(context);
                }
                else if (context.Request.HttpMethod == "GET" &&
                         context.Request.Url?.AbsolutePath.Contains("/custody") == true)
                {
                    HandleQueryRequest(context);
                }
                else if (context.Request.HttpMethod == "GET" &&
                         context.Request.Url?.AbsolutePath.Contains("/health") == true)
                {
                    HandleHealthCheck(context);
                }
                else
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                }
            }
            catch (ObjectDisposedException) { break; }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning("CustodyServer error: {Error}", ex.Message);
            }
        }
    }

    private async Task HandleWebSocketAsync(HttpListenerContext context)
    {
        var wsContext = await context.AcceptWebSocketAsync(null);
        var ws = wsContext.WebSocket;
        var clientAddr = context.Request.RemoteEndPoint?.ToString() ?? "unknown";

        bool reject = false;
        lock (_clientsLock)
        {
            if (_streamClients.Count(w => w.State == WebSocketState.Open) >= MaxConnections)
            {
                reject = true;
            }
            else
            {
                _streamClients.Add(ws);
            }
        }

        if (reject)
        {
            _logger.LogWarning("Max stream clients reached, rejecting {Addr}", clientAddr);
            try { await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, "Max connections", CancellationToken.None); } catch { }
            ws.Dispose();
            return;
        }

        _logger.LogInformation("Drone connected from {Addr}", clientAddr);
        var buffer = new byte[256 * 1024]; // 256KB buffer for custody batches

        try
        {
            while (ws.State == WebSocketState.Open && !_cts!.Token.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var data = ms.ToArray();
                await ProcessCustodyReport(data, ws);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _logger.LogWarning("Drone {Addr} disconnected: {Error}", clientAddr, ex.Message);
        }
        finally
        {
            lock (_clientsLock) _streamClients.Remove(ws);
            if (ws.State == WebSocketState.Open)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None); } catch { }
            }
            ws.Dispose();
            _logger.LogInformation("Drone disconnected from {Addr}", clientAddr);
        }
    }

    private async Task ProcessCustodyReport(byte[] data, WebSocket responseWs)
    {
        try
        {
            CustodyRecord[]? records;

            // Detect NMCP Merkle binary frame: starts with "NMCP" magic (0x4E4D4350)
            if (IsNmcpMerkleFrame(data))
            {
                records = ProcessBinaryCustodyFrame(data);
                if (records == null || records.Length == 0)
                {
                    _logger.LogWarning("Failed to parse NMCP Merkle custody frame");
                    return;
                }
                _logger.LogInformation("Received binary custody frame: {Count} records (Merkle-verified)", records.Length);
            }
            else
            {
                // JSON fallback
                var json = Encoding.UTF8.GetString(data);
                records = JsonSerializer.Deserialize<CustodyRecord[]>(json, CustodyRecord.JsonOptions);
                if (records == null || records.Length == 0) return;
            }

            // Validate chain continuity against server-side state
            var validRecords = new List<CustodyRecord>();
            lock (_chainLock)
            {
                foreach (var record in records)
                {
                    if (string.IsNullOrEmpty(record.DroneId)) continue;

                    if (_droneChainState.TryGetValue(record.DroneId, out var state))
                    {
                        // Verify chain continuity
                        if (record.Sequence != state.Seq + 1 || record.PrevHash != state.Hash)
                        {
                            _logger.LogWarning("Chain break from drone {Drone}: expected seq {Expected}, got {Got}",
                                record.DroneId, state.Seq + 1, record.Sequence);
                            continue;
                        }
                    }

                    if (!record.VerifyHash())
                    {
                        _logger.LogWarning("Hash verification failed for record {EventId}", record.EventId);
                        continue;
                    }

                    _droneChainState[record.DroneId] = (record.Sequence, record.Hash);
                    validRecords.Add(record);
                }
            }

            var (accepted, rejected) = _store.StoreRecords(validRecords);
            _logger.LogInformation("CustodyReport: {Accepted} accepted, {Rejected} rejected (of {Total})",
                accepted, rejected, records.Length);

            // Send acknowledgment
            var ack = JsonSerializer.Serialize(new { acknowledged = true, accepted, rejected, lastSeq = validRecords.Count > 0 ? validRecords[^1].Sequence : 0 });
            var ackBytes = Encoding.UTF8.GetBytes(ack);
            await responseWs.SendAsync(new ArraySegment<byte>(ackBytes), WebSocketMessageType.Text, true, CancellationToken.None);

            // Broadcast to stream clients
            if (accepted > 0)
            {
                await BroadcastToStreamClientsAsync(data);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error processing custody report: {Error}", ex.Message);
        }
    }

    /// <summary>
    /// Check if data starts with NMCP Merkle frame magic bytes (0x4E4D4350 = "NMCP").
    /// This distinguishes binary frames from JSON custody reports.
    /// </summary>
    private static bool IsNmcpMerkleFrame(byte[] data)
    {
        return data.Length >= 36 &&
               data[0] == 0x4E && data[1] == 0x4D &&
               data[2] == 0x43 && data[3] == 0x50;
    }

    /// <summary>
    /// Parse an NMCP Merkle binary custody frame.
    /// Layout: [4-byte magic "NMCP"] [32-byte Merkle root] [N x 256-byte records]
    /// Validates the Merkle root using native Rust parser (if available), then
    /// deserializes records using managed binary serializer.
    /// </summary>
    private CustodyRecord[]? ProcessBinaryCustodyFrame(byte[] data)
    {
        // Try native Rust Merkle frame validation (verifies magic + extracts root)
        var merkleRoot = new byte[32];
        if (NativeBindings.TryParseMerkleFrame(data, merkleRoot, out var payloadLen))
        {
            _logger.LogInformation("NMCP Merkle frame validated via native parser (payload={Len})", payloadLen);
        }

        // Deserialize records using managed binary serializer (validates Merkle root internally)
        return CustodyBinarySerializer.DeserializeBatch(data);
    }

    private async Task BroadcastToStreamClientsAsync(byte[] data)
    {
        WebSocket[] clients;
        lock (_clientsLock)
        {
            clients = _streamClients.Where(w => w.State == WebSocketState.Open).ToArray();
        }

        foreach (var ws in clients)
        {
            try
            {
                await ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch { /* individual client failure is non-fatal */ }
        }
    }

    private void HandleQueryRequest(HttpListenerContext context)
    {
        try
        {
            var query = context.Request.QueryString;
            var droneId = query["drone"];
            var correlationId = query["correlation"];
            var eventType = query["eventType"];
            var fromStr = query["from"];
            var toStr = query["to"];

            DateTime? from = null, to = null;
            if (!string.IsNullOrEmpty(fromStr) && DateTime.TryParse(fromStr, out var f)) from = f;
            if (!string.IsNullOrEmpty(toStr) && DateTime.TryParse(toStr, out var t)) to = t;

            // Delegate to CustodyQueryEngine
            var results = _queryEngine.Query(droneId, from, to, correlationId, eventType);

            var json = JsonSerializer.Serialize(new
            {
                count = results.Length,
                records = results
            }, CustodyRecord.JsonOptions);

            var bytes = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.AddHeader("Access-Control-Allow-Origin", "*");
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.Close();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Query error: {Error}", ex.Message);
            context.Response.StatusCode = 500;
            context.Response.Close();
        }
    }

    private void HandleHealthCheck(HttpListenerContext context)
    {
        var summary = _queryEngine.GetSummary();
        var health = JsonSerializer.Serialize(new
        {
            status = "healthy",
            totalRecords = summary.TotalRecords,
            droneCount = summary.DroneCount,
            streamClients = StreamClientCount,
            droneIds = summary.DroneIds
        });
        var bytes = Encoding.UTF8.GetBytes(health);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.AddHeader("Access-Control-Allow-Origin", "*");
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();

        // Close stream clients
        WebSocket[] clients;
        lock (_clientsLock)
        {
            clients = _streamClients.ToArray();
            _streamClients.Clear();
        }
        foreach (var ws in clients)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Shutting down", CancellationToken.None); } catch { }
            ws.Dispose();
        }

        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _cts?.Dispose();
    }
}
