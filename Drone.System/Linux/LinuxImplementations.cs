using Drone.Core;

namespace Drone.System.Linux;

/// <summary>Linux screen capture via grim (Wayland) or import (X11/ImageMagick).</summary>
public class X11ScreenCapture : IScreenCapture
{
    private readonly ILogger _logger;
    public X11ScreenCapture(ILogger logger) => _logger = logger;

    public async Task<byte[]> CaptureScreenAsync(CancellationToken ct = default)
    {
        var result = await RunCaptureAsync("grim", "-o -", ct);
        if (result.Length > 0) return result;
        return await RunCaptureAsync("import", "-window root png:-", ct);
    }

    public async Task<byte[]> CaptureWindowAsync(nint handle, CancellationToken ct = default)
    {
        if (handle == nint.Zero) return Array.Empty<byte>();
        // Use import -window <xid> to capture a specific X11 window by its window ID
        var result = await RunCaptureAsync("import", $"-window {(long)handle} png:-", ct);
        return result;
    }

    public async Task<(int Width, int Height)> GetScreenSizeAsync()
    {
        try
        {
            var psi = new global::System.Diagnostics.ProcessStartInfo("xrandr", "--current")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var proc = global::System.Diagnostics.Process.Start(psi);
            if (proc == null) return (1920, 1080);
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            // Parse "1920x1080" from xrandr output
            foreach (var line in output.Split('\n'))
            {
                if (line.Contains('*')) // Current resolution has asterisk
                {
                    var parts = line.Trim().Split(' ')[0].Split('x');
                    if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                        return (w, h);
                }
            }
        }
        catch { /* resolution detection failed — returning default */ }
        return (1920, 1080);
    }

    public async Task<(byte R, byte G, byte B)> GetPixelColorAsync(int x, int y)
    {
        try
        {
            // Use xwd + convert or import to get pixel at coordinates
            // Simplest: import -window root -crop 1x1+x+y txt:-
            var psi = new global::System.Diagnostics.ProcessStartInfo("import", $"-window root -crop 1x1+{x}+{y} txt:-")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var proc = global::System.Diagnostics.Process.Start(psi);
            if (proc == null) return (0, 0, 0);
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            // ImageMagick txt: format contains "0,(R,G,B) = hex" or similar
            return ParseRgbFromOutput(output);
        }
        catch { /* pixel capture failed — returning black */ }
        return (0, 0, 0);
    }

    private static (byte R, byte G, byte B) ParseRgbFromOutput(string output)
    {
        // Look for "0,(  R,  G,  B)" pattern from ImageMagick txt: format
        var parenStart = output.IndexOf('(');
        var parenEnd = output.IndexOf(')', parenStart >= 0 ? parenStart : 0);
        if (parenStart >= 0 && parenEnd > parenStart)
        {
            var inner = output[(parenStart + 1)..parenEnd];
            var parts = inner.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 3 &&
                int.TryParse(parts[0].Trim(), out var r) &&
                int.TryParse(parts[1].Trim(), out var g) &&
                int.TryParse(parts[2].Trim(), out var b))
                return ((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255));
        }
        // Also try hex format: "#RRGGBB"
        var hexIdx = output.IndexOf('#');
        if (hexIdx >= 0 && hexIdx + 7 <= output.Length)
        {
            var hex = output[(hexIdx + 1)..(hexIdx + 7)];
            if (byte.TryParse(hex[0..2], global::System.Globalization.NumberStyles.HexNumber, null, out var r2) &&
                byte.TryParse(hex[2..4], global::System.Globalization.NumberStyles.HexNumber, null, out var g2) &&
                byte.TryParse(hex[4..6], global::System.Globalization.NumberStyles.HexNumber, null, out var b2))
                return (r2, g2, b2);
        }
        return (0, 0, 0);
    }

    private async Task<byte[]> RunCaptureAsync(string cmd, string args, CancellationToken ct)
    {
        try
        {
            var psi = new global::System.Diagnostics.ProcessStartInfo(cmd, args)
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            using var proc = global::System.Diagnostics.Process.Start(psi);
            if (proc == null) return Array.Empty<byte>();
            using var ms = new MemoryStream();
            await proc.StandardOutput.BaseStream.CopyToAsync(ms, ct);
            await proc.WaitForExitAsync(ct);
            return ms.ToArray();
        }
        catch { return Array.Empty<byte>(); }
    }
}

/// <summary>Linux input simulation via xdotool.</summary>
public class X11InputSimulator : IInputSimulator
{
    private readonly ILogger _logger;
    public X11InputSimulator(ILogger logger) => _logger = logger;

    public async Task TypeTextAsync(string text, CancellationToken ct = default)
    {
        var escaped = text.Replace("'", "'\\''");
        await RunAsync("xdotool", "type --clearmodifiers '" + escaped + "'", ct);
    }

    public async Task PressKeyAsync(VirtualKey key, CancellationToken ct = default)
        => await RunAsync("xdotool", "key " + MapKey(key), ct);

    public async Task KeyDownAsync(VirtualKey key, CancellationToken ct = default)
        => await RunAsync("xdotool", "keydown " + MapKey(key), ct);

    public async Task KeyUpAsync(VirtualKey key, CancellationToken ct = default)
        => await RunAsync("xdotool", "keyup " + MapKey(key), ct);

    public async Task MoveMouseAsync(int x, int y, CancellationToken ct = default)
        => await RunAsync("xdotool", "mousemove " + x + " " + y, ct);

    public async Task ClickAsync(int x, int y, MouseButton button = MouseButton.Left, CancellationToken ct = default)
    {
        await MoveMouseAsync(x, y, ct);
        var btn = button == MouseButton.Left ? "1" : button == MouseButton.Right ? "3" : "2";
        await RunAsync("xdotool", "click " + btn, ct);
    }

    public async Task DoubleClickAsync(int x, int y, CancellationToken ct = default)
    {
        await MoveMouseAsync(x, y, ct);
        await RunAsync("xdotool", "click --repeat 2 1", ct);
    }

    public async Task DragAsync(int fromX, int fromY, int toX, int toY, CancellationToken ct = default)
    {
        await MoveMouseAsync(fromX, fromY, ct);
        await RunAsync("xdotool", "mousedown 1", ct);
        await Task.Delay(50, ct);
        await RunAsync("xdotool", "mousemove --sync " + toX + " " + toY, ct);
        await RunAsync("xdotool", "mouseup 1", ct);
    }

    public async Task ScrollAsync(int deltaX, int deltaY, CancellationToken ct = default)
    {
        if (deltaY != 0)
        {
            var btn = deltaY > 0 ? "4" : "5";
            for (var i = 0; i < Math.Abs(deltaY); i++)
                await RunAsync("xdotool", "click " + btn, ct);
        }
        if (deltaX != 0)
        {
            var btn = deltaX > 0 ? "7" : "6";
            for (var i = 0; i < Math.Abs(deltaX); i++)
                await RunAsync("xdotool", "click " + btn, ct);
        }
    }

    public async Task<(int X, int Y)> GetMousePositionAsync()
    {
        try
        {
            var psi = new global::System.Diagnostics.ProcessStartInfo("xdotool", "getmouselocation")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            using var proc = global::System.Diagnostics.Process.Start(psi);
            if (proc == null) return (0, 0);
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            var parts = output.Split(' ');
            var x = int.Parse(parts[0].Split(':')[1]);
            var y = int.Parse(parts[1].Split(':')[1]);
            return (x, y);
        }
        catch { return (0, 0); }
    }

    private async Task RunAsync(string cmd, string args, CancellationToken ct)
    {
        try
        {
            var psi = new global::System.Diagnostics.ProcessStartInfo(cmd, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            using var proc = global::System.Diagnostics.Process.Start(psi);
            if (proc != null) await proc.WaitForExitAsync(ct);
        }
        catch (Exception ex) { _logger.LogWarning("xdotool error: {Error}", ex.Message); }
    }

    private static string MapKey(VirtualKey key) => key switch
    {
        VirtualKey.Enter => "Return",
        VirtualKey.Escape => "Escape",
        VirtualKey.Tab => "Tab",
        VirtualKey.Space => "space",
        VirtualKey.Backspace => "BackSpace",
        VirtualKey.Delete => "Delete",
        VirtualKey.Up => "Up", VirtualKey.Down => "Down",
        VirtualKey.Left => "Left", VirtualKey.Right => "Right",
        VirtualKey.Home => "Home", VirtualKey.End => "End",
        VirtualKey.PageUp => "Prior", VirtualKey.PageDown => "Next",
        VirtualKey.Shift => "Shift_L", VirtualKey.Control => "Control_L",
        VirtualKey.Alt => "Alt_L", VirtualKey.Meta => "Super_L",
        VirtualKey.F1 => "F1", VirtualKey.F2 => "F2", VirtualKey.F3 => "F3",
        VirtualKey.F4 => "F4", VirtualKey.F5 => "F5", VirtualKey.F6 => "F6",
        VirtualKey.F7 => "F7", VirtualKey.F8 => "F8", VirtualKey.F9 => "F9",
        VirtualKey.F10 => "F10", VirtualKey.F11 => "F11", VirtualKey.F12 => "F12",
        VirtualKey.Insert => "Insert", VirtualKey.PrintScreen => "Print",
        VirtualKey.Pause => "Pause", VirtualKey.CapsLock => "Caps_Lock",
        VirtualKey.NumLock => "Num_Lock", VirtualKey.ScrollLock => "Scroll_Lock",
        _ => key.ToString().ToLower()
    };
}

/// <summary>Linux window management via xdotool and xwininfo.</summary>
public class X11WindowManager : IWindowManager
{
    private readonly ILogger _logger;
    public X11WindowManager(ILogger logger) => _logger = logger;

    public async Task<WindowInfo[]> ListWindowsAsync(CancellationToken ct = default)
    {
        try
        {
            // Get list of visible window IDs
            var psi = new global::System.Diagnostics.ProcessStartInfo("xdotool", "search --onlyvisible --name ''")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            using var proc = global::System.Diagnostics.Process.Start(psi);
            if (proc == null) return [];
            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            var windows = new List<WindowInfo>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!long.TryParse(line.Trim(), out var windowId)) continue;
                var title = await GetWindowTitleAsync(windowId, ct);
                var (x, y, w, h) = await GetWindowGeometryAsync(windowId, ct);
                var procName = await GetWindowProcessNameAsync(windowId, ct);
                windows.Add(new WindowInfo((nint)windowId, title, procName, x, y, w, h, true, false));
            }
            return windows.ToArray();
        }
        catch { return []; }
    }

    public async Task FocusWindowAsync(nint handle, CancellationToken ct = default)
    {
        try
        {
            var psi = new global::System.Diagnostics.ProcessStartInfo("xdotool", "windowactivate " + handle)
            { UseShellExecute = false, CreateNoWindow = true };
            using var proc = global::System.Diagnostics.Process.Start(psi);
            if (proc != null) await proc.WaitForExitAsync(ct);
        }
        catch (Exception ex) { _logger.LogWarning("Focus window failed: {Error}", ex.Message); }
    }

    public async Task CloseWindowAsync(nint handle, CancellationToken ct = default)
    {
        try
        {
            var psi = new global::System.Diagnostics.ProcessStartInfo("wmctrl", "-ic " + handle)
            { UseShellExecute = false, CreateNoWindow = true };
            using var proc = global::System.Diagnostics.Process.Start(psi);
            if (proc != null) await proc.WaitForExitAsync(ct);
        }
        catch (Exception ex) { _logger.LogWarning("Close window failed: {Error}", ex.Message); }
    }

    public async Task<(int X, int Y, int Width, int Height)> GetWindowBoundsAsync(nint handle)
    {
        return await GetWindowGeometryAsync((long)handle, CancellationToken.None);
    }

    private static async Task<string> GetWindowTitleAsync(long windowId, CancellationToken ct)
    {
        try
        {
            var psi = new global::System.Diagnostics.ProcessStartInfo("xdotool", "getwindowname " + windowId)
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var proc = global::System.Diagnostics.Process.Start(psi);
            if (proc == null) return "";
            var result = (await proc.StandardOutput.ReadToEndAsync(ct)).Trim();
            await proc.WaitForExitAsync(ct);
            return result;
        }
        catch { return ""; }
    }

    private static async Task<string> GetWindowProcessNameAsync(long windowId, CancellationToken ct)
    {
        try
        {
            var psi = new global::System.Diagnostics.ProcessStartInfo("xdotool", "getwindowpid " + windowId)
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var proc = global::System.Diagnostics.Process.Start(psi);
            if (proc == null) return "";
            var pidStr = (await proc.StandardOutput.ReadToEndAsync(ct)).Trim();
            await proc.WaitForExitAsync(ct);
            if (int.TryParse(pidStr, out var pid))
            {
                using var p = global::System.Diagnostics.Process.GetProcessById(pid);
                return p.ProcessName;
            }
        }
        catch { /* window info query failed */ }
        return "";
    }

    private static async Task<(int X, int Y, int W, int H)> GetWindowGeometryAsync(long windowId, CancellationToken ct)
    {
        try
        {
            var psi = new global::System.Diagnostics.ProcessStartInfo("xwininfo", "-id " + windowId)
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var proc = global::System.Diagnostics.Process.Start(psi);
            if (proc == null) return (0, 0, 0, 0);
            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            int x = 0, y = 0, w = 0, h = 0;
            foreach (var line in output.Split('\n'))
            {
                // Use TrimStart() + StartsWith to avoid false matches
                // (e.g. "Root Window ID:" contains "Width:" as a substring)
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("Absolute upper-left X:"))
                    x = int.Parse(trimmed.Split(':')[1].Trim());
                else if (trimmed.StartsWith("Absolute upper-left Y:"))
                    y = int.Parse(trimmed.Split(':')[1].Trim());
                else if (trimmed.StartsWith("Width:"))
                    w = int.Parse(trimmed.Split(':')[1].Trim());
                else if (trimmed.StartsWith("Height:"))
                    h = int.Parse(trimmed.Split(':')[1].Trim());
            }
            return (x, y, w, h);
        }
        catch { return (0, 0, 0, 0); }
    }
}
