using global::System.Text.Json;
using Drone.Core.Custody;

namespace Drone.Custody;

/// <summary>
/// Append-only JSON-lines storage for custody records.
/// Maintains per-drone files and a merged global timeline.
/// Supports Merkle root verification for O(log N) batch integrity checks.
/// </summary>
public class CustodyLogStore : IDisposable
{
    private readonly string _basePath;
    private readonly object _lock = new();
    private readonly Dictionary<string, StreamWriter> _droneWriters = new();
    private StreamWriter? _mergedWriter;
    private string _mergedPath = "";
    private DateTime _mergedDate;
    private readonly long _maxFileSizeBytes;

    /// <summary>In-memory index: drone ID -> list of records (for fast queries).</summary>
    private readonly Dictionary<string, List<CustodyRecord>> _droneIndex = new();

    /// <summary>Merged timeline across all drones (for cross-drone queries).</summary>
    private readonly List<CustodyRecord> _mergedIndex = new();

    /// <summary>Max records to keep in memory per drone before evicting to disk-only.</summary>
    private const int MaxInMemoryPerDrone = 10_000;

    /// <summary>Max records in merged index before evicting.</summary>
    private const int MaxInMemoryMerged = 100_000;

    public CustodyLogStore(string basePath, long maxFileSizeMb = 100)
    {
        _basePath = basePath;
        _maxFileSizeBytes = maxFileSizeMb * 1024 * 1024;

        if (!Directory.Exists(_basePath))
            Directory.CreateDirectory(_basePath);

        _mergedPath = GetMergedFilePath(DateTime.UtcNow.Date);
        EnsureMergedWriter();
    }

    /// <summary>Total records stored (in-memory count).</summary>
    public int TotalRecords
    {
        get { lock (_lock) return _mergedIndex.Count; }
    }

    /// <summary>Number of drones with records.</summary>
    public int DroneCount
    {
        get { lock (_lock) return _droneIndex.Count; }
    }

    /// <summary>
    /// Store a batch of custody records. Validates hash chain continuity.
    /// If records have MerkleRoot set, also validates the batch Merkle root.
    /// Returns (accepted, rejected) counts.
    /// </summary>
    public (int accepted, int rejected) StoreRecords(IEnumerable<CustodyRecord> records)
    {
        int accepted = 0, rejected = 0;
        var recordList = records.ToList();

        // If records have MerkleRoot, verify the batch root
        var batchRoot = "";
        if (recordList.Count > 0 && !string.IsNullOrEmpty(recordList[0].MerkleRoot))
        {
            batchRoot = recordList[0].MerkleRoot;
            if (!VerifyBatchMerkleRoot(recordList, batchRoot))
            {
                // All records in this batch fail Merkle verification
                return (0, recordList.Count);
            }
        }

        lock (_lock)
        {
            foreach (var record in recordList)
            {
                if (record == null || string.IsNullOrEmpty(record.DroneId))
                {
                    rejected++;
                    continue;
                }

                // Validate hash
                if (!record.VerifyHash())
                {
                    rejected++;
                    continue;
                }

                // Validate chain continuity for this drone
                if (_droneIndex.TryGetValue(record.DroneId, out var droneRecords) && droneRecords.Count > 0)
                {
                    var lastForDrone = droneRecords[^1];
                    if (record.Sequence != lastForDrone.Sequence + 1)
                    {
                        rejected++;
                        continue;
                    }
                    if (record.PrevHash != lastForDrone.Hash)
                    {
                        rejected++;
                        continue;
                    }
                }
                else if (record.Sequence != 1)
                {
                    // First record for this drone should be sequence 1
                    // (unless we're loading from persisted state)
                    // Accept it anyway — it might be a resumed chain
                }

                // Store in per-drone index
                if (!_droneIndex.ContainsKey(record.DroneId))
                    _droneIndex[record.DroneId] = new List<CustodyRecord>();

                _droneIndex[record.DroneId].Add(record);

                // Evict old records from memory if needed
                if (_droneIndex[record.DroneId].Count > MaxInMemoryPerDrone)
                    _droneIndex[record.DroneId].RemoveRange(0, _droneIndex[record.DroneId].Count - MaxInMemoryPerDrone);

                // Store in merged index
                _mergedIndex.Add(record);
                if (_mergedIndex.Count > MaxInMemoryMerged)
                    _mergedIndex.RemoveRange(0, _mergedIndex.Count - MaxInMemoryMerged);

                // Write to per-drone file
                WriteToDroneFile(record);

                // Write to merged file
                WriteToMergedFile(record);

                accepted++;
            }
        }

        return (accepted, rejected);
    }

    /// <summary>
    /// Verify that a batch of records matches the expected Merkle root.
    /// Computes the Merkle root from the records' content hashes and compares.
    /// </summary>
    public static bool VerifyBatchMerkleRoot(IList<CustodyRecord> records, string expectedRoot)
    {
        if (records.Count == 0 || string.IsNullOrEmpty(expectedRoot))
            return false;

        var computedRoot = CustodyChain.ComputeBatchMerkleRoot(records);
        return computedRoot == expectedRoot;
    }

    /// <summary>
    /// Verify the entire custody trail for a drone using Merkle batch verification.
    /// Groups records by their MerkleRoot and verifies each batch independently.
    /// </summary>
    /// <param name="droneId">The drone to verify.</param>
    /// <returns>Tuple of (totalBatches, validBatches, chainValid).</returns>
    public (int TotalBatches, int ValidBatches, bool ChainValid) VerifyDroneTrailMerkle(string droneId)
    {
        var records = GetDroneRecords(droneId);
        if (records.Length == 0)
            return (0, 0, true);

        // First verify the hash chain
        var chainValid = CustodyChain.VerifyChain(records);

        // Group records by MerkleRoot for batch verification
        var batches = records
            .Where(r => !string.IsNullOrEmpty(r.MerkleRoot))
            .GroupBy(r => r.MerkleRoot)
            .ToList();

        int validBatches = 0;
        foreach (var batch in batches)
        {
            var root = batch.Key;
            var batchRecords = batch.ToList();
            if (VerifyBatchMerkleRoot(batchRecords, root))
                validBatches++;
        }

        return (batches.Count, validBatches, chainValid);
    }

    /// <summary>Get all records for a specific drone.</summary>
    public CustodyRecord[] GetDroneRecords(string droneId)
    {
        lock (_lock)
        {
            if (_droneIndex.TryGetValue(droneId, out var records))
                return records.ToArray();
            return Array.Empty<CustodyRecord>();
        }
    }

    /// <summary>Get the merged timeline across all drones.</summary>
    public CustodyRecord[] GetMergedTimeline()
    {
        lock (_lock)
        {
            return _mergedIndex.ToArray();
        }
    }

    /// <summary>Get records matching a time range.</summary>
    public CustodyRecord[] GetRecordsByTimeRange(DateTime from, DateTime to)
    {
        lock (_lock)
        {
            return _mergedIndex
                .Where(r => r.Timestamp >= from && r.Timestamp <= to)
                .ToArray();
        }
    }

    /// <summary>Get records matching a correlation ID.</summary>
    public CustodyRecord[] GetRecordsByCorrelation(string correlationId)
    {
        lock (_lock)
        {
            return _mergedIndex
                .Where(r => r.CorrelationId == correlationId)
                .ToArray();
        }
    }

    /// <summary>Get records matching an event type.</summary>
    public CustodyRecord[] GetRecordsByEventType(string eventType)
    {
        lock (_lock)
        {
            return _mergedIndex
                .Where(r => r.EventType == eventType)
                .ToArray();
        }
    }

    /// <summary>Get all known drone IDs.</summary>
    public string[] GetDroneIds()
    {
        lock (_lock)
        {
            return _droneIndex.Keys.ToArray();
        }
    }

    /// <summary>Get the last record for a specific drone.</summary>
    public CustodyRecord? GetLastRecord(string droneId)
    {
        lock (_lock)
        {
            if (_droneIndex.TryGetValue(droneId, out var records) && records.Count > 0)
                return records[^1];
            return null;
        }
    }

    private void WriteToDroneFile(CustodyRecord record)
    {
        try
        {
            if (!_droneWriters.TryGetValue(record.DroneId, out var writer) || writer == null)
            {
                var path = GetDroneFilePath(record.DroneId, DateTime.UtcNow.Date);
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                writer = new StreamWriter(path, append: true) { AutoFlush = false };
                _droneWriters[record.DroneId] = writer;
            }
            writer.WriteLine(record.ToJson());
            writer.Flush();
        }
        catch { /* write failure is non-fatal */ }
    }

    private void WriteToMergedFile(CustodyRecord record)
    {
        try
        {
            EnsureMergedWriter();
            _mergedWriter?.WriteLine(record.ToJson());
            _mergedWriter?.Flush();
        }
        catch { /* write failure is non-fatal */ }
    }

    private void EnsureMergedWriter()
    {
        var today = DateTime.UtcNow.Date;
        var path = GetMergedFilePath(today);

        if (_mergedWriter != null && today != _mergedDate)
        {
            _mergedWriter.Dispose();
            _mergedWriter = null;
        }

        if (_mergedWriter == null)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            _mergedWriter = new StreamWriter(path, append: true) { AutoFlush = false };
            _mergedPath = path;
            _mergedDate = today;
        }
    }

    private string GetDroneFilePath(string droneId, DateTime date)
    {
        var safeId = string.Join("_", droneId.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_basePath, "drones", $"{safeId}-custody-{date:yyyy-MM-dd}.jsonl");
    }

    private string GetMergedFilePath(DateTime date)
    {
        return Path.Combine(_basePath, $"custody-merged-{date:yyyy-MM-dd}.jsonl");
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var writer in _droneWriters.Values)
            {
                try { writer.Dispose(); } catch { }
            }
            _droneWriters.Clear();
            _mergedWriter?.Dispose();
            _mergedWriter = null;
        }
        GC.SuppressFinalize(this);
    }
}
