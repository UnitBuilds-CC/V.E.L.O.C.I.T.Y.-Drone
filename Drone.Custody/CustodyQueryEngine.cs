using global::System.Text.Json;
using Drone.Core.Custody;

namespace Drone.Custody;

/// <summary>
/// Query engine for the custody trail. Supports querying by drone ID, time range,
/// correlation ID, and event type. Wraps CustodyLogStore with higher-level query operations.
/// Includes Merkle proof generation for O(log N) record verification.
/// </summary>
public class CustodyQueryEngine
{
    private readonly CustodyLogStore _store;

    public CustodyQueryEngine(CustodyLogStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Query custody records with flexible filtering.
    /// </summary>
    /// <param name="droneId">Filter by drone ID (null for all drones).</param>
    /// <param name="from">Start of time range (null for no lower bound).</param>
    /// <param name="to">End of time range (null for no upper bound).</param>
    /// <param name="correlationId">Filter by correlation ID (null for no filter).</param>
    /// <param name="eventType">Filter by event type (null for no filter).</param>
    /// <param name="limit">Maximum number of records to return (default 100).</param>
    /// <returns>Matching custody records in chronological order.</returns>
    public CustodyRecord[] Query(string? droneId = null, DateTime? from = null,
        DateTime? to = null, string? correlationId = null, string? eventType = null,
        int limit = 100)
    {
        CustodyRecord[] results;

        // Priority: correlation > drone > eventType > time range > merged timeline
        if (!string.IsNullOrEmpty(correlationId))
        {
            results = _store.GetRecordsByCorrelation(correlationId);
        }
        else if (!string.IsNullOrEmpty(droneId))
        {
            results = _store.GetDroneRecords(droneId);

            // Apply time filter if provided
            if (from.HasValue)
                results = results.Where(r => r.Timestamp >= from.Value).ToArray();
            if (to.HasValue)
                results = results.Where(r => r.Timestamp <= to.Value).ToArray();
        }
        else if (!string.IsNullOrEmpty(eventType))
        {
            results = _store.GetRecordsByEventType(eventType);

            // Apply time filter if provided
            if (from.HasValue)
                results = results.Where(r => r.Timestamp >= from.Value).ToArray();
            if (to.HasValue)
                results = results.Where(r => r.Timestamp <= to.Value).ToArray();
        }
        else if (from.HasValue && to.HasValue)
        {
            results = _store.GetRecordsByTimeRange(from.Value, to.Value);
        }
        else
        {
            // Return merged timeline (most recent)
            var all = _store.GetMergedTimeline();
            results = all.Skip(Math.Max(0, all.Length - limit)).ToArray();
        }

        // Apply limit
        if (results.Length > limit)
            results = results.Take(limit).ToArray();

        return results;
    }

    /// <summary>
    /// Get the full custody trail for a specific drone, verified for chain integrity.
    /// </summary>
    /// <param name="droneId">The drone to query.</param>
    /// <returns>Tuple of (records, chainValid) -- chainValid is false if any hash breaks detected.</returns>
    public (CustodyRecord[] Records, bool ChainValid) GetVerifiedDroneTrail(string droneId)
    {
        var records = _store.GetDroneRecords(droneId);
        var chainValid = CustodyChain.VerifyChain(records);
        return (records, chainValid);
    }

    /// <summary>
    /// Get the full custody trail for a drone with Merkle batch verification.
    /// Returns chain validity plus per-batch Merkle verification results.
    /// </summary>
    public (CustodyRecord[] Records, bool ChainValid, int TotalBatches, int ValidBatches) GetVerifiedDroneTrailMerkle(string droneId)
    {
        var (totalBatches, validBatches, chainValid) = _store.VerifyDroneTrailMerkle(droneId);
        var records = _store.GetDroneRecords(droneId);
        return (records, chainValid, totalBatches, validBatches);
    }

    /// <summary>
    /// Build a Merkle proof for a specific record within its batch.
    /// The record must belong to a batch with a MerkleRoot assigned.
    /// </summary>
    /// <param name="droneId">The drone that produced the record.</param>
    /// <param name="sequence">Sequence number of the record to prove.</param>
    /// <returns>Proof result, or null if the record is not found or has no MerkleRoot.</returns>
    public MerkleProofResult? GetMerkleProof(string droneId, long sequence)
    {
        var records = _store.GetDroneRecords(droneId);
        if (records.Length == 0) return null;

        // Find the target record
        var targetIndex = Array.FindIndex(records, r => r.Sequence == sequence);
        if (targetIndex < 0) return null;

        var target = records[targetIndex];
        if (string.IsNullOrEmpty(target.MerkleRoot))
            return null;

        // Find all records in the same batch (same MerkleRoot)
        var batchRecords = records
            .Where(r => r.MerkleRoot == target.MerkleRoot)
            .ToList();

        if (batchRecords.Count == 0) return null;

        // Find the target's index within the batch
        var batchIndex = batchRecords.FindIndex(r => r.Sequence == sequence);
        if (batchIndex < 0) return null;

        // Build the proof
        var proof = CustodyChain.BuildBatchProof(batchRecords, batchIndex);

        return new MerkleProofResult
        {
            DroneId = droneId,
            Sequence = sequence,
            ContentHash = target.Hash,
            MerkleRoot = target.MerkleRoot,
            BatchIndex = batchIndex,
            BatchSize = batchRecords.Count,
            ProofPath = proof,
            Verified = MerkleTree.VerifyProofHex(target.MerkleRoot, target.Hash, proof, batchIndex)
        };
    }

    /// <summary>
    /// Get the merged global timeline across all drones.
    /// </summary>
    /// <param name="limit">Maximum records to return.</param>
    /// <returns>Records sorted by timestamp, newest last.</returns>
    public CustodyRecord[] GetGlobalTimeline(int limit = 100)
    {
        var all = _store.GetMergedTimeline();
        if (all.Length > limit)
            return all.Skip(all.Length - limit).ToArray();
        return all;
    }

    /// <summary>
    /// Get all known drone IDs that have reported custody records.
    /// </summary>
    public string[] GetDroneIds() => _store.GetDroneIds();

    /// <summary>
    /// Get a summary of the custody trail status.
    /// </summary>
    public CustodyTrailSummary GetSummary()
    {
        var droneIds = _store.GetDroneIds();
        return new CustodyTrailSummary
        {
            TotalRecords = _store.TotalRecords,
            DroneCount = droneIds.Length,
            DroneIds = droneIds,
            MergedTimelineCount = _store.GetMergedTimeline().Length
        };
    }
}

/// <summary>Result of a Merkle proof computation for a custody record.</summary>
public class MerkleProofResult
{
    /// <summary>Drone that produced the record.</summary>
    public string DroneId { get; set; } = "";

    /// <summary>Sequence number of the proven record.</summary>
    public long Sequence { get; set; }

    /// <summary>Content hash of the record (hex).</summary>
    public string ContentHash { get; set; } = "";

    /// <summary>Merkle root of the batch (hex).</summary>
    public string MerkleRoot { get; set; } = "";

    /// <summary>Index of the record within the batch (0-based).</summary>
    public int BatchIndex { get; set; }

    /// <summary>Total records in the batch.</summary>
    public int BatchSize { get; set; }

    /// <summary>Merkle proof path (array of hex sibling hashes, bottom to top).</summary>
    public string[] ProofPath { get; set; } = Array.Empty<string>();

    /// <summary>Whether the proof verified successfully.</summary>
    public bool Verified { get; set; }
}

/// <summary>Summary of the custody trail state.</summary>
public class CustodyTrailSummary
{
    /// <summary>Total records stored across all drones.</summary>
    public int TotalRecords { get; set; }

    /// <summary>Number of distinct drones that have reported records.</summary>
    public int DroneCount { get; set; }

    /// <summary>List of drone IDs.</summary>
    public string[] DroneIds { get; set; } = Array.Empty<string>();

    /// <summary>Number of records in the merged global timeline.</summary>
    public int MergedTimelineCount { get; set; }
}
