//! Drone configuration — TOML-based with environment variable overrides.

use serde::{Deserialize, Serialize};
use std::path::Path;

/// Master configuration for the Velocity Drone agent.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DroneConfig {
    /// Drone identity — username when connecting to services.
    #[serde(default = "default_drone_id")]
    pub drone_id: String,

    /// Operating mode: full (screen+input+services) or headless (services only).
    #[serde(default)]
    pub mode: DroneMode,

    /// Connection settings for the Velocity uplink.
    #[serde(default)]
    pub uplink: UplinkConfig,

    /// Messenger service connector settings.
    #[serde(default)]
    pub messenger: MessengerConfig,

    /// Share service connector settings.
    #[serde(default)]
    pub share: ShareConfig,

    /// Remote service connector settings.
    #[serde(default)]
    pub remote: RemoteConfig,

    /// Autonomy engine settings.
    #[serde(default)]
    pub autonomy: AutonomyConfig,

    /// MCP server settings.
    #[serde(default)]
    pub mcp: McpConfig,
}

fn default_drone_id() -> String { "Drone".to_string() }

impl Default for DroneConfig {
    fn default() -> Self {
        Self {
            drone_id: default_drone_id(),
            mode: DroneMode::default(),
            uplink: UplinkConfig::default(),
            messenger: MessengerConfig::default(),
            share: ShareConfig::default(),
            remote: RemoteConfig::default(),
            autonomy: AutonomyConfig::default(),
            mcp: McpConfig::default(),
        }
    }
}

impl DroneConfig {
    /// Load configuration from a TOML file. Returns defaults if file doesn't exist.
    pub fn load(path: &Path) -> anyhow::Result<Self> {
        if !path.exists() {
            return Ok(Self::default());
        }
        let content = std::fs::read_to_string(path)?;
        let config: DroneConfig = toml::from_str(&content)?;
        Ok(config)
    }

    /// Save configuration to a TOML file.
    pub fn save(&self, path: &Path) -> anyhow::Result<()> {
        let content = toml::to_string_pretty(self)?;
        if let Some(dir) = path.parent() {
            std::fs::create_dir_all(dir)?;
        }
        std::fs::write(path, content)?;
        Ok(())
    }

    /// Apply environment variable overrides.
    pub fn apply_env_overrides(&mut self) {
        if let Ok(id) = std::env::var("DRONE_ID") {
            if !id.is_empty() { self.drone_id = id; }
        }
        if let Ok(mode) = std::env::var("DRONE_MODE") {
            if mode.eq_ignore_ascii_case("headless") {
                self.mode = DroneMode::Headless;
            }
        }
        if let Ok(url) = std::env::var("DRONE_WS_URL") {
            if !url.is_empty() { self.uplink.websocket_url = Some(url); }
        }
    }

    /// Validate all config sections.
    pub fn validate(&self) -> Result<(), ConfigError> {
        if self.drone_id.trim().is_empty() {
            return Err(ConfigError::Validation("DroneId must not be empty".into()));
        }
        self.uplink.validate()?;
        self.messenger.validate()?;
        self.share.validate()?;
        self.remote.validate()?;
        self.autonomy.validate()?;
        self.mcp.validate()?;
        Ok(())
    }
}

/// Operating mode for the drone.
#[derive(Debug, Clone, Copy, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum DroneMode {
    /// Full capabilities: screen capture, input simulation, all services.
    #[default]
    Full,
    /// Headless: no screen/input, services and system commands only. For cloud VMs.
    Headless,
}

/// Uplink (Velocity connection) configuration.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct UplinkConfig {
    /// Transport mode: "auto", "websocket", or "shmem".
    #[serde(default = "default_transport")]
    pub transport: String,

    /// WebSocket URL for remote uplink.
    #[serde(default)]
    pub websocket_url: Option<String>,

    /// Path to shared memory buffer file for NMCP mode.
    #[serde(default = "default_buffer_path")]
    pub buffer_path: String,

    /// Buffer size in bytes (default 4MB).
    #[serde(default = "default_uplink_buffer_size")]
    pub buffer_size: usize,

    /// Enable auto-reconnect on connection loss.
    #[serde(default = "default_true")]
    pub auto_reconnect: bool,

    /// Max reconnect attempts before giving up.
    #[serde(default = "default_max_reconnect")]
    pub max_reconnect_attempts: u32,
}

fn default_transport() -> String { "auto".into() }
fn default_buffer_path() -> String { "nmcp_drone.bin".into() }
fn default_uplink_buffer_size() -> usize { 4 * 1024 * 1024 }
fn default_true() -> bool { true }
fn default_max_reconnect() -> u32 { 10 }

impl Default for UplinkConfig {
    fn default() -> Self {
        Self {
            transport: default_transport(),
            websocket_url: None,
            buffer_path: default_buffer_path(),
            buffer_size: default_uplink_buffer_size(),
            auto_reconnect: true,
            max_reconnect_attempts: default_max_reconnect(),
        }
    }
}

impl UplinkConfig {
    pub fn validate(&self) -> Result<(), ConfigError> {
        if self.buffer_size == 0 || self.buffer_size > 64 * 1024 * 1024 {
            return Err(ConfigError::Validation(format!(
                "Uplink.buffer_size must be between 1 and 67108864 (64MB), got {}", self.buffer_size
            )));
        }
        if !["auto", "websocket", "shmem"].contains(&self.transport.as_str()) {
            return Err(ConfigError::Validation(format!(
                "Uplink.transport must be 'auto', 'websocket', or 'shmem', got '{}'", self.transport
            )));
        }
        Ok(())
    }
}

/// Messenger connector configuration.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct MessengerConfig {
    /// Messenger server WebSocket URL.
    #[serde(default)]
    pub server_url: Option<String>,

    /// Connection secret for authentication.
    #[serde(default)]
    pub connection_secret: Option<String>,

    /// Enable auto-reconnect.
    #[serde(default = "default_true")]
    pub auto_reconnect: bool,
}

impl MessengerConfig {
    pub fn validate(&self) -> Result<(), ConfigError> {
        Ok(()) // URL validation deferred to connect time
    }
}

/// Share connector configuration.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct ShareConfig {
    /// Whether the share server is enabled.
    #[serde(default)]
    pub enabled: bool,

    /// Share server base URL.
    #[serde(default)]
    pub server_url: Option<String>,

    /// Admin API key for REST operations.
    #[serde(default)]
    pub admin_api_key: Option<String>,

    /// WebSocket token for real-time sync.
    #[serde(default)]
    pub websocket_token: Option<String>,
}

impl ShareConfig {
    pub fn validate(&self) -> Result<(), ConfigError> {
        Ok(())
    }
}

/// Remote connector configuration.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct RemoteConfig {
    /// Remote server WebSocket URL.
    #[serde(default)]
    pub server_url: Option<String>,

    /// API key for authentication.
    #[serde(default)]
    pub api_key: Option<String>,
}

impl RemoteConfig {
    pub fn validate(&self) -> Result<(), ConfigError> {
        Ok(())
    }
}

/// Autonomy engine configuration.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AutonomyConfig {
    /// Enable the autonomy engine.
    #[serde(default = "default_true")]
    pub enabled: bool,

    /// Path to behavior rules config file.
    #[serde(default = "default_rules_path")]
    pub rules_path: String,

    /// Screen monitoring interval in seconds (0 = disabled).
    #[serde(default)]
    pub screen_monitor_interval_sec: u64,

    /// System metrics collection interval in seconds (0 = disabled).
    #[serde(default = "default_metrics_interval")]
    pub system_metrics_interval_sec: u64,

    /// Process monitor interval in seconds (0 = disabled).
    #[serde(default = "default_process_interval")]
    pub process_monitor_interval_sec: u64,

    /// Scheduled task poll interval in seconds (0 = disabled).
    #[serde(default = "default_sched_poll")]
    pub scheduled_task_poll_sec: u64,
}

fn default_rules_path() -> String { "rules.json".into() }
fn default_metrics_interval() -> u64 { 30 }
fn default_process_interval() -> u64 { 10 }
fn default_sched_poll() -> u64 { 60 }

impl Default for AutonomyConfig {
    fn default() -> Self {
        Self {
            enabled: true,
            rules_path: default_rules_path(),
            screen_monitor_interval_sec: 0,
            system_metrics_interval_sec: default_metrics_interval(),
            process_monitor_interval_sec: default_process_interval(),
            scheduled_task_poll_sec: default_sched_poll(),
        }
    }
}

impl AutonomyConfig {
    pub fn validate(&self) -> Result<(), ConfigError> {
        Ok(())
    }
}

/// MCP server configuration.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct McpConfig {
    /// Path to shared memory buffer for MCP NMCP protocol.
    #[serde(default = "default_mcp_buffer_path")]
    pub buffer_path: String,

    /// Buffer size in bytes (default 1MB).
    #[serde(default = "default_mcp_buffer_size")]
    pub buffer_size: usize,
}

fn default_mcp_buffer_path() -> String { "nmcp_mcp.bin".into() }
fn default_mcp_buffer_size() -> usize { 1048576 }

impl Default for McpConfig {
    fn default() -> Self {
        Self {
            buffer_path: default_mcp_buffer_path(),
            buffer_size: default_mcp_buffer_size(),
        }
    }
}

impl McpConfig {
    pub fn validate(&self) -> Result<(), ConfigError> {
        if self.buffer_path.trim().is_empty() {
            return Err(ConfigError::Validation("Mcp.buffer_path must not be empty".into()));
        }
        if self.buffer_size == 0 || self.buffer_size > 64 * 1024 * 1024 {
            return Err(ConfigError::Validation(format!(
                "Mcp.buffer_size must be between 1 and 67108864 (64MB), got {}", self.buffer_size
            )));
        }
        Ok(())
    }
}

/// Configuration errors.
#[derive(Debug, thiserror::Error)]
pub enum ConfigError {
    #[error("Config validation error: {0}")]
    Validation(String),

    #[error("Config I/O error: {0}")]
    Io(#[from] std::io::Error),

    #[error("Config parse error: {0}")]
    Parse(#[from] toml::de::Error),
}
