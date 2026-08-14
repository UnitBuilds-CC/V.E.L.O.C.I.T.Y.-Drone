//! Linux platform implementations using shell commands.

use crate::traits::*;

// ── X11/Wayland Screen Capture ──────────────────────────────────────────────

pub struct X11ScreenCapture;

impl X11ScreenCapture {
    pub fn new() -> Self { Self }

    async fn run_capture(&self, cmd: &str, args: &str) -> Vec<u8> {
        match tokio::process::Command::new(cmd)
            .args(args.split_whitespace())
            .output().await
        {
            Ok(out) if out.status.success() => out.stdout,
            _ => vec![],
        }
    }
}

#[async_trait::async_trait]
impl ScreenCapture for X11ScreenCapture {
    async fn capture_screen(&self) -> anyhow::Result<Vec<u8>> {
        // Try grim (Wayland) first, then import (X11/ImageMagick)
        let result = self.run_capture("grim", "-o -").await;
        if !result.is_empty() { return Ok(result); }
        let result = self.run_capture("import", "-window root png:-").await;
        if !result.is_empty() { return Ok(result); }
        Err(anyhow::anyhow!("No screen capture tool available (install grim or imagemagick)"))
    }

    async fn capture_window(&self, handle: u64) -> anyhow::Result<Vec<u8>> {
        if handle == 0 { return Ok(vec![]); }
        let args = format!("-window {} png:-", handle);
        Ok(self.run_capture("import", &args).await)
    }

    async fn screen_size(&self) -> anyhow::Result<(u32, u32)> {
        let output = tokio::process::Command::new("xrandr")
            .args(["--current"])
            .output().await?;
        let text = String::from_utf8_lossy(&output.stdout);
        for line in text.lines() {
            if line.contains('*') {
                let parts: Vec<&str> = line.trim().split_whitespace().collect();
                if let Some(res) = parts.first() {
                    let dims: Vec<&str> = res.split('x').collect();
                    if dims.len() == 2 {
                        if let (Ok(w), Ok(h)) = (dims[0].parse(), dims[1].parse()) {
                            return Ok((w, h));
                        }
                    }
                }
            }
        }
        Ok((1920, 1080))
    }

    async fn pixel_color(&self, x: i32, y: i32) -> anyhow::Result<(u8, u8, u8)> {
        let args = format!("-window root -crop 1x1+{}+{} txt:-", x, y);
        let output = self.run_capture("import", &args).await;
        let text = String::from_utf8_lossy(&output);
        // Parse hex format: #RRGGBB
        if let Some(idx) = text.find('#') {
            if idx + 7 <= text.len() {
                let hex = &text[idx+1..idx+7];
                if let (Ok(r), Ok(g), Ok(b)) = (
                    u8::from_str_radix(&hex[0..2], 16),
                    u8::from_str_radix(&hex[2..4], 16),
                    u8::from_str_radix(&hex[4..6], 16),
                ) {
                    return Ok((r, g, b));
                }
            }
        }
        Ok((0, 0, 0))
    }
}

// ── X11 Input Simulator (xdotool) ───────────────────────────────────────────

pub struct X11InputSimulator;

impl X11InputSimulator {
    pub fn new() -> Self { Self }

    async fn run_xdotool(&self, args: &str) -> anyhow::Result<()> {
        tokio::process::Command::new("xdotool")
            .args(args.split_whitespace())
            .output().await?;
        Ok(())
    }
}

#[async_trait::async_trait]
impl InputSimulator for X11InputSimulator {
    async fn type_text(&self, text: &str) -> anyhow::Result<()> {
        let escaped = text.replace('\'', "'\\''");
        self.run_xdotool(&format!("type --clearmodifiers '{}'", escaped)).await
    }

    async fn press_key(&self, key: &str) -> anyhow::Result<()> {
        self.run_xdotool(&format!("key {}", map_key_x11(key))).await
    }

    async fn move_mouse(&self, x: i32, y: i32) -> anyhow::Result<()> {
        self.run_xdotool(&format!("mousemove {} {}", x, y)).await
    }

    async fn click(&self, x: i32, y: i32, button: MouseButton) -> anyhow::Result<()> {
        self.move_mouse(x, y).await?;
        let btn = match button {
            MouseButton::Left => "1",
            MouseButton::Right => "3",
            MouseButton::Middle => "2",
        };
        self.run_xdotool(&format!("click {}", btn)).await
    }

    async fn drag(&self, from_x: i32, from_y: i32, to_x: i32, to_y: i32) -> anyhow::Result<()> {
        self.move_mouse(from_x, from_y).await?;
        self.run_xdotool("mousedown 1").await?;
        tokio::time::sleep(std::time::Duration::from_millis(50)).await;
        self.run_xdotool(&format!("mousemove --sync {} {}", to_x, to_y)).await?;
        self.run_xdotool("mouseup 1").await
    }

    async fn scroll(&self, _delta_x: i32, delta_y: i32) -> anyhow::Result<()> {
        let btn = if delta_y > 0 { "4" } else { "5" };
        for _ in 0..delta_y.abs() {
            self.run_xdotool(&format!("click {}", btn)).await?;
        }
        Ok(())
    }
}

// ── Linux Process Manager ───────────────────────────────────────────────────

pub struct LinuxProcessManager;

impl LinuxProcessManager {
    pub fn new() -> Self { Self }
}

#[async_trait::async_trait]
impl ProcessManager for LinuxProcessManager {
    async fn run_command(&self, command: &str, args: &str, working_dir: Option<&str>) -> anyhow::Result<CommandResult> {
        let start = std::time::Instant::now();
        let full_cmd = if args.is_empty() {
            command.to_string()
        } else {
            format!("{} {}", command, args)
        };

        let mut cmd = tokio::process::Command::new("sh");
        cmd.args(["-c", &full_cmd]);
        if let Some(dir) = working_dir {
            cmd.current_dir(dir);
        }

        let output = cmd.output().await?;
        Ok(CommandResult {
            exit_code: output.status.code().unwrap_or(-1),
            stdout: String::from_utf8_lossy(&output.stdout).to_string(),
            stderr: String::from_utf8_lossy(&output.stderr).to_string(),
            duration: start.elapsed(),
        })
    }

    async fn list_processes(&self) -> anyhow::Result<Vec<ProcessInfo>> {
        use sysinfo::System;
        let mut sys = System::new();
        sys.refresh_all();
        let procs = sys.processes().iter().map(|(pid, proc_)| {
            ProcessInfo {
                pid: pid.as_u32(),
                name: proc_.name().to_string_lossy().to_string(),
                status: format!("{:?}", proc_.status()),
                cpu_usage: proc_.cpu_usage() as f64,
                memory: proc_.memory(),
            }
        }).collect();
        Ok(procs)
    }

    async fn kill_process(&self, pid: u32) -> anyhow::Result<bool> {
        let output = tokio::process::Command::new("kill")
            .args(["-9", &pid.to_string()])
            .output().await?;
        Ok(output.status.success())
    }

    async fn system_info(&self) -> anyhow::Result<SystemInfo> {
        use sysinfo::System;
        let sys = System::new_all();
        Ok(SystemInfo {
            hostname: System::host_name().unwrap_or_default(),
            os: System::name().unwrap_or_default(),
            os_version: System::os_version().unwrap_or_default(),
            arch: std::env::consts::ARCH.to_string(),
            cpu_count: System::cpus().len(),
            total_memory: sys.total_memory(),
            used_memory: sys.used_memory(),
        })
    }
}

// ── X11 Window Manager ──────────────────────────────────────────────────────

pub struct X11WindowManager;

impl X11WindowManager {
    pub fn new() -> Self { Self }
}

#[async_trait::async_trait]
impl WindowManager for X11WindowManager {
    async fn list_windows(&self) -> anyhow::Result<Vec<WindowInfo>> {
        let output = tokio::process::Command::new("xdotool")
            .args(["search", "--onlyvisible", "--name", ""])
            .output().await?;
        let text = String::from_utf8_lossy(&output.stdout);
        let mut windows = Vec::new();

        for line in text.lines() {
            let line = line.trim();
            if let Ok(wid) = line.parse::<u64>() {
                let title = get_window_prop("getwindowname", wid).await;
                let (x, y, w, h) = get_window_geometry(wid).await;
                windows.push(WindowInfo {
                    handle: wid,
                    title,
                    pid: 0,
                    x, y, width: w, height: h,
                });
            }
        }
        Ok(windows)
    }

    async fn focus_window(&self, handle: u64) -> anyhow::Result<()> {
        tokio::process::Command::new("xdotool")
            .args(["windowactivate", &handle.to_string()])
            .output().await?;
        Ok(())
    }

    async fn close_window(&self, handle: u64) -> anyhow::Result<()> {
        tokio::process::Command::new("wmctrl")
            .args(["-ic", &handle.to_string()])
            .output().await?;
        Ok(())
    }
}

// ── Helpers ─────────────────────────────────────────────────────────────────

async fn get_window_prop(prop: &str, wid: u64) -> String {
    match tokio::process::Command::new("xdotool")
        .args([prop, &wid.to_string()])
        .output().await
    {
        Ok(out) => String::from_utf8_lossy(&out.stdout).trim().to_string(),
        Err(_) => String::new(),
    }
}

async fn get_window_geometry(wid: u64) -> (i32, i32, i32, i32) {
    let output = match tokio::process::Command::new("xwininfo")
        .args(["-id", &wid.to_string()])
        .output().await
    {
        Ok(out) => String::from_utf8_lossy(&out.stdout).to_string(),
        Err(_) => return (0, 0, 0, 0),
    };

    let mut x = 0i32; let mut y = 0i32; let mut w = 0i32; let mut h = 0i32;
    for line in output.lines() {
        let trimmed = line.trim();
        if let Some(val) = trimmed.strip_prefix("Absolute upper-left X:") {
            x = val.trim().parse().unwrap_or(0);
        } else if let Some(val) = trimmed.strip_prefix("Absolute upper-left Y:") {
            y = val.trim().parse().unwrap_or(0);
        } else if trimmed.starts_with("Width:") {
            w = trimmed.split(':').nth(1).and_then(|v| v.trim().parse().ok()).unwrap_or(0);
        } else if trimmed.starts_with("Height:") {
            h = trimmed.split(':').nth(1).and_then(|v| v.trim().parse().ok()).unwrap_or(0);
        }
    }
    (x, y, w, h)
}

fn map_key_x11(key: &str) -> &str {
    match key.to_lowercase().as_str() {
        "enter" | "return" => "Return",
        "escape" | "esc" => "Escape",
        "tab" => "Tab",
        "space" => "space",
        "backspace" => "BackSpace",
        "delete" | "del" => "Delete",
        "up" => "Up", "down" => "Down", "left" => "Left", "right" => "Right",
        "home" => "Home", "end" => "End",
        "pageup" => "Prior", "pagedown" => "Next",
        "shift" => "Shift_L", "control" | "ctrl" => "Control_L",
        "alt" => "Alt_L", "meta" | "super" | "win" => "Super_L",
        "f1" => "F1", "f2" => "F2", "f3" => "F3", "f4" => "F4",
        "f5" => "F5", "f6" => "F6", "f7" => "F7", "f8" => "F8",
        "f9" => "F9", "f10" => "F10", "f11" => "F11", "f12" => "F12",
        other => other,
    }
}
