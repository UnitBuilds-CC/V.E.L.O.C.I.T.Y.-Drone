using global::System.Collections.Concurrent;
using global::System.Security.Cryptography;

namespace Drone.Core.Custody;

/// <summary>
/// Generates and tracks correlation IDs for cross-machine action sequences.
/// A correlation ID ties together all steps of a multi-step operation that
/// spans multiple drones/systems.
/// </summary>
public class CorrelationTracker
{
    /// <summary>Active correlations with their creation timestamp and description.</summary>
    private readonly ConcurrentDictionary<string, CorrelationEntry> _active = new();

    /// <summary>Maximum number of concurrent active correlations (prevents memory leak).</summary>
    private const int MaxActive = 10_000;

    /// <summary>Maximum age of a correlation before it's considered stale (24 hours).</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>
    /// Generate a new correlation ID from the triggering context.
    /// </summary>
    /// <param name="droneId">The drone initiating the action.</param>
    /// <param name="triggerEventId">The event that triggered this cross-machine sequence.</param>
    /// <param name="description">Human-readable description of the sequence.</param>
    /// <returns>The correlation ID.</returns>
    public string Create(string droneId, string triggerEventId, string description = "")
    {
        EvictStale();

        var timestamp = DateTime.UtcNow;
        var content = $"{droneId}|{timestamp:O}|{triggerEventId}";
        var bytes = global::System.Text.Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        var correlationId = "corr-" + Convert.ToHexString(hash)[..16].ToLowerInvariant();

        _active[correlationId] = new CorrelationEntry
        {
            CorrelationId = correlationId,
            DroneId = droneId,
            TriggerEventId = triggerEventId,
            Description = description,
            CreatedAt = timestamp,
            StepCount = 0
        };

        return correlationId;
    }

    /// <summary>
    /// Record a step in an existing correlation. Increments the step counter.
    /// </summary>
    /// <returns>True if the correlation exists, false if it's unknown or expired.</returns>
    public bool RecordStep(string correlationId, string droneId, string action)
    {
        if (!_active.TryGetValue(correlationId, out var entry))
            return false;

        if (DateTime.UtcNow - entry.CreatedAt > MaxAge)
        {
            _active.TryRemove(correlationId, out _);
            return false;
        }

        Interlocked.Increment(ref entry.StepCount);
        entry.LastStepAt = DateTime.UtcNow;
        entry.LastDroneId = droneId;
        entry.LastAction = action;
        return true;
    }

    /// <summary>
    /// Complete (close) a correlation. The correlation is removed from active tracking.
    /// </summary>
    public void Complete(string correlationId)
    {
        _active.TryRemove(correlationId, out _);
    }

    /// <summary>
    /// Check if a correlation ID is still active.
    /// </summary>
    public bool IsActive(string correlationId)
    {
        if (!_active.TryGetValue(correlationId, out var entry))
            return false;

        if (DateTime.UtcNow - entry.CreatedAt > MaxAge)
        {
            _active.TryRemove(correlationId, out _);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Get details of an active correlation.
    /// </summary>
    public CorrelationEntry? GetEntry(string correlationId)
    {
        _active.TryGetValue(correlationId, out var entry);
        return entry;
    }

    /// <summary>Number of currently active correlations.</summary>
    public int ActiveCount => _active.Count;

    /// <summary>Remove correlations older than MaxAge.</summary>
    private void EvictStale()
    {
        if (_active.Count < MaxActive) return;

        var cutoff = DateTime.UtcNow - MaxAge;
        foreach (var kvp in _active)
        {
            if (kvp.Value.CreatedAt < cutoff)
                _active.TryRemove(kvp.Key, out _);
        }
    }
}

/// <summary>Metadata for an active correlation.</summary>
public class CorrelationEntry
{
    public string CorrelationId { get; set; } = "";
    public string DroneId { get; set; } = "";
    public string TriggerEventId { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime LastStepAt { get; set; }
    public int StepCount;
    public string? LastDroneId { get; set; }
    public string? LastAction { get; set; }
}
