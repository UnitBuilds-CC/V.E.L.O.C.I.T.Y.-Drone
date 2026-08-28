namespace Drone.Core.Custody;

/// <summary>
/// Manages the hash chain for custody records. Tracks the previous record's hash
/// and assigns monotonic sequence numbers. Thread-safe.
/// Supports Merkle batch verification for O(log N) integrity checks.
/// </summary>
public class CustodyChain
{
    private readonly string _droneId;
    private readonly object _lock = new();
    private long _sequence;
    private string _prevHash = "";
    private CustodyRecord? _lastRecord;

    public CustodyChain(string droneId)
    {
        _droneId = droneId;
    }

    /// <summary>Current sequence number (last assigned). 0 if no records yet.</summary>
    public long CurrentSequence { get { lock (_lock) return _sequence; } }

    /// <summary>Hash of the last record in the chain. Empty if no records yet.</summary>
    public string CurrentHash { get { lock (_lock) return _prevHash; } }

    /// <summary>The last record added to the chain. Null if empty.</summary>
    public CustodyRecord? LastRecord { get { lock (_lock) return _lastRecord; } }

    /// <summary>
    /// Create the next record in the chain. Assigns sequence number, event ID,
    /// previous hash, and computes the hash.
    /// </summary>
    public CustodyRecord NextRecord(string eventType, string action, string? arguments = null,
        string? result = null, bool success = true, string? targetSystem = null,
        string? correlationId = null)
    {
        lock (_lock)
        {
            _sequence++;
            var record = new CustodyRecord
            {
                DroneId = _droneId,
                EventId = $"{_droneId}:{_sequence}",
                Sequence = _sequence,
                Timestamp = DateTime.UtcNow,
                EventType = eventType,
                TargetSystem = targetSystem ?? "local",
                Action = action,
                Arguments = arguments,
                Result = result,
                Success = success,
                CorrelationId = correlationId,
                PrevHash = _prevHash
            };

            record.Seal();
            _prevHash = record.Hash;
            _lastRecord = record;
            return record;
        }
    }

    /// <summary>
    /// Verify the integrity of a sequence of records. Checks that:
    /// 1. Each record's hash matches its content
    /// 2. Each record's PrevHash matches the previous record's Hash
    /// 3. Sequence numbers are monotonically increasing
    /// </summary>
    /// <returns>True if the entire chain is valid.</returns>
    public static bool VerifyChain(IEnumerable<CustodyRecord> records)
    {
        CustodyRecord? prev = null;
        long expectedSeq = 0;

        foreach (var record in records)
        {
            // Check sequence continuity
            expectedSeq++;
            if (record.Sequence != expectedSeq)
                return false;

            // Check hash chain
            if (!record.VerifyChain(prev))
                return false;

            prev = record;
        }

        return true;
    }

    /// <summary>
    /// Compute the Merkle root over a batch of records using their content hashes as leaves.
    /// This enables O(log N) batch verification instead of O(N) chain walk.
    /// </summary>
    /// <param name="records">Records in the batch (must have Hash set).</param>
    /// <returns>Hex-encoded Merkle root (64 chars).</returns>
    public static string ComputeBatchMerkleRoot(IEnumerable<CustodyRecord> records)
    {
        var leaves = records
            .Where(r => !string.IsNullOrEmpty(r.Hash))
            .Select(r => Convert.FromHexString(r.Hash))
            .ToArray();

        if (leaves.Length == 0)
            return "";

        var root = MerkleTree.ComputeRoot(leaves);
        return Convert.ToHexString(root);
    }

    /// <summary>
    /// Verify a batch of records against a known Merkle root.
    /// All records must have their MerkleRoot field set to the expected root.
    /// </summary>
    /// <param name="records">Records to verify (must have Hash and MerkleRoot set).</param>
    /// <param name="expectedRoot">Expected Merkle root (hex).</param>
    /// <returns>True if all records verify against the root.</returns>
    public static bool VerifyBatchMerkleRoot(IEnumerable<CustodyRecord> records, string expectedRoot)
    {
        var recordList = records.ToList();
        if (recordList.Count == 0) return false;

        // Compute the root from the records' hashes
        var computedRoot = ComputeBatchMerkleRoot(recordList);
        return global::System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            global::System.Text.Encoding.UTF8.GetBytes(computedRoot),
            global::System.Text.Encoding.UTF8.GetBytes(expectedRoot));
    }

    /// <summary>
    /// Assign Merkle roots to a batch of records. Computes the Merkle root over all records
    /// and sets the MerkleRoot field on each record to the same value.
    /// </summary>
    /// <param name="records">Records to assign roots to (must have Hash set).</param>
    /// <returns>The computed Merkle root (hex), or empty if no records.</returns>
    public static string AssignBatchMerkleRoot(IList<CustodyRecord> records)
    {
        if (records.Count == 0) return "";

        var root = ComputeBatchMerkleRoot(records);
        foreach (var record in records)
        {
            record.MerkleRoot = root;
        }
        return root;
    }

    /// <summary>
    /// Build a Merkle proof for a specific record within a batch.
    /// </summary>
    /// <param name="records">All records in the batch (must have Hash set).</param>
    /// <param name="batchIndex">Index of the record to prove (0-based within the batch).</param>
    /// <returns>Array of hex-encoded sibling hashes forming the proof path.</returns>
    public static string[] BuildBatchProof(IList<CustodyRecord> records, int batchIndex)
    {
        var leaves = records
            .Where(r => !string.IsNullOrEmpty(r.Hash))
            .Select(r => Convert.FromHexString(r.Hash))
            .ToArray();

        if (leaves.Length == 0 || batchIndex < 0 || batchIndex >= leaves.Length)
            return Array.Empty<string>();

        var proof = MerkleTree.BuildProof(leaves, batchIndex);
        return proof.Select(p => Convert.ToHexString(p)).ToArray();
    }

    /// <summary>
    /// Verify the chain starting from this chain's known state.
    /// Useful when resuming from a persisted state.
    /// </summary>
    public bool VerifyContinuation(IEnumerable<CustodyRecord> newRecords)
    {
        lock (_lock)
        {
            var expectedSeq = _sequence;
            var expectedPrevHash = _prevHash;

            foreach (var record in newRecords)
            {
                expectedSeq++;
                if (record.Sequence != expectedSeq) return false;
                if (record.PrevHash != expectedPrevHash) return false;
                if (!record.VerifyHash()) return false;

                expectedPrevHash = record.Hash;
            }

            return true;
        }
    }

    /// <summary>
    /// Reset the chain to a known state (e.g., after loading from persisted records).
    /// </summary>
    public void ResetTo(long sequence, string lastHash, CustodyRecord? lastRecord = null)
    {
        lock (_lock)
        {
            _sequence = sequence;
            _prevHash = lastHash;
            _lastRecord = lastRecord;
        }
    }
}
