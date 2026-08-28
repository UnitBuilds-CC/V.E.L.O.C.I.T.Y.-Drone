---
kind: platform_abstraction
name: Cross-Platform System Abstractions
category: architecture
scope:
    - 'Drone.System/**'
source_files:
    - Drone.System/Interfaces.cs
    - Drone.System/PlatformFactory.cs
    - Drone.System/Windows/WindowsScreenCapture.cs
    - Drone.System/Linux/LinuxScreenCapture.cs
    - Drone.System/MacOS/MacOsScreenCapture.cs
    - Drone.System/CrossPlatformProcessManager.cs
    - Drone.System/CrossPlatformClipboardManager.cs
---

Drone.System provides cross-platform abstractions for system-level operations, with platform-specific implementations for Windows, Linux, and macOS. The `PlatformFactory` creates appropriate implementations at runtime based on OS detection.

**Core Interfaces:**

1. **IScreenCapture** — Screen image capture:
   - `Task<byte[]> CaptureScreenAsync()` — Returns PNG/JPEG bytes
   - Windows: Win32 GDI+ (BitBlt)
   - Linux: `scrot` or `import` command
   - macOS: `screencapture` command

2. **IInputSimulator** — Keyboard/mouse simulation:
   - `Task TypeTextAsync(string text)` — Type string
   - `Task PressKeyAsync(string key)` — Press key (e.g., "enter", "ctrl+c")
   - `Task ClickAsync(int x, int y, string button)` — Mouse click
   - Windows: Win32 `SendInput` API
   - Linux: `xdotool` command
   - macOS: `cliclick` command

3. **IWindowManager** — Window enumeration/manipulation:
   - `Task<List<WindowInfo>> EnumerateWindowsAsync()` — List windows
   - `Task FocusWindowAsync(string title)` — Focus window by title
   - `Task MoveWindowAsync(string title, int x, int y)` — Move window
   - Windows: Win32 `EnumWindows`, `SetForegroundWindow`
   - Linux: `wmctrl` command
   - macOS: `osascript` (AppleScript)

4. **IProcessManager** — Process management:
   - `Task<ProcessResult> RunCommandAsync(string command)` — Execute shell command
   - `Task<List<ProcessInfo>> ListProcessesAsync()` — Enumerate processes
   - `Task KillProcessAsync(int pid)` — Terminate process
   - Cross-platform: `System.Diagnostics.Process`

5. **IClipboardManager** — Clipboard operations:
   - `Task SetClipboardTextAsync(string text)` — Copy to clipboard
   - `Task<string> GetClipboardTextAsync()` — Paste from clipboard
   - Windows: Win32 clipboard API
   - Linux: `xclip` or `xsel` command
   - macOS: `pbcopy`/`pbpaste` command

**PlatformFactory:**

```csharp
public static class PlatformFactory
{
    public static IScreenCapture CreateScreenCapture(ILogger logger)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WindowsScreenCapture(logger);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new LinuxScreenCapture(logger);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new MacOsScreenCapture(logger);
        else
            throw new PlatformNotSupportedException();
    }
    
    // Similar methods for other interfaces...
}
```

**Cross-Platform Implementations:**

- **CrossPlatformProcessManager** — Unified process management:
  - Windows: `cmd.exe /c` for shell commands
  - Linux/macOS: `/bin/bash -c` for shell commands
  - Timeout handling, output capture

- **CrossPlatformClipboardManager** — Unified clipboard:
  - Detects platform and delegates to appropriate implementation
  - Fallback to `xclip` on Linux if `xsel` unavailable

**Platform Detection:**
- Uses `RuntimeInformation.IsOSPlatform()` for runtime detection
- `OSPlatform.Windows`, `OSPlatform.Linux`, `OSPlatform.OSX`
- Throws `PlatformNotSupportedException` for unsupported platforms

**Graceful Degradation:**
- Agent catches `PlatformNotSupportedException` during initialization
- Logs warning and continues without unavailable features
- Headless mode skips screen/input/window entirely

**Dependencies:**
- Windows: No external dependencies (Win32 API)
- Linux: Requires `xdotool`, `xclip`, `wmctrl`, `scrot` (optional)
- macOS: Requires `cliclick` (install via Homebrew)

**Testing:**
- Mock implementations for unit testing
- Platform-specific E2E tests in CI/CD matrix
