using global::System.Text.Json;

namespace Drone.Core.Config;

/// <summary>
/// Master configuration for the Velocity Drone agent.
/// Loaded from appsettings.json or environment variables.
/// </summary>
public class DroneConfig
{
    /// <summary>Drone identity — username when connecting to services.</summary>
    public string DroneId { get; set; } = "Drone";

    /// <summary>Operating mode: full (screen+input+services) or headless (services only).</summary>
    public DroneMode Mode { get; set; } = DroneMode.Full;

    /// <summary>Drone role: server (hosts relay), client (connects to relay), or standalone (both).</summary>
    public DroneRole Role { get; set; } = DroneRole.Standalone;

    /// <summary>Connection settings for the Velocity uplink.</summary>
    public UplinkConfig Uplink { get; set; } = new();

    /// <summary>Messenger service connector settings.</summary>
    public MessengerConfig Messenger { get; set; } = new();

    /// <summary>Share service connector settings.</summary>
    public ShareConfig Share { get; set; } = new();

    /// <summary>Remote service connector settings.</summary>
    public RemoteConfig Remote { get; set; } = new();

    /// <summary>Autonomy engine settings.</summary>
    public AutonomyConfig Autonomy { get; set; } = new();

    /// <summary>MCP server settings.</summary>
    public McpConfig Mcp { get; set; } = new();

    /// <summary>Relay server settings (for server/standalone roles).</summary>
    public RelayConfig Relay { get; set; } = new();

    /// <summary>
    /// Validates all config sections. Throws InvalidOperationException if any section is invalid.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DroneId))
            throw new InvalidOperationException("DroneId must not be empty");
        Uplink.Validate();
        Messenger.Validate();
        Share.Validate();
        Remote.Validate();
        Autonomy.Validate();
        Mcp.Validate();
        Relay.Validate();
    }

    public static DroneConfig Load(string path)
    {
        if (!File.Exists(path)) return new DroneConfig();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<DroneConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new DroneConfig();
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}

public enum DroneMode
{
    /// <summary>Full capabilities: screen capture, input simulation, all services.</summary>
    Full,
    /// <summary>Headless: no screen/input, services and system commands only. For cloud VMs.</summary>
    Headless
}

/// <summary>
/// Drone role in the relay architecture.
/// </summary>
public enum DroneRole
{
    /// <summary>Hosts relay server and connects as client (single-machine mode).</summary>
    Standalone,
    /// <summary>Hosts relay server for other drones to connect to.</summary>
    Server,
    /// <summary>Connects to an external relay server.</summary>
    Client
}

public class UplinkConfig
{
    /// <summary>Transport mode: nmcp (shared memory), websocket, or auto.</summary>
    public string Transport { get; set; } = "auto";

    /// <summary>WebSocket URL for remote uplink (e.g. ws://host:port/uplink).</summary>
    public string? WebSocketUrl { get; set; }

    /// <summary>Path to shared memory buffer file for NMCP mode.</summary>
    public string BufferPath { get; set; } = "nmcp_drone.bin";

    /// <summary>Buffer size in bytes (default 4MB).</summary>
    public int BufferSize { get; set; } = 4 * 1024 * 1024;

    /// <summary>Enable auto-reconnect on connection loss.</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Max reconnect attempts before giving up.</summary>
    public int MaxReconnectAttempts { get; set; } = 10;

    public void Validate()
    {
        if (BufferSize <= 0 || BufferSize > 64 * 1024 * 1024)
            throw new InvalidOperationException($"Uplink.BufferSize must be between 1 and 67108864 (64MB), got {BufferSize}");

        if (Transport != "auto" && Transport != "websocket" && Transport != "shmem")
            throw new InvalidOperationException($"Uplink.Transport must be 'auto', 'websocket', or 'shmem', got '{Transport}'");

        if ((Transport == "websocket" || Transport == "auto") && !string.IsNullOrEmpty(WebSocketUrl) &&
            !Uri.TryCreate(WebSocketUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException($"Uplink.WebSocketUrl is not a valid URI: '{WebSocketUrl}'");
    }
}

public class MessengerConfig
{
    /// <summary>Messenger server WebSocket URL (e.g. ws://host:5000/ws).</summary>
    public string? ServerUrl { get; set; }

    /// <summary>Connection secret for authentication.</summary>
    public string? ConnectionSecret { get; set; }

    /// <summary>Enable auto-reconnect.</summary>
    public bool AutoReconnect { get; set; } = true;

    public void Validate()
    {
        if (!string.IsNullOrEmpty(ServerUrl) && !Uri.TryCreate(ServerUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException($"Messenger.ServerUrl is not a valid URI: '{ServerUrl}'");
    }
}

public class ShareConfig
{
    /// <summary>Whether the share server is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Share server base URL (e.g. http://host:5002).</summary>
    public string? ServerUrl { get; set; }

    /// <summary>Admin API key for REST operations.</summary>
    public string? AdminApiKey { get; set; }

    /// <summary>WebSocket token for real-time sync.</summary>
    public string? WebSocketToken { get; set; }

    public void Validate()
    {
        if (!string.IsNullOrEmpty(ServerUrl) && !Uri.TryCreate(ServerUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException($"Share.ServerUrl is not a valid URI: '{ServerUrl}'");
    }
}

public class RemoteConfig
{
    /// <summary>Remote server WebSocket URL (e.g. ws://host:5003/ws).</summary>
    public string? ServerUrl { get; set; }

    /// <summary>API key for authentication.</summary>
    public string? ApiKey { get; set; }

    public void Validate()
    {
        if (!string.IsNullOrEmpty(ServerUrl) && !Uri.TryCreate(ServerUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException($"Remote.ServerUrl is not a valid URI: '{ServerUrl}'");
    }
}

public class AutonomyConfig
{
    /// <summary>Enable the autonomy engine.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Path to behavior rules config file.</summary>
    public string RulesPath { get; set; } = "rules.json";

    /// <summary>Screen monitoring interval in seconds (0 = disabled).</summary>
    public int ScreenMonitorIntervalSec { get; set; } = 0;

    /// <summary>System metrics collection interval in seconds (0 = disabled).</summary>
    public int SystemMetricsIntervalSec { get; set; } = 30;

    /// <summary>Process monitor interval in seconds (0 = disabled).</summary>
    public int ProcessMonitorIntervalSec { get; set; } = 10;

    /// <summary>Scheduled task poll interval in seconds (0 = disabled).</summary>
    public int ScheduledTaskPollSec { get; set; } = 60;

    public void Validate()
    {
        if (ScreenMonitorIntervalSec < 0)
            throw new InvalidOperationException($"Autonomy.ScreenMonitorIntervalSec must be >= 0, got {ScreenMonitorIntervalSec}");
        if (SystemMetricsIntervalSec < 0)
            throw new InvalidOperationException($"Autonomy.SystemMetricsIntervalSec must be >= 0, got {SystemMetricsIntervalSec}");
        if (ProcessMonitorIntervalSec < 0)
            throw new InvalidOperationException($"Autonomy.ProcessMonitorIntervalSec must be >= 0, got {ProcessMonitorIntervalSec}");
        if (ScheduledTaskPollSec < 0)
            throw new InvalidOperationException($"Autonomy.ScheduledTaskPollSec must be >= 0, got {ScheduledTaskPollSec}");
        if (Enabled && string.IsNullOrWhiteSpace(RulesPath))
            throw new InvalidOperationException("Autonomy.RulesPath must not be empty when Autonomy is enabled");
    }
}

public class McpConfig
{
    /// <summary>Path to shared memory buffer for MCP NMCP protocol.</summary>
    public string BufferPath { get; set; } = "nmcp_mcp.bin";

    /// <summary>Buffer size in bytes (default 1MB).</summary>
    public int BufferSize { get; set; } = 1048576;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BufferPath))
            throw new InvalidOperationException("Mcp.BufferPath must not be empty");
        if (BufferSize <= 0 || BufferSize > 64 * 1024 * 1024)
            throw new InvalidOperationException($"Mcp.BufferSize must be between 1 and 67108864 (64MB), got {BufferSize}");
    }
}

/// <summary>
/// Relay server configuration for drone-to-drone communication.
/// </summary>
public class RelayConfig
{
    /// <summary>Whether the relay server is enabled (auto-set by Role).</summary>
    public bool Enabled { get; set; }

    /// <summary>Port for the relay server to listen on.</summary>
    public int Port { get; set; } = 9200;

    /// <summary>Shared API key for drone authentication.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Storage path for file share service.</summary>
    public string StoragePath { get; set; } = "relay_data";

    /// <summary>Maximum concurrent WebSocket connections.</summary>
    public int MaxConnections { get; set; } = 32;

    /// <summary>Maximum upload file size in bytes (default 100 MB).</summary>
    public long MaxUploadSize { get; set; } = 100 * 1024 * 1024;

    /// <summary>Maximum total storage quota in bytes (default 1 GB).</summary>
    public long StorageQuotaBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>Max messages per second per drone (0 = unlimited).</summary>
    public int MaxMessagesPerSecond { get; set; } = 30;

    /// <summary>Path to PFX certificate file for TLS. If set, relay also listens on HTTPS.</summary>
    public string? TlsCertificatePath { get; set; }

    /// <summary>Password for the PFX certificate file.</summary>
    public string? TlsCertificatePassword { get; set; }

    /// <summary>WebSocket URL of the relay server (for client role).</summary>
    public string? RelayUrl { get; set; }

    public void Validate()
    {
        if (Port < 1 || Port > 65535)
            throw new InvalidOperationException($"Relay.Port must be between 1 and 65535, got {Port}");
        if (MaxConnections < 1 || MaxConnections > 1000)
            throw new InvalidOperationException($"Relay.MaxConnections must be between 1 and 1000, got {MaxConnections}");
        if (MaxUploadSize < 1024 || MaxUploadSize > 10L * 1024 * 1024 * 1024)
            throw new InvalidOperationException($"Relay.MaxUploadSize must be between 1KB and 10GB, got {MaxUploadSize}");
        if (StorageQuotaBytes < 1024 || StorageQuotaBytes > 100L * 1024 * 1024 * 1024)
            throw new InvalidOperationException($"Relay.StorageQuotaBytes must be between 1KB and 100GB, got {StorageQuotaBytes}");
        if (MaxMessagesPerSecond < 0 || MaxMessagesPerSecond > 10000)
            throw new InvalidOperationException($"Relay.MaxMessagesPerSecond must be between 0 and 10000, got {MaxMessagesPerSecond}");
        if (!string.IsNullOrEmpty(TlsCertificatePath) && !global::System.IO.File.Exists(TlsCertificatePath))
            throw new InvalidOperationException($"Relay.TlsCertificatePath file not found: '{TlsCertificatePath}'");
        if (!string.IsNullOrEmpty(RelayUrl) && !Uri.TryCreate(RelayUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException($"Relay.RelayUrl is not a valid URI: '{RelayUrl}'");
    }
}
