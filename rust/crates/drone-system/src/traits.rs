//! Platform abstraction traits for cross-platform system operations.

use serde::{Deserialize, Serialize};
use std::time::Duration;

// ── Screen Capture ──────────────────────────────────────────────────────────

#[async_trait::async_trait]
pub trait ScreenCapture: Send + Sync {
    /// Capture the entire primary screen as PNG bytes.
    async fn capture_screen(&self) -> anyhow::Result<Vec<u8>>;
    /// Capture a specific window by handle/ID.
    async fn capture_window(&self, handle: u64) -> anyhow::Result<Vec<u8>>;
    /// Get screen dimensions (width, height).
    async fn screen_size(&self) -> anyhow::Result<(u32, u32)>;
    /// Get pixel color at coordinates. Returns RGB tuple.
    async fn pixel_color(&self, x: i32, y: i32) -> anyhow::Result<(u8, u8, u8)>;
}

// ── Input Simulation ────────────────────────────────────────────────────────

#[async_trait::async_trait]
pub trait InputSimulator: Send + Sync {
    async fn type_text(&self, text: &str) -> anyhow::Result<()>;
    async fn press_key(&self, key: &str) -> anyhow::Result<()>;
    async fn move_mouse(&self, x: i32, y: i32) -> anyhow::Result<()>;
    async fn click(&self, x: i32, y: i32, button: MouseButton) -> anyhow::Result<()>;
    async fn drag(&self, from_x: i32, from_y: i32, to_x: i32, to_y: i32) -> anyhow::Result<()>;
    async fn scroll(&self, delta_x: i32, delta_y: i32) -> anyhow::Result<()>;
}

// ── Process Management ──────────────────────────────────────────────────────

#[async_trait::async_trait]
pub trait ProcessManager: Send + Sync {
    async fn run_command(&self, command: &str, args: &str, working_dir: Option<&str>) -> anyhow::Result<CommandResult>;
    async fn list_processes(&self) -> anyhow::Result<Vec<ProcessInfo>>;
    async fn kill_process(&self, pid: u32) -> anyhow::Result<bool>;
    async fn system_info(&self) -> anyhow::Result<SystemInfo>;
}

// ── Clipboard ───────────────────────────────────────────────────────────────

#[async_trait::async_trait]
pub trait ClipboardManager: Send + Sync {
    async fn get_text(&self) -> anyhow::Result<Option<String>>;
    async fn set_text(&self, text: &str) -> anyhow::Result<()>;
}

// ── Window Management ───────────────────────────────────────────────────────

#[async_trait::async_trait]
pub trait WindowManager: Send + Sync {
    async fn list_windows(&self) -> anyhow::Result<Vec<WindowInfo>>;
    async fn focus_window(&self, handle: u64) -> anyhow::Result<()>;
    async fn close_window(&self, handle: u64) -> anyhow::Result<()>;
}

// ── Data Models ─────────────────────────────────────────────────────────────

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum MouseButton {
    Left,
    Right,
    Middle,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CommandResult {
    pub exit_code: i32,
    pub stdout: String,
    pub stderr: String,
    pub duration: Duration,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ProcessInfo {
    pub pid: u32,
    pub name: String,
    pub status: String,
    pub cpu_usage: f64,
    pub memory: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SystemInfo {
    pub hostname: String,
    pub os: String,
    pub os_version: String,
    pub arch: String,
    pub cpu_count: usize,
    pub total_memory: u64,
    pub used_memory: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WindowInfo {
    pub handle: u64,
    pub title: String,
    pub pid: u32,
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
}
