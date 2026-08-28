//! Windows platform implementations using Win32 API.

use crate::traits::*;
use winapi::ctypes::c_void;
use winapi::shared::windef::{HWND, HGDIOBJ};

// ── Win32 Screen Capture (GDI) ──────────────────────────────────────────────

pub struct Win32ScreenCapture;

impl Win32ScreenCapture {
    pub fn new() -> Self { Self }
}

#[async_trait::async_trait]
impl ScreenCapture for Win32ScreenCapture {
    async fn capture_screen(&self) -> anyhow::Result<Vec<u8>> {
        use winapi::um::wingdi::*;
        use winapi::um::winuser::*;

        unsafe {
            let hdc_screen = GetWindowDC(GetDesktopWindow());
            if hdc_screen.is_null() { return Err(anyhow::anyhow!("GetWindowDC failed")); }

            let hdc_mem = CreateCompatibleDC(hdc_screen);
            let w = GetSystemMetrics(SM_CXSCREEN);
            let h = GetSystemMetrics(SM_CYSCREEN);
            let hbm = CreateCompatibleBitmap(hdc_screen, w, h);
            let old = SelectObject(hdc_mem, hbm as HGDIOBJ);

            BitBlt(hdc_mem, 0, 0, w, h, hdc_screen, 0, 0, SRCCOPY);

            let mut bmi: BITMAPINFO = std::mem::zeroed();
            bmi.bmiHeader.biSize = std::mem::size_of::<BITMAPINFOHEADER>() as u32;
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = -h;
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 24;
            bmi.bmiHeader.biCompression = BI_RGB;

            let row_size = ((w * 3 + 3) / 4) * 4;
            let mut pixels = vec![0u8; (row_size * h.abs()) as usize];

            GetDIBits(hdc_mem, hbm, 0, h as u32, pixels.as_mut_ptr() as *mut c_void,
                      &mut bmi, DIB_RGB_COLORS);

            SelectObject(hdc_mem, old);
            DeleteObject(hbm as HGDIOBJ);
            DeleteDC(hdc_mem);
            ReleaseDC(GetDesktopWindow(), hdc_screen);

            // Build BMP file
            let file_size = 14 + 40 + pixels.len();
            let mut bmp = vec![0u8; file_size];
            bmp[0] = b'B'; bmp[1] = b'M';
            let fs = file_size as u32;
            bmp[2..6].copy_from_slice(&fs.to_le_bytes());
            bmp[10] = 54; // data offset
            bmp[14] = 40; // DIB header size
            bmp[18..22].copy_from_slice(&(w as u32).to_le_bytes());
            let neg_h = (-(h as i32)) as u32;
            bmp[22..26].copy_from_slice(&neg_h.to_le_bytes());
            bmp[26] = 1; bmp[28] = 24;
            bmp[54..].copy_from_slice(&pixels);

            Ok(bmp)
        }
    }

    async fn capture_window(&self, handle: u64) -> anyhow::Result<Vec<u8>> {
        use winapi::um::wingdi::*;
        use winapi::um::winuser::*;
        use winapi::shared::windef::RECT;

        if handle == 0 { return Ok(vec![]); }
        let hwnd = handle as HWND;

        unsafe {
            let mut rect = RECT { left: 0, top: 0, right: 0, bottom: 0 };
            if GetWindowRect(hwnd, &mut rect) == 0 {
                return Err(anyhow::anyhow!("GetWindowRect failed"));
            }
            let w = rect.right - rect.left;
            let h = rect.bottom - rect.top;
            if w <= 0 || h <= 0 { return Ok(vec![]); }

            let hdc = GetWindowDC(hwnd);
            let hdc_mem = CreateCompatibleDC(hdc);
            let hbm = CreateCompatibleBitmap(hdc, w, h);
            let old = SelectObject(hdc_mem, hbm as HGDIOBJ);
            BitBlt(hdc_mem, 0, 0, w, h, hdc, 0, 0, SRCCOPY);
            SelectObject(hdc_mem, old);

            let mut bmi: BITMAPINFO = std::mem::zeroed();
            bmi.bmiHeader.biSize = std::mem::size_of::<BITMAPINFOHEADER>() as u32;
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = -h;
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 24;
            bmi.bmiHeader.biCompression = BI_RGB;

            let row_size = ((w * 3 + 3) / 4) * 4;
            let mut pixels = vec![0u8; (row_size * h.abs()) as usize];
            GetDIBits(hdc_mem, hbm, 0, h as u32, pixels.as_mut_ptr() as *mut c_void,
                      &mut bmi, DIB_RGB_COLORS);

            DeleteObject(hbm as HGDIOBJ);
            DeleteDC(hdc_mem);
            ReleaseDC(hwnd, hdc);

            let file_size = 14 + 40 + pixels.len();
            let mut bmp = vec![0u8; file_size];
            bmp[0] = b'B'; bmp[1] = b'M';
            bmp[2..6].copy_from_slice(&(file_size as u32).to_le_bytes());
            bmp[10] = 54;
            bmp[14] = 40;
            bmp[18..22].copy_from_slice(&(w as u32).to_le_bytes());
            bmp[22..26].copy_from_slice(&((-(h as i32)) as u32).to_le_bytes());
            bmp[26] = 1; bmp[28] = 24;
            bmp[54..].copy_from_slice(&pixels);
            Ok(bmp)
        }
    }

    async fn screen_size(&self) -> anyhow::Result<(u32, u32)> {
        unsafe {
            Ok((
                winapi::um::winuser::GetSystemMetrics(winapi::um::winuser::SM_CXSCREEN) as u32,
                winapi::um::winuser::GetSystemMetrics(winapi::um::winuser::SM_CYSCREEN) as u32,
            ))
        }
    }

    async fn pixel_color(&self, x: i32, y: i32) -> anyhow::Result<(u8, u8, u8)> {
        use winapi::um::winuser::*;
        use winapi::um::wingdi::*;
        unsafe {
            let hdc = GetWindowDC(GetDesktopWindow());
            let color = GetPixel(hdc, x, y);
            ReleaseDC(GetDesktopWindow(), hdc);
            Ok(((color & 0xFF) as u8, ((color >> 8) & 0xFF) as u8, ((color >> 16) & 0xFF) as u8))
        }
    }
}

// ── Win32 Input Simulator ───────────────────────────────────────────────────

pub struct Win32InputSimulator;

impl Win32InputSimulator {
    pub fn new() -> Self { Self }
}

#[async_trait::async_trait]
impl InputSimulator for Win32InputSimulator {
    async fn type_text(&self, text: &str) -> anyhow::Result<()> {
        use winapi::um::winuser::*;
        for ch in text.chars() {
            unsafe {
                let vk = VkKeyScanW(ch as u16);
                let key = (vk & 0xFF) as u8;
                let shift = (vk & 0x100) != 0;
                if shift { keybd_event(VK_SHIFT as u8, 0, 0, 0); }
                keybd_event(key, 0, 0, 0);
                keybd_event(key, 0, KEYEVENTF_KEYUP, 0);
                if shift { keybd_event(VK_SHIFT as u8, 0, KEYEVENTF_KEYUP, 0); }
            }
        }
        Ok(())
    }

    async fn press_key(&self, key: &str) -> anyhow::Result<()> {
        use winapi::um::winuser::*;
        let vk = map_key(key);
        unsafe {
            keybd_event(vk, 0, 0, 0);
            keybd_event(vk, 0, KEYEVENTF_KEYUP, 0);
        }
        Ok(())
    }

    async fn move_mouse(&self, x: i32, y: i32) -> anyhow::Result<()> {
        unsafe { winapi::um::winuser::SetCursorPos(x, y); }
        Ok(())
    }

    async fn click(&self, x: i32, y: i32, button: MouseButton) -> anyhow::Result<()> {
        use winapi::um::winuser::*;
        self.move_mouse(x, y).await?;
        let flag = match button {
            MouseButton::Left => MOUSEEVENTF_LEFTDOWN,
            MouseButton::Right => MOUSEEVENTF_RIGHTDOWN,
            MouseButton::Middle => MOUSEEVENTF_MIDDLEDOWN,
        };
        unsafe {
            mouse_event(flag, 0, 0, 0, 0);
            mouse_event(flag | 0x0002, 0, 0, 0, 0); // UP = DOWN + 1 bit
        }
        Ok(())
    }

    async fn drag(&self, from_x: i32, from_y: i32, to_x: i32, to_y: i32) -> anyhow::Result<()> {
        use winapi::um::winuser::*;
        self.move_mouse(from_x, from_y).await?;
        unsafe { mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0); }
        tokio::time::sleep(std::time::Duration::from_millis(50)).await;
        self.move_mouse(to_x, to_y).await?;
        unsafe { mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0); }
        Ok(())
    }

    async fn scroll(&self, _delta_x: i32, delta_y: i32) -> anyhow::Result<()> {
        use winapi::um::winuser::*;
        unsafe { mouse_event(MOUSEEVENTF_WHEEL, 0, 0, delta_y as u32 * 120, 0); }
        Ok(())
    }
}

// ── Win32 Process Manager ───────────────────────────────────────────────────

pub struct Win32ProcessManager;

impl Win32ProcessManager {
    pub fn new() -> Self { Self }
}

#[async_trait::async_trait]
impl ProcessManager for Win32ProcessManager {
    async fn run_command(&self, command: &str, args: &str, working_dir: Option<&str>) -> anyhow::Result<CommandResult> {
        let start = std::time::Instant::now();
        let full_cmd = if args.is_empty() {
            command.to_string()
        } else {
            format!("{} {}", command, args)
        };

        let mut cmd = tokio::process::Command::new("cmd");
        cmd.args(["/C", &full_cmd]);
        if let Some(dir) = working_dir {
            cmd.current_dir(dir);
        }

        let output = cmd.output().await?;
        let duration = start.elapsed();

        Ok(CommandResult {
            exit_code: output.status.code().unwrap_or(-1),
            stdout: String::from_utf8_lossy(&output.stdout).to_string(),
            stderr: String::from_utf8_lossy(&output.stderr).to_string(),
            duration,
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
        let output = tokio::process::Command::new("taskkill")
            .args(["/PID", &pid.to_string(), "/F"])
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
            cpu_count: sys.cpus().len(),
            total_memory: sys.total_memory(),
            used_memory: sys.used_memory(),
        })
    }
}

// ── Win32 Clipboard Manager ─────────────────────────────────────────────────

pub struct Win32ClipboardManager;

impl Win32ClipboardManager {
    pub fn new() -> Self { Self }
}

#[async_trait::async_trait]
impl ClipboardManager for Win32ClipboardManager {
    async fn get_text(&self) -> anyhow::Result<Option<String>> {
        use winapi::um::winuser::*;

        unsafe {
            if OpenClipboard(std::ptr::null_mut()) == 0 { return Ok(None); }
            let handle = GetClipboardData(CF_UNICODETEXT);
            if handle.is_null() { CloseClipboard(); return Ok(None); }

            let ptr = handle as *const u16;
            let mut len = 0usize;
            while *ptr.add(len) != 0 { len += 1; }
            let slice = std::slice::from_raw_parts(ptr, len);
            let text = String::from_utf16_lossy(slice);
            CloseClipboard();
            Ok(Some(text))
        }
    }

    async fn set_text(&self, text: &str) -> anyhow::Result<()> {
        use winapi::um::winuser::*;
        use winapi::um::winbase::*;

        let wide: Vec<u16> = text.encode_utf16().chain(std::iter::once(0)).collect();
        let bytes = wide.len() * 2;

        unsafe {
            if OpenClipboard(std::ptr::null_mut()) == 0 { return Err(anyhow::anyhow!("OpenClipboard failed")); }
            EmptyClipboard();
            let hmem = GlobalAlloc(GMEM_MOVEABLE, bytes);
            if hmem.is_null() { CloseClipboard(); return Err(anyhow::anyhow!("GlobalAlloc failed")); }
            let ptr = GlobalLock(hmem) as *mut u16;
            std::ptr::copy_nonoverlapping(wide.as_ptr(), ptr, wide.len());
            GlobalUnlock(hmem);
            SetClipboardData(CF_UNICODETEXT, hmem as *mut c_void);
            CloseClipboard();
        }
        Ok(())
    }
}

// ── Win32 Window Manager ────────────────────────────────────────────────────

pub struct Win32WindowManager;

impl Win32WindowManager {
    pub fn new() -> Self { Self }
}

#[async_trait::async_trait]
impl WindowManager for Win32WindowManager {
    async fn list_windows(&self) -> anyhow::Result<Vec<WindowInfo>> {
        use winapi::um::winuser::*;
        use winapi::shared::windef::RECT;
        use winapi::shared::minwindef::LPARAM;

        let mut windows = Vec::new();

        unsafe extern "system" fn enum_proc(hwnd: HWND, lparam: LPARAM) -> i32 {
            let windows = &mut *(lparam as *mut Vec<WindowInfo>);
            if IsWindowVisible(hwnd) == 0 { return 1; }

            let len = GetWindowTextLengthW(hwnd);
            if len == 0 { return 1; }

            let mut buf = vec![0u16; (len + 1) as usize];
            let copied = GetWindowTextW(hwnd, buf.as_mut_ptr(), buf.len() as i32);
            let title = String::from_utf16_lossy(&buf[..copied as usize]);

            let mut rect = RECT { left: 0, top: 0, right: 0, bottom: 0 };
            GetWindowRect(hwnd, &mut rect);

            let mut pid: u32 = 0;
            GetWindowThreadProcessId(hwnd, &mut pid);

            windows.push(WindowInfo {
                handle: hwnd as u64,
                title,
                pid,
                x: rect.left,
                y: rect.top,
                width: rect.right - rect.left,
                height: rect.bottom - rect.top,
            });
            1
        }

        unsafe {
            EnumWindows(Some(enum_proc), &mut windows as *mut Vec<WindowInfo> as LPARAM);
        }
        Ok(windows)
    }

    async fn focus_window(&self, handle: u64) -> anyhow::Result<()> {
        use winapi::um::winuser::*;
        let hwnd = handle as HWND;
        unsafe {
            if IsIconic(hwnd) != 0 {
                ShowWindow(hwnd, SW_RESTORE);
            }
            SetForegroundWindow(hwnd);
        }
        Ok(())
    }

    async fn close_window(&self, handle: u64) -> anyhow::Result<()> {
        use winapi::um::winuser::*;
        unsafe { PostMessageW(handle as HWND, WM_CLOSE, 0, 0); }
        Ok(())
    }
}

// ── Key mapping helper ──────────────────────────────────────────────────────

fn map_key(key: &str) -> u8 {
    use winapi::um::winuser::*;
    match key.to_lowercase().as_str() {
        "enter" | "return" => VK_RETURN as u8,
        "escape" | "esc" => VK_ESCAPE as u8,
        "tab" => VK_TAB as u8,
        "space" => VK_SPACE as u8,
        "backspace" => VK_BACK as u8,
        "delete" | "del" => VK_DELETE as u8,
        "up" => VK_UP as u8,
        "down" => VK_DOWN as u8,
        "left" => VK_LEFT as u8,
        "right" => VK_RIGHT as u8,
        "home" => VK_HOME as u8,
        "end" => VK_END as u8,
        "pageup" => VK_PRIOR as u8,
        "pagedown" => VK_NEXT as u8,
        "shift" => VK_SHIFT as u8,
        "control" | "ctrl" => VK_CONTROL as u8,
        "alt" => VK_MENU as u8,
        "meta" | "win" | "super" => VK_LWIN as u8,
        "f1" => VK_F1 as u8, "f2" => VK_F2 as u8, "f3" => VK_F3 as u8,
        "f4" => VK_F4 as u8, "f5" => VK_F5 as u8, "f6" => VK_F6 as u8,
        "f7" => VK_F7 as u8, "f8" => VK_F8 as u8, "f9" => VK_F9 as u8,
        "f10" => VK_F10 as u8, "f11" => VK_F11 as u8, "f12" => VK_F12 as u8,
        s if s.len() == 1 => {
            let ch = s.chars().next().unwrap();
            if ch.is_ascii_alphabetic() { ch.to_ascii_uppercase() as u8 }
            else if ch.is_ascii_digit() { ch as u8 }
            else { 0 }
        }
        _ => 0,
    }
}
