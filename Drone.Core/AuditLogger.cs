using global::System.Text.Json;
using global::System.Text.Json.Serialization;

namespace Drone.Core;

/// <summary>
/// Thread-safe audit logger for security-sensitive operations.
/// Writes JSON-lines to a file with automatic daily rotation.
/// Each line is a self-contained JSON record for easy parsing with jq, grep, etc.
/// </summary>
public class AuditLogger : IDisposable
{
    private readonly string _basePath;
    private readonly long _maxFileSizeBytes;
    private readonly ILogger? _logger;
    private StreamWriter? _writer;
    private string _currentFilePath = "";
    private DateTime _currentDate;
    private readonly object _lock = new();
    private bool _disposed;
    private long _failedWrites;

    /// <summary>Create an audit logger. Pass null path to disable auditing.</summary>
    public AuditLogger(string? basePath, long maxFileSizeMb = 50, ILogger? logger = null)
    {
        _basePath = basePath ?? "";
        _maxFileSizeBytes = maxFileSizeMb * 1024 * 1024;
        _logger = logger;
        if (!string.IsNullOrEmpty(_basePath))
        {
            EnsureWriter();
        }
    }

    /// <summary>Number of write failures since startup.</summary>
    public long FailedWrites => _failedWrites;

    /// <summary>Whether audit logging is enabled.</summary>
    public bool IsEnabled => !string.IsNullOrEmpty(_basePath);

    /// <summary>Log a tool call with full context.</summary>
    public void LogToolCall(string clientAddress, string toolName, long durationMs, bool isError, string? errorMessage = null)
    {
        if (!IsEnabled) return;
        WriteRecord(new AuditRecord
        {
            Timestamp = DateTime.UtcNow,
            Event = "tool_call",
            ClientAddress = clientAddress,
            ToolName = toolName,
            DurationMs = durationMs,
            Success = !isError,
            Error = errorMessage
        });
    }

    /// <summary>Log a connection event (connect, disconnect, auth failure).</summary>
    public void LogConnection(string clientAddress, string eventType, string? details = null)
    {
        if (!IsEnabled) return;
        WriteRecord(new AuditRecord
        {
            Timestamp = DateTime.UtcNow,
            Event = eventType,
            ClientAddress = clientAddress,
            Details = details
        });
    }

    /// <summary>Log a security event (rate limit, connection limit, auth rejection).</summary>
    public void LogSecurity(string clientAddress, string eventType, string? details = null)
    {
        if (!IsEnabled) return;
        WriteRecord(new AuditRecord
        {
            Timestamp = DateTime.UtcNow,
            Event = "security_" + eventType,
            ClientAddress = clientAddress,
            Details = details
        });
    }

    private void WriteRecord(AuditRecord record)
    {
        if (_disposed) return;
        lock (_lock)
        {
            try
            {
                EnsureWriter();
                var json = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = false });
                _writer?.WriteLine(json);
                _writer?.Flush();
            }
            catch (Exception ex)
            {
                global::System.Threading.Interlocked.Increment(ref _failedWrites);
                _logger?.LogWarning("Audit write failed: {Error}", ex.Message);
            }
        }
    }

    private void EnsureWriter()
    {
        if (string.IsNullOrEmpty(_basePath)) return;

        var today = DateTime.UtcNow.Date;
        var filePath = GetFilePath(today);

        // Daily rotation: if the date changed, close old writer and open new file
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

        // Size-based rotation: if file exceeds max, rotate to .1, .2, etc.
        try
        {
            var fi = new FileInfo(_currentFilePath);
            if (fi.Exists && fi.Length > _maxFileSizeBytes)
            {
                _writer.Dispose();
                var rotatedPath = _currentFilePath + "." + DateTime.UtcNow.ToString("HHmmss");
                File.Move(_currentFilePath, rotatedPath);
                _writer = new StreamWriter(_currentFilePath, append: true) { AutoFlush = false };

                // Keep only last 5 rotated files
                CleanupOldRotations();
            }
        }
        catch (Exception ex) { _logger?.LogWarning("Audit log rotation failed: {Error}", ex.Message); }
    }

    private string GetFilePath(DateTime date)
    {
        // If basePath ends with .log or .jsonl, use it directly with date suffix
        var ext = Path.GetExtension(_basePath);
        var baseName = Path.GetFileNameWithoutExtension(_basePath);
        var dir = Path.GetDirectoryName(_basePath) ?? ".";
        return Path.Combine(dir, $"{baseName}-{date:yyyy-MM-dd}{ext}");
    }

    private void CleanupOldRotations()
    {
        try
        {
            var dir = Path.GetDirectoryName(_currentFilePath) ?? ".";
            var baseName = Path.GetFileName(_currentFilePath);
            var rotated = Directory.GetFiles(dir, baseName + ".*")
                .OrderByDescending(f => f)
                .Skip(5); // Keep last 5
            foreach (var old in rotated)
            {
                try { File.Delete(old); } catch (Exception ex) { _logger?.LogWarning("Failed to delete old rotation file: {Error}", ex.Message); }
            }
        }
        catch (Exception ex) { _logger?.LogWarning("Rotation cleanup failed: {Error}", ex.Message); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
        GC.SuppressFinalize(this);
    }
}

internal class AuditRecord
{
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }
    [JsonPropertyName("event")]
    public string Event { get; set; } = "";
    [JsonPropertyName("clientAddress")]
    public string? ClientAddress { get; set; }
    [JsonPropertyName("toolName")]
    public string? ToolName { get; set; }
    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    [JsonPropertyName("error")]
    public string? Error { get; set; }
    [JsonPropertyName("details")]
    public string? Details { get; set; }
}
