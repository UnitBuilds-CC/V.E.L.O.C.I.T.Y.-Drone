namespace Drone.Core.Config;

/// <summary>
/// Master configuration for the Velocity Drone agent.
/// Loaded from appsettings.json or environment variables.
/// </summary>
public class DroneConfig
{
    /// <summary>Drone identity â€” username when connecting to services.</summary>
    public string DroneId { get; set; } = "Drone";

    /// <summary>Operating mode: full (screen+input+services) or headless (services only).</summary>
    public DroneMode Mode { get; set; } = DroneMode.Full;

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
}

public enum DroneMode
{
    /// <summary>Full capabilities: screen capture, input simulation, all services.</summary>
    Full,
    /// <summary>Headless: no screen/input, services and system commands only. For cloud VMs.</summary>
    Headless
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
}

public class MessengerConfig
{
    /// <summary>Messenger server WebSocket URL (e.g. ws://host:5000/ws).</summary>
    public string? ServerUrl { get; set; }

    /// <summary>Connection secret for authentication.</summary>
    public string? ConnectionSecret { get; set; }

    /// <summary>Enable auto-reconnect.</summary>
    public bool AutoReconnect { get; set; } = true;
}

public class ShareConfig
{
    /// <summary>Share server base URL (e.g. http://host:5002).</summary>
    public string? ServerUrl { get; set; }

    /// <summary>Admin API key for REST operations.</summary>
    public string? AdminApiKey { get; set; }

    /// <summary>WebSocket token for real-time sync.</summary>
    public string? WebSocketToken { get; set; }
}

public class RemoteConfig
{
    /// <summary>Remote server WebSocket URL (e.g. ws://host:5003/ws).</summary>
    public string? ServerUrl { get; set; }

    /// <summary>API key for authentication.</summary>
    public string? ApiKey { get; set; }
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
}
