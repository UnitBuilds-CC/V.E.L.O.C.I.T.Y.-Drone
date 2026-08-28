namespace Drone.System;

/// <summary>Cross-platform screen capture abstraction.</summary>
public interface IScreenCapture
{
    /// <summary>Capture the entire primary screen as PNG bytes.</summary>
    Task<byte[]> CaptureScreenAsync(CancellationToken ct = default);

    /// <summary>Capture a specific window by handle/ID.</summary>
    Task<byte[]> CaptureWindowAsync(nint handle, CancellationToken ct = default);

    /// <summary>Get screen dimensions (width, height).</summary>
    Task<(int Width, int Height)> GetScreenSizeAsync();

    /// <summary>Get pixel color at coordinates. Returns RGB tuple.</summary>
    Task<(byte R, byte G, byte B)> GetPixelColorAsync(int x, int y);

    /// <summary>Capture raw BGRA pixels for delta processing. Allocates — use overload for zero-alloc.</summary>
    Task<(byte[] Pixels, uint Stride, int Width, int Height)?> CaptureRawBgraAsync(CancellationToken ct = default)
        => Task.FromResult<(byte[], uint, int, int)?>(null);

    /// <summary>Ultra-cheap screen change detection.</summary>
    bool HasScreenChanged() => true;

    /// <summary>Capture raw BGRA into pre-allocated buffer. Zero-alloc. Returns (stride, w, h) or null.</summary>
    Task<(uint Stride, int Width, int Height)?> CaptureRawBgraAsync(byte[] targetBuffer, CancellationToken ct = default)
        => Task.FromResult<(uint, int, int)?>(null);
}

/// <summary>Cross-platform input simulation abstraction.</summary>
public interface IInputSimulator
{
    Task TypeTextAsync(string text, CancellationToken ct = default);
    Task PressKeyAsync(VirtualKey key, CancellationToken ct = default);
    Task KeyDownAsync(VirtualKey key, CancellationToken ct = default);
    Task KeyUpAsync(VirtualKey key, CancellationToken ct = default);
    Task MoveMouseAsync(int x, int y, CancellationToken ct = default);
    Task ClickAsync(int x, int y, MouseButton button = MouseButton.Left, CancellationToken ct = default);
    Task DoubleClickAsync(int x, int y, CancellationToken ct = default);
    Task DragAsync(int fromX, int fromY, int toX, int toY, CancellationToken ct = default);
    Task ScrollAsync(int deltaX, int deltaY, CancellationToken ct = default);
    Task<(int X, int Y)> GetMousePositionAsync();
}

/// <summary>Cross-platform process and command management.</summary>
public interface IProcessManager
{
    Task<ProcessInfo[]> ListProcessesAsync(CancellationToken ct = default);
    Task<CommandResult> RunCommandAsync(string command, string arguments, string? workingDir = null, CancellationToken ct = default);
    Task<bool> KillProcessAsync(int processId, CancellationToken ct = default);
    Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default);
}

/// <summary>Clipboard operations.</summary>
public interface IClipboardManager
{
    Task<string> GetTextAsync(CancellationToken ct = default);
    Task SetTextAsync(string text, CancellationToken ct = default);
}

/// <summary>Window management operations.</summary>
public interface IWindowManager
{
    Task<WindowInfo[]> ListWindowsAsync(CancellationToken ct = default);
    Task FocusWindowAsync(nint handle, CancellationToken ct = default);
    Task CloseWindowAsync(nint handle, CancellationToken ct = default);
    Task<(int X, int Y, int Width, int Height)> GetWindowBoundsAsync(nint handle);
}

// ── Data Models ──────────────────────────────────────────────

public enum MouseButton { Left, Right, Middle }

public enum VirtualKey
{
    // Letters
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    // Numbers
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    // Function keys
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    // Modifiers
    Shift, Control, Alt, Meta,
    // Navigation
    Enter, Escape, Tab, Space, Backspace, Delete,
    Up, Down, Left, Right, Home, End, PageUp, PageDown,
    // Special
    Insert, PrintScreen, Pause, CapsLock, NumLock, ScrollLock
}

public record ProcessInfo(int Id, string Name, string MainWindowTitle, long WorkingSet64, int ThreadCount, string Status);

public record CommandResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Duration);

public record SystemInfo(string Hostname, string OS, string OSVersion, string Architecture, int ProcessorCount, long TotalMemoryMB, long AvailableMemoryMB, long[] DriveTotalMB, long[] DriveFreeMB, string[] DriveNames);

public record WindowInfo(nint Handle, string Title, string ProcessName, int X, int Y, int Width, int Height, bool IsVisible, bool IsMinimized);