using global::System.Security.Cryptography;
using global::System.Text.Json;
using global::System.Text.Json.Serialization;

namespace Drone.Core.Custody;

/// <summary>
/// A single custody record in the audit trail. Each record is hash-chained to the previous one,
/// creating a tamper-evident log of every action the agent takes across all connected systems.
/// </summary>
public class CustodyRecord
{
    /// <summary>Which drone agent produced this record.</summary>
    [JsonPropertyName("droneId")]
    public string DroneId { get; set; } = "";

    /// <summary>Globally unique event ID: DroneId + monotonic sequence.</summary>
    [JsonPropertyName("eventId")]
    public string EventId { get; set; } = "";

    /// <summary>Monotonic sequence number within this drone's timeline.</summary>
    [JsonPropertyName("sequence")]
    public long Sequence { get; set; }

    /// <summary>UTC timestamp with high resolution.</summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    /// <summary>Event category: "tool_call", "connection", "security", "cross_machine".</summary>
    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = "";

    /// <summary>Which system was affected: "local", "drone:xyz", "share-server", "messenger".</summary>
    [JsonPropertyName("targetSystem")]
    public string TargetSystem { get; set; } = "local";

    /// <summary>What action was performed: "run_command", "read_file", "send_message", etc.</summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    /// <summary>Sanitized arguments (no secrets). Null for events with no arguments.</summary>
    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }

    /// <summary>Result summary: success/failure + brief description. Not full output.</summary>
    [JsonPropertyName("result")]
    public string? Result { get; set; }

    /// <summary>True if the action succeeded, false otherwise.</summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;

    /// <summary>Links multi-step cross-machine sequences together.</summary>
    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    /// <summary>SHA-256 hash of the previous record in the chain. Empty for the genesis record.</summary>
    [JsonPropertyName("prevHash")]
    public string PrevHash { get; set; } = "";

    /// <summary>SHA-256 hash of this record's content (excluding prevHash and hash fields).</summary>
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";

    /// <summary>
    /// Compute the content hash of this record (excluding prevHash and hash fields).
    /// Used for both computing and verifying the hash chain.
    /// </summary>
    public string ComputeHash()
    {
        var content = $"{DroneId}|{EventId}|{Sequence}|{Timestamp:O}|{EventType}|{TargetSystem}|{Action}|{Arguments}|{Result}|{Success}|{CorrelationId}";
        var bytes = global::System.Text.Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Compute and set the Hash field based on this record's content.
    /// </summary>
    public void Seal()
    {
        Hash = ComputeHash();
    }

    /// <summary>
    /// Verify that this record's hash matches its content.
    /// Returns false if the record has been tampered with.
    /// </summary>
    public bool VerifyHash()
    {
        if (string.IsNullOrEmpty(Hash)) return false;
        return ComputeHash() == Hash;
    }

    /// <summary>
    /// Verify that this record properly chains to the previous one.
    /// </summary>
    /// <param name="previousRecord">The previous record in the chain (null for genesis).</param>
    public bool VerifyChain(CustodyRecord? previousRecord)
    {
        // Verify this record's own hash
        if (!VerifyHash()) return false;

        // Genesis record has no previous
        if (previousRecord == null)
            return string.IsNullOrEmpty(PrevHash);

        // This record's PrevHash must match the previous record's Hash
        return PrevHash == previousRecord.Hash;
    }

    /// <summary>Serialize to JSON for storage/transmission.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Deserialize from JSON.</summary>
    public static CustodyRecord? FromJson(string json) => JsonSerializer.Deserialize<CustodyRecord>(json, JsonOptions);

    /// <summary>Shared JSON options for consistent serialization.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
