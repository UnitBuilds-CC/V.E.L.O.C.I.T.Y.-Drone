using global::System.Diagnostics;
using global::System.Runtime.InteropServices;
using global::System.Text;
using Drone.Core;

namespace Drone.System.Windows;

public class Win32ScreenCapture : IScreenCapture
{
    private readonly ILogger _logger;
    public Win32ScreenCapture(ILogger logger) => _logger = logger;

    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll")] private static extern nint GetWindowDC(nint hWnd);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint hdc, int x, int y, int w, int h, nint src, int sx, int sy, uint op);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint hDC);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool GetPixel(nint hdc, int x, int y, out uint color);

    private const uint SRCCOPY = 0x00CC0020;

    public Task<byte[]> CaptureScreenAsync(CancellationToken ct = default)
    {
        var hdcScreen = GetWindowDC(GetDesktopWindow());
        var hdcMem = CreateCompatibleDC(hdcScreen);
        var width = GetSystemMetrics(0); // SM_CXSCREEN
        var height = GetSystemMetrics(1); // SM_CYSCREEN
        var hBitmap = CreateCompatibleBitmap(hdcScreen, width, height);
        var hOld = SelectObject(hdcMem, hBitmap);
        BitBlt(hdcMem, 0, 0, width, height, hdcScreen, 0, 0, SRCCOPY);
        SelectObject(hdcMem, hOld);
        DeleteDC(hdcMem);
        ReleaseDC(GetDesktopWindow(), hdcScreen);
        // Return screen info as bytes (full PNG encoding would use SkiaSharp)
        var info = Encoding.UTF8.GetBytes("Screen:" + width + "x" + height);
        DeleteObject(hBitmap);
        return Task.FromResult(info);
    }

    public Task<byte[]> CaptureWindowAsync(nint handle, CancellationToken ct = default)
        => Task.FromResult(Encoding.UTF8.GetBytes("Window capture requires SkiaSharp"));

    public Task<(int Width, int Height)> GetScreenSizeAsync()
        => Task.FromResult((GetSystemMetrics(0), GetSystemMetrics(1)));

    public Task<(byte R, byte G, byte B)> GetPixelColorAsync(int x, int y)
    {
        var hdc = GetWindowDC(GetDesktopWindow());
        GetPixel(hdc, x, y, out var color);
        ReleaseDC(GetDesktopWindow(), hdc);
        var r = (byte)(color & 0xFF);
        var g = (byte)((color >> 8) & 0xFF);
        var b = (byte)((color >> 16) & 0xFF);
        return Task.FromResult((r, g, b));
    }
}

public class Win32InputSimulator : IInputSimulator
{
    private readonly ILogger _logger;
    public Win32InputSimulator(ILogger logger) => _logger = logger;

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, nuint extra);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern ushort VkKeyScan(char ch);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    public Task TypeTextAsync(string text, CancellationToken ct = default)
    {
        foreach (var ch in text)
        {
            var vk = VkKeyScan(ch);
            var key = (byte)(vk & 0xFF);
            var shift = (vk & 0x100) != 0;
            if (shift) keybd_event(0x10, 0, 0, nuint.Zero); // SHIFT down
            keybd_event(key, 0, 0, nuint.Zero);
            keybd_event(key, 0, 2, nuint.Zero); // KEYEVENTF_KEYUP
            if (shift) keybd_event(0x10, 0, 2, nuint.Zero); // SHIFT up
        }
        return Task.CompletedTask;
    }

    public Task PressKeyAsync(VirtualKey key, CancellationToken ct = default)
    {
        var vk = MapKey(key);
        keybd_event((byte)vk, 0, 0, nuint.Zero);
        keybd_event((byte)vk, 0, 2, nuint.Zero);
        return Task.CompletedTask;
    }

    public Task KeyDownAsync(VirtualKey key, CancellationToken ct = default) { keybd_event((byte)MapKey(key), 0, 0, nuint.Zero); return Task.CompletedTask; }
    public Task KeyUpAsync(VirtualKey key, CancellationToken ct = default) { keybd_event((byte)MapKey(key), 0, 2, nuint.Zero); return Task.CompletedTask; }

    public Task MoveMouseAsync(int x, int y, CancellationToken ct = default) { SetCursorPos(x, y); return Task.CompletedTask; }

    public async Task ClickAsync(int x, int y, MouseButton button = MouseButton.Left, CancellationToken ct = default)
    {
        await MoveMouseAsync(x, y, ct);
        var flag = button == MouseButton.Left ? 0x0002u : button == MouseButton.Right ? 0x0008u : 0x0020u;
        mouse_event(flag, 0, 0, 0, nuint.Zero);
        mouse_event(flag + 1, 0, 0, 0, nuint.Zero); // button up
    }

    public async Task DoubleClickAsync(int x, int y, CancellationToken ct = default)
    {
        await ClickAsync(x, y, MouseButton.Left, ct);
        await ClickAsync(x, y, MouseButton.Left, ct);
    }

    public async Task DragAsync(int fromX, int fromY, int toX, int toY, CancellationToken ct = default)
    {
        await MoveMouseAsync(fromX, fromY, ct);
        mouse_event(0x0002, 0, 0, 0, nuint.Zero); // LEFTDOWN
        await Task.Delay(50, ct);
        await MoveMouseAsync(toX, toY, ct);
        mouse_event(0x0004, 0, 0, 0, nuint.Zero); // LEFTUP
    }

    public Task ScrollAsync(int deltaX, int deltaY, CancellationToken ct = default)
    {
        mouse_event(0x0800, 0, 0, (uint)deltaY, nuint.Zero); // MOUSEEVENTF_WHEEL
        return Task.CompletedTask;
    }

    public Task<(int X, int Y)> GetMousePositionAsync()
    {
        GetCursorPos(out var pt);
        return Task.FromResult((pt.X, pt.Y));
    }

    private static int MapKey(VirtualKey key) => key switch
    {
        VirtualKey.Enter => 0x0D, VirtualKey.Escape => 0x1B, VirtualKey.Tab => 0x09,
        VirtualKey.Space => 0x20, VirtualKey.Backspace => 0x08, VirtualKey.Delete => 0x2E,
        VirtualKey.Up => 0x26, VirtualKey.Down => 0x28, VirtualKey.Left => 0x25, VirtualKey.Right => 0x27,
        VirtualKey.Shift => 0x10, VirtualKey.Control => 0x11, VirtualKey.Alt => 0x12,
        VirtualKey.A => 0x41, VirtualKey.B => 0x42, VirtualKey.C => 0x43, VirtualKey.D => 0x44,
        VirtualKey.E => 0x45, VirtualKey.F => 0x46, VirtualKey.G => 0x47, VirtualKey.H => 0x48,
        VirtualKey.I => 0x49, VirtualKey.J => 0x4A, VirtualKey.K => 0x4B, VirtualKey.L => 0x4C,
        VirtualKey.M => 0x4D, VirtualKey.N => 0x4E, VirtualKey.O => 0x4F, VirtualKey.P => 0x50,
        VirtualKey.Q => 0x51, VirtualKey.R => 0x52, VirtualKey.S => 0x53, VirtualKey.T => 0x54,
        VirtualKey.U => 0x55, VirtualKey.V => 0x56, VirtualKey.W => 0x57, VirtualKey.X => 0x58,
        VirtualKey.Y => 0x59, VirtualKey.Z => 0x5A,
        _ => 0
    };
}

public class Win32ClipboardManager : IClipboardManager
{
    private readonly ILogger _logger;
    public Win32ClipboardManager(ILogger logger) => _logger = logger;

    [DllImport("user32.dll")] private static extern bool OpenClipboard(nint hWnd);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint hMem);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nint size);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint hMem);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint hMem);
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    public Task<string> GetTextAsync(CancellationToken ct = default)
    {
        if (!OpenClipboard(nint.Zero)) return Task.FromResult("");
        try
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == nint.Zero) return Task.FromResult("");
            var ptr = GlobalLock(handle);
            if (ptr == nint.Zero) return Task.FromResult("");
            try { return Task.FromResult(Marshal.PtrToStringUni(ptr) ?? ""); }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    public Task SetTextAsync(string text, CancellationToken ct = default)
    {
        if (!OpenClipboard(nint.Zero)) return Task.CompletedTask;
        try
        {
            EmptyClipboard();
            var bytes = Encoding.Unicode.GetBytes(text + '\0');
            var hMem = GlobalAlloc(GMEM_MOVEABLE, bytes.Length);
            var ptr = GlobalLock(hMem);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            GlobalUnlock(hMem);
            SetClipboardData(CF_UNICODETEXT, hMem);
        }
        finally { CloseClipboard(); }
        return Task.CompletedTask;
    }
}

public class Win32WindowManager : IWindowManager
{
    private readonly ILogger _logger;
    private readonly List<WindowInfo> _windows = new();

    public Win32WindowManager(ILogger logger) => _logger = logger;

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, nint lParam);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(nint hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowText(nint hWnd, StringBuilder sb, int maxCount);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    private const int SW_RESTORE = 9;
    private const uint WM_CLOSE = 0x0010;
    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    public Task<WindowInfo[]> ListWindowsAsync(CancellationToken ct = default)
    {
        _windows.Clear();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            var len = GetWindowTextLength(hWnd);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            GetWindowRect(hWnd, out var rect);
            GetWindowThreadProcessId(hWnd, out var pid);
            var procName = "";
            try { procName = global::System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
            _windows.Add(new WindowInfo(hWnd, sb.ToString(), procName, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, true, IsIconic(hWnd)));
            return true;
        }, nint.Zero);
        return Task.FromResult(_windows.ToArray());
    }

    public Task FocusWindowAsync(nint handle, CancellationToken ct = default)
    {
        if (IsIconic(handle)) ShowWindow(handle, SW_RESTORE);
        SetForegroundWindow(handle);
        return Task.CompletedTask;
    }

    public Task CloseWindowAsync(nint handle, CancellationToken ct = default)
    {
        PostMessage(handle, WM_CLOSE, nint.Zero, nint.Zero);
        return Task.CompletedTask;
    }

    public Task<(int X, int Y, int Width, int Height)> GetWindowBoundsAsync(nint handle)
    {
        GetWindowRect(handle, out var rect);
        return Task.FromResult((rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
    }
}
