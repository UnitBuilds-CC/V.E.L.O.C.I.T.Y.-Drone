//! macOS platform implementations using system commands.

use crate::traits::*;

// ── CoreGraphics Screen Capture (screencapture) ─────────────────────────────

pub struct CoreGraphicsScreenCapture;

impl CoreGraphicsScreenCapture {
    pub fn new() -> Self { Self }
}

#[async_trait::async_trait]
impl ScreenCapture for CoreGraphicsScreenCapture {
    async fn capture_screen(&self) -> anyhow::Result<Vec<u8>> {
        let tmp = std::env::temp_dir().join(format!("drone_cap_{}.png", std::process::id()));
        let tmp_str = tmp.to_string_lossy().to_string();

        let status = tokio::process::Command::new("screencapture")
            .args(["-x", &tmp_str])
            .output().await?;

        if status.status.success() && tmp.exists() {
            let data = tokio::fs::read(&tmp).await?;
            let _ = tokio::fs::remove_file(&tmp).await;
            return Ok(data);
        }
        let _ = tokio::fs::remove_file(&tmp).await;
        Err(anyhow::anyhow!("screencapture failed"))
    }

    async fn capture_window(&self, handle: u64) -> anyhow::Result<Vec<u8>> {
        if handle == 0 { return Ok(vec![]); }
        let tmp = std::env::temp_dir().join(format!("drone_wcap_{}.png", std::process::id()));
        let tmp_str = tmp.to_string_lossy().to_string();

        let status = tokio::process::Command::new("screencapture")
            .args(["-x", &format!("-l{}", handle), &tmp_str])
            .output().await?;

        if status.status.success() && tmp.exists() {
            let data = tokio::fs::read(&tmp).await?;
            let _ = tokio::fs::remove_file(&tmp).await;
            return Ok(data);
        }
        let _ = tokio::fs::remove_file(&tmp).await;
        Ok(vec![])
    }

    async fn screen_size(&self) -> anyhow::Result<(u32, u32)> {
        let output = tokio::process::Command::new("system_profiler")
            .args(["SPDisplaysDataType"])
            .output().await?;
        let text = String::from_utf8_lossy(&output.stdout);

        for line in text.lines() {
            if line.contains("Resolution") || line.contains("Retina") {
                let parts: Vec<&str> = line.split('x').collect();
                if parts.len() == 2 {
                    let w_str: String = parts[0].chars().rev().take_while(|c| c.is_ascii_digit()).collect::<String>().chars().rev().collect();
                    let h_str: String = parts[1].trim().chars().take_while(|c| c.is_ascii_digit()).collect();
                    if let (Ok(w), Ok(h)) = (w_str.parse(), h_str.parse()) {
                        return Ok((w, h));
                    }
                }
            }
        }
        Ok((1920, 1080))
    }

    async fn pixel_color(&self, x: i32, y: i32) -> anyhow::Result<(u8, u8, u8)> {
        let tmp = std::env::temp_dir().join(format!("drone_px_{}.png", std::process::id()));
        let tmp_str = tmp.to_string_lossy().to_string();

        let _ = tokio::process::Command::new("screencapture")
            .args(["-x", &format!("-R{},{},1,1", x, y), &tmp_str])
            .output().await?;

        let _ = tokio::fs::remove_file(&tmp).await;
        Ok((0, 0, 0))
    }
}

// ── CoreGraphics Input Simulator (cliclick) ─────────────────────────────────

pub struct CoreGraphicsInputSimulator;

impl CoreGraphicsInputSimulator {
    pub fn new() -> Self { Self }

    async fn run_cliclick(&self, args: &str) -> anyhow::Result<()> {
        tokio::process::Command::new("cliclick")
            .args(args.split_whitespace())
            .output().await?;
        Ok(())
    }
}

#[async_trait::async_trait]
impl InputSimulator for CoreGraphicsInputSimulator {
    async fn type_text(&self, text: &str) -> anyhow::Result<()> {
        let escaped = text.replace('\'', "'\\''");
        self.run_cliclick(&format!("t:'{}'", escaped)).await
    }

    async fn press_key(&self, key: &str) -> anyhow::Result<()> {
        self.run_cliclick(&format!("kp:{}", map_key_macos(key))).await
    }

    async fn move_mouse(&self, x: i32, y: i32) -> anyhow::Result<()> {
        self.run_cliclick(&format!("m:{},{}", x, y)).await
    }

    async fn click(&self, x: i32, y: i32, button: MouseButton) -> anyhow::Result<()> {
        let cmd = match button {
            MouseButton::Left => "c",
            MouseButton::Right => "rc",
            MouseButton::Middle => "mc",
        };
        self.run_cliclick(&format!("{}:{},{}", cmd, x, y)).await
    }

    async fn drag(&self, from_x: i32, from_y: i32, to_x: i32, to_y: i32) -> anyhow::Result<()> {
        self.run_cliclick(&format!("dd:{},{}", from_x, from_y)).await?;
        tokio::time::sleep(std::time::Duration::from_millis(100)).await;
        self.run_cliclick(&format!("du:{},{}", to_x, to_y)).await
    }

    async fn scroll(&self, _delta_x: i32, delta_y: i32) -> anyhow::Result<()> {
        if delta_y != 0 {
            self.run_cliclick(&format!("scroll:{}", delta_y)).await?;
        }
        Ok(())
    }
}

// ── macOS Process Manager ───────────────────────────────────────────────────

pub struct MacOSProcessManager;

impl MacOSProcessManager {
    pub fn new() -> Self { Self }
}

#[async_trait::async_trait]
impl ProcessManager for MacOSProcessManager {
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

// ── macOS Window Manager (AppleScript) ──────────────────────────────────────

pub struct CoreGraphicsWindowManager;

impl CoreGraphicsWindowManager {
    pub fn new() -> Self { Self }
}

#[async_trait::async_trait]
impl WindowManager for CoreGraphicsWindowManager {
    async fn list_windows(&self) -> anyhow::Result<Vec<WindowInfo>> {
        let script = r#"
            tell application "System Events"
                set windowList to {}
                set appList to every application process whose visible is true
                repeat with proc in appList
                    try
                        set winList to every window of proc
                        repeat with w in winList
                            set wpos to position of w
                            set wsize to size of w
                            set end of windowList to (name of proc) & "|" & (name of w) & "|" & (item 1 of wpos) & "," & (item 2 of wpos) & "|" & (item 1 of wsize) & "," & (item 2 of wsize)
                        end repeat
                    end try
                end repeat
                return windowList as text
            end tell"#;

        let output = tokio::process::Command::new("osascript")
            .arg("-e").arg(script)
            .output().await?;
        let text = String::from_utf8_lossy(&output.stdout);
        let mut windows = Vec::new();

        for line in text.lines() {
            let parts: Vec<&str> = line.split('|').collect();
            if parts.len() >= 4 {
                let proc_name = parts[0].trim().to_string();
                let title = parts[1].trim().to_string();
                let pos = parse_pair(parts[2].trim());
                let size = parse_pair(parts[3].trim());
                windows.push(WindowInfo {
                    handle: 0,
                    title,
                    pid: 0,
                    x: pos.0, y: pos.1,
                    width: size.0, height: size.1,
                });
            }
        }
        Ok(windows)
    }

    async fn focus_window(&self, _handle: u64) -> anyhow::Result<()> {
        let script = r#"
            tell application "System Events"
                set appList to every application process whose visible is true
                if (count of appList) > 0 then
                    set frontmost of item 1 of appList to true
                end if
            end tell"#;
        tokio::process::Command::new("osascript")
            .arg("-e").arg(script)
            .output().await?;
        Ok(())
    }

    async fn close_window(&self, _handle: u64) -> anyhow::Result<()> {
        let script = r#"
            tell application "System Events"
                keystroke "w" using command down
            end tell"#;
        tokio::process::Command::new("osascript")
            .arg("-e").arg(script)
            .output().await?;
        Ok(())
    }
}

// ── Helpers ─────────────────────────────────────────────────────────────────

fn parse_pair(s: &str) -> (i32, i32) {
    let parts: Vec<&str> = s.split(',').collect();
    if parts.len() >= 2 {
        let x = parts[0].trim().parse().unwrap_or(0);
        let y = parts[1].trim().parse().unwrap_or(0);
        return (x, y);
    }
    (0, 0)
}

fn map_key_macos(key: &str) -> &str {
    match key.to_lowercase().as_str() {
        "enter" | "return" => "return",
        "escape" | "esc" => "escape",
        "tab" => "tab",
        "space" => "space",
        "backspace" => "delete",
        "delete" | "del" => "forward-delete",
        "up" => "up-arrow", "down" => "down-arrow",
        "left" => "left-arrow", "right" => "right-arrow",
        "home" => "home", "end" => "end",
        "pageup" => "page-up", "pagedown" => "page-down",
        "shift" => "shift", "control" | "ctrl" => "ctrl",
        "alt" => "alt", "meta" | "super" | "win" => "cmd",
        "f1" => "f1", "f2" => "f2", "f3" => "f3", "f4" => "f4",
        "f5" => "f5", "f6" => "f6", "f7" => "f7", "f8" => "f8",
        "f9" => "f9", "f10" => "f10", "f11" => "f11", "f12" => "f12",
        other => other,
    }
}
