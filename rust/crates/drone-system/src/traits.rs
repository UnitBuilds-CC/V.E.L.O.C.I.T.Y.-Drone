//! Platform abstraction traits for cross-system support.

use async_trait::async_trait;
use serde::{Deserialize, Serialize};

/// Screen capture interface.
#[async_trait]
pub trait ScreenCapture: Send + Sync {
    /// Capture the entire screen as PNG bytes.
    async fn capture_screen(&self) -> anyhow::Result<Vec<u8>>;

    /// Capture a specific window by handle.
    async fn capture_window(&self, handle: u64) -> anyhow::Result<Vec<u8>>;

    /// Get the primary screen resolution.
    async fn screen_size(&self) -> anyhow::Result<(u32, u32)>;

    /// Get the RGB color of a pixel at (x, y).
    async fn pixel_color(&self, x: i32, y: i32) -> anyhow::Result<(u8, u8, u8)>;
}

/// Input simulation interface.
#[async_trait]
pub trait InputSimulator: Send + Sync {
    async fn type_text(&self, text: &str) -> anyhow::Result<()>;
    async fn press_key(&self, key: &str) -> anyhow::Result<()>;
    async fn move_mouse(&self, x: i32, y: i32) -> anyhow::Result<()>;
    async fn click(&self, x: i32, y: i32, button: MouseButton) -> anyhow::Result<()>;
    async fn drag(&self, from_x: i32, from_y: i32, to_x: i32, to_y: i32) -> anyhow::Result<()>;
    async fn scroll(&self, delta_x: i32, delta_y: i32) -> anyhow::Result<()>;
}

/// Mouse button.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum MouseButton {
    Left,
    Right,
    Middle,
}

/// Process management interface.
#[async_trait]
pub trait ProcessManager: Send + Sync {
    async fn run_command(&self, command: &str, args: &str, working_dir: Option<&str>) -> anyhow::Result<CommandResult>;
    async fn list_processes(&self) -> anyhow::Result<Vec<ProcessInfo>>;
    async fn kill_process(&self, pid: u32) -> anyhow::Result<bool>;
    async fn system_info(&self) -> anyhow::Result<SystemInfo>;
}

/// Result of running a command.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CommandResult {
    pub exit_code: i32,
    pub stdout: String,
    pub stderr: String,
    pub duration_ms: u64,
}

/// Process information.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ProcessInfo {
    pub pid: u32,
    pub name: String,
    pub memory_mb: u64,
    pub threads: u32,
    pub status: String,
}

/// System information.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SystemInfo {
    pub os: String,
    pub arch: String,
    pub cpu_count: u32,
    pub total_memory_mb: u64,
    pub free_memory_mb: u64,
    pub hostname: String,
}

/// Clipboard interface.
#[async_trait]
pub trait ClipboardManager: Send + Sync {
    async fn get_text(&self) -> anyhow::Result<Option<String>>;
    async fn set_text(&self, text: &str) -> anyhow::Result<()>;
}

/// Window information.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WindowInfo {
    pub handle: u64,
    pub title: String,
    pub process_name: String,
    pub is_visible: bool,
    pub is_minimized: bool,
    pub x: i32,
    pub y: i32,
    pub width: u32,
    pub height: u32,
}

/// Window management interface.
#[async_trait]
pub trait WindowManager: Send + Sync {
    async fn list_windows(&self) -> anyhow::Result<Vec<WindowInfo>>;
    async fn focus_window(&self, handle: u64) -> anyhow::Result<()>;
    async fn close_window(&self, handle: u64) -> anyhow::Result<()>;
}
