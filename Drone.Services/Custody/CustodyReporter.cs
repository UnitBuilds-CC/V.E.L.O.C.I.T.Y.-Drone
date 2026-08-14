using global::System.Text;
using global::System.Text.Json;
using Drone.Core;
using Drone.Core.Custody;

namespace Drone.Services.Custody;

/// <summary>
/// Background service that batches custody records and streams them to the
/// CustodyServer via NMCP CustodyReport frames. Supports offline operation —
/// queues records locally and flushes when reconnected.
/// </summary>
public class CustodyReporter : IAsyncDisposable
{
    private readonly CustodyAuditLogger _logger;
    private readonly ILogger _logger2;
    private CancellationTokenSource? _cts;
    private Task? _flushTask;

    /// <summary>Function to send raw bytes (NMCP frame payload) to the custody server.</summary>
    private Func<byte[], Task<bool>>? _sendFunc;

    /// <summary>Last sequence number acknowledged by the server.</summary>
    private long _lastAckedSequence;

    /// <summary>Flush interval — how often to batch-send records.</summary>
    private readonly TimeSpan _flushInterval;

    /// <summary>Maximum records per batch.</summary>
    private const int MaxBatchSize = 50;

    /// <summary>Whether the reporter is currently connected to the server.</summary>
    private volatile bool _connected;

    /// <summary>Event fired when records are successfully sent and acknowledged.</summary>
    public event Action<long>? OnRecordsAcked;

    /// <summary>
    /// Create a custody reporter.
    /// </summary>
    /// <param name="logger">The custody audit logger to read records from.</param>
    /// <param name="log">Logger for diagnostics.</param>
    /// <param name="flushIntervalSec">How often to flush records to server (default 5s).</param>
    public CustodyReporter(CustodyAuditLogger logger, ILogger log, int flushIntervalSec = 5)
    {
        _logger = logger;
        _logger2 = log;
        _flushInterval = TimeSpan.FromSeconds(flushIntervalSec);
        _lastAckedSequence = logger.CurrentSequence; // Start from current — don't replay old records
    }

    /// <summary>Whether the reporter is connected and actively streaming.</summary>
    public bool IsConnected => _connected;

    /// <summary>Last acknowledged sequence number.</summary>
    public long LastAckedSequence => _lastAckedSequence;

    /// <summary>
    /// Set the send function. This is called by the agent to wire up the transport
    /// (RemoteConnector, VelocityConnection, etc.).
    /// The function should send the bytes and return true if the server acknowledged.
    /// </summary>
    public void SetSendFunction(Func<byte[], Task<bool>> sendFunc)
    {
        _sendFunc = sendFunc;
        _connected = true;
    }

    /// <summary>Mark the reporter as disconnected (e.g., when the transport drops).</summary>
    public void SetDisconnected()
    {
        _connected = false;
    }

    /// <summary>Start the background flush loop.</summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Subscribe to new records for immediate notification
        _logger.OnRecordCreated += OnNewRecord;

        _flushTask = Task.Run(() => FlushLoopAsync(_cts.Token));
        _logger2.LogInformation("CustodyReporter started (flush every {Interval}s)", _flushInterval.TotalSeconds);
        return Task.CompletedTask;
    }

    private volatile bool _hasPendingRecords;

    private void OnNewRecord(CustodyRecord record)
    {
        _hasPendingRecords = true;
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_flushInterval, ct);

                if (!_connected || _sendFunc == null)
                    continue;

                if (!_hasPendingRecords && _lastAckedSequence >= _logger.CurrentSequence)
                    continue;

                await FlushAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger2.LogWarning("CustodyReporter flush error: {Error}", ex.Message);
                await Task.Delay(1000, ct);
            }
        }
    }

    /// <summary>
    /// Flush pending records to the custody server.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_sendFunc == null) return;

        var records = _logger.GetRecordsAfter(_lastAckedSequence);
        if (records.Length == 0)
        {
            _hasPendingRecords = false;
            return;
        }

        // Batch into groups of MaxBatchSize
        for (int i = 0; i < records.Length; i += MaxBatchSize)
        {
            var batch = records.Skip(i).Take(MaxBatchSize).ToArray();
            var payload = BuildCustodyReportPayload(batch);

            try
            {
                var acked = await _sendFunc(payload).WaitAsync(TimeSpan.FromSeconds(10), ct);
                if (acked)
                {
                    _lastAckedSequence = batch[^1].Sequence;
                    _hasPendingRecords = false;
                    OnRecordsAcked?.Invoke(_lastAckedSequence);
                }
                else
                {
                    _logger2.LogWarning("CustodyReport not acknowledged, will retry");
                    break; // Stop sending — retry next flush
                }
            }
            catch (Exception ex)
            {
                _logger2.LogWarning("CustodyReport send failed: {Error}", ex.Message);
                break; // Will retry next flush
            }
        }
    }

    /// <summary>
    /// Build an NMCP CustodyReport payload from a batch of records.
    /// Format: JSON array of CustodyRecord objects.
    /// </summary>
    private static byte[] BuildCustodyReportPayload(CustodyRecord[] records)
    {
        var json = JsonSerializer.Serialize(records, CustodyRecord.JsonOptions);
        return Encoding.UTF8.GetBytes(json);
    }

    /// <summary>
    /// Build an NMCP CustodyQuery payload.
    /// </summary>
    public static byte[] BuildQueryPayload(string? droneId = null, DateTime? from = null,
        DateTime? to = null, string? correlationId = null, string? eventType = null)
    {
        var query = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(droneId)) query["droneId"] = droneId;
        if (from.HasValue) query["from"] = from.Value.ToString("O");
        if (to.HasValue) query["to"] = to.Value.ToString("O");
        if (!string.IsNullOrEmpty(correlationId)) query["correlationId"] = correlationId;
        if (!string.IsNullOrEmpty(eventType)) query["eventType"] = eventType;

        var json = JsonSerializer.Serialize(query);
        return Encoding.UTF8.GetBytes(json);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _logger.OnRecordCreated -= OnNewRecord;

        if (_flushTask != null)
        {
            try { await _flushTask; } catch { }
        }

        // Final flush
        if (_connected && _sendFunc != null)
        {
            try { await FlushAsync(CancellationToken.None); } catch { }
        }

        _cts?.Dispose();
    }
}
