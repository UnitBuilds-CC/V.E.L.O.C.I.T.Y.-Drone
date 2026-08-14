using global::System.Text.Json;
using Drone.Core.Protocol;

namespace Drone.Core.Custody;

/// <summary>
/// Custody-aware audit logger. Wraps CustodyChain to produce hash-chained records,
/// writes them to local JSON-lines file (offline resilience), and maintains an
/// in-memory ring buffer for streaming to the CustodyServer.
/// Supports Merkle batch roots for O(log N) verification.
/// </summary>
public class CustodyAuditLogger : IDisposable
{
    private readonly CustodyChain _chain;
    private readonly CorrelationTracker _correlations;
    private readonly string _logPath;
    private readonly long _maxFileSizeBytes;
    private StreamWriter? _writer;
    private string _currentFilePath = "";
    private DateTime _currentDate;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>In-memory ring buffer of recent records for streaming.</summary>
    private readonly CustodyRecord?[] _ringBuffer;
    private int _ringWritePos;
    private int _ringCount;
    private readonly object _ringLock = new();

    /// <summary>Default ring buffer size.</summary>
    private const int DefaultRingBufferSize = 1000;

    /// <summary>Pending batch for Merkle root assignment.</summary>
    private readonly List<CustodyRecord> _pendingBatch = new();
    private readonly object _batchLock = new();

    /// <summary>Maximum records in a pending batch before auto-flush.</summary>
    private const int MaxPendingBatchSize = 50;

    /// <summary>Event fired when a new custody record is created. Used by CustodyReporter.</summary>
    public event Action<CustodyRecord>? OnRecordCreated;

    /// <summary>Event fired when a batch is flushed with Merkle root assigned. Used by CustodyReporter for binary streaming.</summary>
    public event Action<CustodyRecord[], string>? OnBatchFlushed;

    /// <summary>Create a custody audit logger.</summary>
    /// <param name="droneId">Identity of this drone agent.</param>
    /// <param name="logPath">Path for local JSON-lines log file. Null to disable local logging.</param>
    /// <param name="maxFileSizeMb">Max file size before rotation (default 50MB).</param>
    /// <param name="ringBufferSize">Number of recent records to keep in memory for streaming.</param>
    public CustodyAuditLogger(string droneId, string? logPath = null, long maxFileSizeMb = 50, int ringBufferSize = DefaultRingBufferSize)
    {
        _chain = new CustodyChain(droneId);
        _correlations = new CorrelationTracker();
        _logPath = logPath ?? "";
        _maxFileSizeBytes = maxFileSizeMb * 1024 * 1024;
        _ringBuffer = new CustodyRecord[ringBufferSize];

        if (!string.IsNullOrEmpty(_logPath))
            EnsureWriter();
    }

    /// <summary>The drone ID this logger is tracking.</summary>
    public string DroneId => _chain.LastRecord?.DroneId ?? "";

    /// <summary>Current sequence number in the chain.</summary>
    public long CurrentSequence => _chain.CurrentSequence;

    /// <summary>Current hash chain head.</summary>
    public string CurrentHash => _chain.CurrentHash;

    /// <summary>Correlation tracker for cross-machine sequences.</summary>
    public CorrelationTracker Correlations => _correlations;

    /// <summary>The underlying hash chain.</summary>
    public CustodyChain Chain => _chain;

    /// <summary>Number of records in the pending batch (not yet assigned a Merkle root).</summary>
    public int PendingBatchCount { get { lock (_batchLock) return _pendingBatch.Count; } }

    /// <summary>Log a tool call event.</summary>
    public CustodyRecord LogToolCall(string action, string? arguments = null, string? result = null,
        bool success = true, string? targetSystem = null, string? correlationId = null)
    {
        var record = _chain.NextRecord("tool_call", action, arguments, result, success, targetSystem, correlationId);
        EmitRecord(record);
        return record;
    }

    /// <summary>Log a connection event.</summary>
    public CustodyRecord LogConnection(string action, string? targetSystem = null, string? details = null,
        bool success = true, string? correlationId = null)
    {
        var record = _chain.NextRecord("connection", action, details, success ? "ok" : "failed", success, targetSystem, correlationId);
        EmitRecord(record);
        return record;
    }

    /// <summary>Log a security event.</summary>
    public CustodyRecord LogSecurity(string action, string? details = null, string? targetSystem = null,
        string? correlationId = null)
    {
        var record = _chain.NextRecord("security", action, details, null, false, targetSystem, correlationId);
        EmitRecord(record);
        return record;
    }

    /// <summary>Log a cross-machine action (with automatic correlation tracking).</summary>
    public CustodyRecord LogCrossMachine(string action, string targetDroneId, string? arguments = null,
        string? correlationId = null, string? result = null, bool success = true)
    {
        // Auto-create correlation if none provided
        if (string.IsNullOrEmpty(correlationId))
            correlationId = _correlations.Create(DroneId, $"cross_machine_{action}", $"Action on {targetDroneId}");

        _correlations.RecordStep(correlationId, DroneId, action);

        var record = _chain.NextRecord("cross_machine", action, arguments, result, success, targetDroneId, correlationId);
        EmitRecord(record);
        return record;
    }

    /// <summary>Log a generic event.</summary>
    public CustodyRecord Log(string eventType, string action, string? arguments = null,
        string? result = null, bool success = true, string? targetSystem = null,
        string? correlationId = null)
    {
        var record = _chain.NextRecord(eventType, action, arguments, result, success, targetSystem, correlationId);
        EmitRecord(record);
        return record;
    }

    /// <summary>
    /// Flush the pending batch: compute Merkle root over all pending records,
    /// assign it to each record, rewrite them to the log file with the root,
    /// and fire the OnBatchFlushed event.
    /// </summary>
    /// <returns>The Merkle root assigned (hex), or empty if no pending records.</returns>
    public string FlushBatchWithMerkleRoot()
    {
        CustodyRecord[] batch;
        lock (_batchLock)
        {
            if (_pendingBatch.Count == 0) return "";
            batch = _pendingBatch.ToArray();
            _pendingBatch.Clear();
        }

        // Compute and assign Merkle root
        var root = CustodyChain.AssignBatchMerkleRoot(batch);

        // Rewrite records to file with MerkleRoot set
        WriteBatchToFile(batch);

        // Notify listeners (CustodyReporter uses this for binary streaming)
        OnBatchFlushed?.Invoke(batch, root);

        return root;
    }

    /// <summary>
    /// Get recent records from the ring buffer (newest first).
    /// Used by CustodyReporter to stream records to the server.
    /// </summary>
    /// <param name="count">Maximum number of records to return.</param>
    /// <param name="afterSequence">Only return records with sequence > this value.</param>
    public CustodyRecord[] GetRecentRecords(int count = 100, long afterSequence = -1)
    {
        lock (_ringLock)
        {
            if (_ringCount == 0) return Array.Empty<CustodyRecord>();

            var result = new List<CustodyRecord>(Math.Min(count, _ringCount));
            var start = (_ringWritePos - 1 + _ringBuffer.Length) % _ringBuffer.Length;

            for (int i = 0; i < _ringCount && i < count; i++)
            {
                var idx = (start - i + _ringBuffer.Length) % _ringBuffer.Length;
                var record = _ringBuffer[idx];
                if (record != null && record.Sequence > afterSequence)
                    result.Add(record);
            }

            // Return oldest-first
            result.Reverse();
            return result.ToArray();
        }
    }

    /// <summary>Get all records from the ring buffer with sequence > the given value.</summary>
    public CustodyRecord[] GetRecordsAfter(long sequence)
    {
        return GetRecentRecords(_ringBuffer.Length, sequence);
    }

    private void EmitRecord(CustodyRecord record)
    {
        // Add to pending batch for Merkle root assignment
        bool shouldFlush = false;
        lock (_batchLock)
        {
            _pendingBatch.Add(record);
            shouldFlush = _pendingBatch.Count >= MaxPendingBatchSize;
        }

        // Add to ring buffer
        lock (_ringLock)
        {
            _ringBuffer[_ringWritePos] = record;
            _ringWritePos = (_ringWritePos + 1) % _ringBuffer.Length;
            if (_ringCount < _ringBuffer.Length) _ringCount++;
        }

        // Notify listeners (record created, before Merkle root assigned)
        OnRecordCreated?.Invoke(record);

        // Auto-flush if batch is full
        if (shouldFlush)
            FlushBatchWithMerkleRoot();
    }

    private void WriteBatchToFile(CustodyRecord[] batch)
    {
        if (_disposed || string.IsNullOrEmpty(_logPath)) return;
        lock (_lock)
        {
            try
            {
                EnsureWriter();
                foreach (var record in batch)
                {
                    _writer?.WriteLine(record.ToJson());
                }
                _writer?.Flush();
            }
            catch { /* custody write failure must not crash the agent */ }
        }
    }

    private void EnsureWriter()
    {
        if (string.IsNullOrEmpty(_logPath)) return;

        var today = DateTime.UtcNow.Date;
        var filePath = GetFilePath(today);

        // Daily rotation
        if (_writer != null && today != _currentDate)
        {
            _writer.Dispose();
            _writer = null;
        }

        if (_writer == null)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _writer = new StreamWriter(filePath, append: true) { AutoFlush = false };
            _currentFilePath = filePath;
            _currentDate = today;
        }

        // Size-based rotation
        try
        {
            var fi = new FileInfo(_currentFilePath);
            if (fi.Exists && fi.Length > _maxFileSizeBytes)
            {
                _writer.Dispose();
                var rotatedPath = _currentFilePath + "." + DateTime.UtcNow.ToString("HHmmss");
                File.Move(_currentFilePath, rotatedPath);
                _writer = new StreamWriter(_currentFilePath, append: true) { AutoFlush = false };
                CleanupOldRotations();
            }
        }
        catch { /* rotation failure is non-fatal */ }
    }

    private string GetFilePath(DateTime date)
    {
        var ext = Path.GetExtension(_logPath);
        var baseName = Path.GetFileNameWithoutExtension(_logPath);
        var dir = Path.GetDirectoryName(_logPath) ?? ".";
        return Path.Combine(dir, $"{baseName}-custody-{date:yyyy-MM-dd}{ext}");
    }

    private void CleanupOldRotations()
    {
        try
        {
            var dir = Path.GetDirectoryName(_currentFilePath) ?? ".";
            var baseName = Path.GetFileName(_currentFilePath);
            var rotated = Directory.GetFiles(dir, baseName + ".*")
                .OrderByDescending(f => f)
                .Skip(5);
            foreach (var old in rotated)
            {
                try { File.Delete(old); } catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Load persisted records from the local log file and restore the chain state.
    /// Call this on startup to resume the chain from where it left off.
    /// </summary>
    public List<CustodyRecord> LoadPersistedRecords()
    {
        var records = new List<CustodyRecord>();
        if (string.IsNullOrEmpty(_logPath)) return records;

        try
        {
            var dir = Path.GetDirectoryName(_logPath) ?? ".";
            var baseName = Path.GetFileNameWithoutExtension(_logPath);
            var ext = Path.GetExtension(_logPath);

            // Find all custody log files (sorted by date)
            var files = Directory.GetFiles(dir, $"{baseName}-custody-*{ext}")
                .OrderBy(f => f);

            foreach (var file in files)
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var record = CustodyRecord.FromJson(line);
                    if (record != null) records.Add(record);
                }
            }

            // Restore chain state from last valid record
            if (records.Count > 0)
            {
                var last = records[^1];
                _chain.ResetTo(last.Sequence, last.Hash, last);
            }
        }
        catch { /* load failure is non-fatal — starts fresh chain */ }

        return records;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Flush any remaining pending batch
        FlushBatchWithMerkleRoot();

        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
        GC.SuppressFinalize(this);
    }
}
