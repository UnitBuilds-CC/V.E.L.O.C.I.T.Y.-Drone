using global::System.Text.Json;
using Drone.Core.Custody;

namespace Drone.Custody;

/// <summary>
/// Query engine for the custody trail. Supports querying by drone ID, time range,
/// correlation ID, and event type. Wraps CustodyLogStore with higher-level query operations.
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
    /// <returns>Tuple of (records, chainValid) — chainValid is false if any hash breaks detected.</returns>
    public (CustodyRecord[] Records, bool ChainValid) GetVerifiedDroneTrail(string droneId)
    {
        var records = _store.GetDroneRecords(droneId);
        var chainValid = CustodyChain.VerifyChain(records);
        return (records, chainValid);
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
