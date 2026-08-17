using Drone.Core;

namespace Drone.System.MacOS;

/// <summary>macOS screen capture via screencapture (built-in, produces real PNG).</summary>
public class CoreGraphicsScreenCapture : IScreenCapture
{
    private readonly ILogger _logger;
    public CoreGraphicsScreenCapture(ILogger logger) => _logger = logger;

    public async Task<byte[]> CaptureScreenAsync(CancellationToken ct = default)
    {
        var tmpFile = Path.GetTempFileName() + ".png";
        try
        {
            var psi = new global::System.Diagnostics.ProcessStartInfo("screencapture", "-x " + tmpFile)
            { UseShellExecute = false, CreateNoWindow = true };
            using var proc = global::System.Diagnostics.Process.Start(psi);
            if (proc != null) await proc.WaitForExitAsync(ct);
            if (File.Exists(tmpFile))
            {
                var data = await File.ReadAllBytesAsync(tmpFile, ct);
                return data;
            }
        }
        finally { if (File.Exists(tmpFile)) File.Delete(tmpFile); }
        return Array.Empty<byte>();
    }

    public async Task<byte[]> CaptureWindowAsync(nint handle, CancellationToken ct = default)
    {
        // screencapture -l<windowId> captures a specific window by CGWindowID
        var tmpFile = Path.GetTempFileName() + ".png";
        try
        {
            var psi = new global::System.Diagnostics.ProcessStartInfo("screencapture", $"-x -l{handle} {tmpFile}")
            { UseShellExecute = false, CreateNoWindow = true };
            using var proc = global::System.Diagnostics.Process.Start(psi);
            if (proc != null) await proc.WaitForExitAsync(ct);
            if (File.Exists(tmpFile))
                return await File.ReadAllBytesAsync(tmpFile, ct);
        }
        finally { if (File.Exists(tmpFile)) File.Delete(tmpFile); }
        return Array.Empty<byte>();
    }

    public async Task<(int Width, int Height)> GetScreenSizeAsync()
    {
        try
        {
            // Use system_profiler to get display resolution
            var psi = new global::System.Diagnostics.ProcessStartInfo("system_profiler", "SPDisplaysDataType")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var proc = global::System.Diagnostics.Process.Start(psi);
            if (proc == null) return (1920, 1080);
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            // Parse "Resolution: 1920 x 1080" or "Retina: 2560 x 1600"
            foreach (var line in output.Split('\n'))
            {
                if (line.Contains("Resolution") || line.Contains("Retina"))
                {
                    var parts = line.Split('x');
                    if (parts.Length == 2)
                    {
                        var wStr = new string(parts[0].Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
                        var hStr = new string(parts[1].Trim().TakeWhile(char.IsDigit).ToArray());
                        if (int.TryParse(wStr, out var w) && int.TryParse(hStr, out var h))
                            return (w, h);
                    }
                }
            }
        }
        catch { /* resolution detection failed — returning default */ }
        return (1920, 1080);
    }

    public async Task<(byte R, byte G, byte B)> GetPixelColorAsync(int x, int y)
    {
        // Capture a 1x1 region at the specified coordinates, then parse pixel from PNG
        var tmpFile = Path.GetTempFileName() + ".png";
        try
        {
            // screencapture -R<x,y,w,h> captures a specific region
            var psi = new global::System.Diagnostics.ProcessStartInfo("screencapture", $"-x -R{x},{y},1,1 {tmpFile}")
            { UseShellExecute = false, CreateNoWindow = true };
            using var proc = global::System.Diagnostics.Process.Start(psi);
            if (proc != null) await proc.WaitForExitAsync();
            if (File.Exists(tmpFile) && new FileInfo(tmpFile).Length > 0)
            {
                // Use sips to get pixel value — or parse PNG minimally
                // Simpler: use osascript with NSColor to sample the pixel
                var script = $"do shell script \"screencapture -x -R{x},{y},1,1 {tmpFile} && sips -g pixelColor {tmpFile}\"";
                var psi2 = new global::System.Diagnostics.ProcessStartInfo("osascript", "-e " + EscapeShellArg(script))
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var proc2 = global::System.Diagnostics.Process.Start(psi2);
                if (proc2 != null)
                {
                    var output = await proc2.StandardOutput.ReadToEndAsync();
                    await proc2.WaitForExitAsync();
                    // Parse "pixelColor: (255,128,0)" or similar format
                    return ParseRgbFromOutput(output);
                }
            }
        }
        catch { /* pixel capture failed — returning black */ }
        finally { if (File.Exists(tmpFile)) File.Delete(tmpFile); }
        return (0, 0, 0);
    }

    private static string EscapeShellArg(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static (byte R, byte G, byte B) ParseRgbFromOutput(string output)
    {
        // Look for pattern like "(R,G,B)" or "R, G, B" in the output
        var start = output.IndexOf('(');
        var end = output.IndexOf(')');
        if (start >= 0 && end > start)
        {
            var inner = output[(start + 1)..end];
            var parts = inner.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 3 &&
                byte.TryParse(parts[0].Trim(), out var r) &&
                byte.TryParse(parts[1].Trim(), out var g) &&
                byte.TryParse(parts[2].Trim(), out var b))
                return (r, g, b);
        }
        return (0, 0, 0);
    }
}

/// <summary>macOS input simulation via cliclick (requires brew install cliclick).</summary>
public class CoreGraphicsInputSimulator : IInputSimulator
{
    private readonly ILogger _logger;
    public CoreGraphicsInputSimulator(ILogger logger) => _logger = logger;

    public async Task TypeTextAsync(string text, CancellationToken ct = default)
    {
        var escaped = text.Replace("'", "'\\''");
        await RunAsync("cliclick", "t:" + escaped, ct);
    }

    public async Task PressKeyAsync(VirtualKey key, CancellationToken ct = default)
    {
        await RunAsync("cliclick", "kp:" + MapKey(key), ct);
    }

    public async Task KeyDownAsync(VirtualKey key, CancellationToken ct = default)
    {
        await RunAsync("cliclick", "kd:" + MapKey(key), ct);
    }

    public async Task KeyUpAsync(VirtualKey key, CancellationToken ct = default)
    {
        await RunAsync("cliclick", "ku:" + MapKey(key), ct);
    }

    public async Task MoveMouseAsync(int x, int y, CancellationToken ct = default)
    {
        await RunAsync("cliclick", $"m:{x},{y}", ct);
    }

    public async Task ClickAsync(int x, int y, MouseButton button = MouseButton.Left, CancellationToken ct = default)
    {
        var cmd = button == MouseButton.Right ? "rc" : button == MouseButton.Middle ? "mc" : "c";
        await RunAsync("cliclick", $"{cmd}:{x},{y}", ct);
    }

    public async Task DoubleClickAsync(int x, int y, CancellationToken ct = default)
    {
        await RunAsync("cliclick", $"dc:{x},{y}", ct);
    }

    public async Task DragAsync(int fromX, int fromY, int toX, int toY, CancellationToken ct = default)
    {
        await RunAsync("cliclick", $"dd:{fromX},{fromY}", ct);
        await Task.Delay(100, ct);
        await RunAsync("cliclick", $"du:{toX},{toY}", ct);
    }

    public async Task ScrollAsync(int deltaX, int deltaY, CancellationToken ct = default)
    {
        // cliclick scroll command: scroll:N (positive=up, negative=down)
        if (deltaY != 0) await RunAsync("cliclick", $"scroll:{deltaY}", ct);
    }

    public async Task<(int X, int Y)> GetMousePositionAsync()
    {
        try
        {
            var psi = new global::System.Diagnostics.ProcessStartInfo("cliclick", "p")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var proc = global::System.Diagnostics.Process.Start(psi);
            if (proc == null) return (0, 0);
            var output = (await proc.StandardOutput.ReadToEndAsync()).Trim();
            await proc.WaitForExitAsync();
            // Output format: "x,y"
            var parts = output.Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out var x) && int.TryParse(parts[1].Trim(), out var y))
                return (x, y);
        }
        catch { /* cursor position detection failed */ }
        return (0, 0);
    }

    private async Task RunAsync(string cmd, string args, CancellationToken ct)
    {
        try
        {
            var psi = new global::System.Diagnostics.ProcessStartInfo(cmd, args)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var proc = global::System.Diagnostics.Process.Start(psi);
            if (proc != null) await proc.WaitForExitAsync(ct);
        }
        catch (Exception ex) { _logger.LogWarning("cliclick error: {Error}. Install with: brew install cliclick", ex.Message); }
    }

    private static string MapKey(VirtualKey key) => key switch
    {
        VirtualKey.Enter => "return",
        VirtualKey.Escape => "escape",
        VirtualKey.Tab => "tab",
        VirtualKey.Space => "space",
        VirtualKey.Backspace => "delete",
        VirtualKey.Delete => "forward-delete",
        VirtualKey.Up => "up-arrow",
        VirtualKey.Down => "down-arrow",
        VirtualKey.Left => "left-arrow",
        VirtualKey.Right => "right-arrow",
        VirtualKey.Home => "home",
        VirtualKey.End => "end",
        VirtualKey.PageUp => "page-up",
        VirtualKey.PageDown => "page-down",
        VirtualKey.Shift => "shift",
        VirtualKey.Control => "ctrl",
        VirtualKey.Alt => "alt",
        VirtualKey.Meta => "cmd",
        VirtualKey.F1 => "f1", VirtualKey.F2 => "f2", VirtualKey.F3 => "f3",
        VirtualKey.F4 => "f4", VirtualKey.F5 => "f5", VirtualKey.F6 => "f6",
        VirtualKey.F7 => "f7", VirtualKey.F8 => "f8", VirtualKey.F9 => "f9",
        VirtualKey.F10 => "f10", VirtualKey.F11 => "f11", VirtualKey.F12 => "f12",
        VirtualKey.Insert => "help",
        VirtualKey.CapsLock => "caps-lock",
        _ => key.ToString().ToLower()
    };
}

/// <summary>macOS window management via osascript (AppleScript, built-in).</summary>
public class CoreGraphicsWindowManager : IWindowManager
{
    private readonly ILogger _logger;
    public CoreGraphicsWindowManager(ILogger logger) => _logger = logger;

    public async Task<WindowInfo[]> ListWindowsAsync(CancellationToken ct = default)
    {
        try
        {
            var script = @"
                tell application ""System Events""
                    set windowList to {}
                    set appList to every application process whose visible is true
                    repeat with proc in appList
                        try
                            set winList to every window of proc
                            repeat with w in winList
                                set wpos to position of w
                                set wsize to size of w
                                set end of windowList to (name of proc) & ""|"" & (name of w) & ""|"" & (item 1 of wpos) & "","" & (item 2 of wpos) & ""|"" & (item 1 of wsize) & "","" & (item 2 of wsize)
                            end repeat
                        end try
                    end repeat
                    return windowList as text
                end tell";
            var psi = new global::System.Diagnostics.ProcessStartInfo("osascript", "-e " + EscapeShellArg(script))
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var proc = global::System.Diagnostics.Process.Start(psi);
            if (proc == null) return [];
            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            var windows = new List<WindowInfo>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('|');
                if (parts.Length >= 4)
                {
                    var procName = parts[0].Trim();
                    var title = parts[1].Trim();
                    var pos = ParsePair(parts[2].Trim());
                    var size = ParsePair(parts[3].Trim());
                    windows.Add(new WindowInfo(nint.Zero, title, procName, pos.X, pos.Y, size.X, size.Y, true, false));
                }
                else if (parts.Length >= 2)
                {
                    windows.Add(new WindowInfo(nint.Zero, parts[1].Trim(), parts[0].Trim(), 0, 0, 0, 0, true, false));
                }
            }
            return windows.ToArray();
        }
        catch { return []; }
    }

    public async Task FocusWindowAsync(nint handle, CancellationToken ct = default)
    {
        try
        {
            // Use AppleScript to bring the frontmost visible process to front
            // Since handle is nint.Zero from AppleScript listing, we focus by matching
            var script = @"
                tell application ""System Events""
                    set appList to every application process whose visible is true
                    if (count of appList) > 0 then
                        set frontmost of item 1 of appList to true
                    end if
                end tell";
            var psi = new global::System.Diagnostics.ProcessStartInfo("osascript", "-e " + EscapeShellArg(script))
            { UseShellExecute = false, CreateNoWindow = true };
            using var proc = global::System.Diagnostics.Process.Start(psi);
            if (proc != null) await proc.WaitForExitAsync(ct);
        }
        catch (Exception ex) { _logger.LogWarning("macOS focus window failed: {Error}", ex.Message); }
    }

    public async Task CloseWindowAsync(nint handle, CancellationToken ct = default)
    {
        try
        {
            // Send Cmd+W to the frontmost window via AppleScript
            var script = @"
                tell application ""System Events""
                    keystroke ""w"" using command down
                end tell";
            var psi = new global::System.Diagnostics.ProcessStartInfo("osascript", "-e " + EscapeShellArg(script))
            { UseShellExecute = false, CreateNoWindow = true };
            using var proc = global::System.Diagnostics.Process.Start(psi);
            if (proc != null) await proc.WaitForExitAsync(ct);
        }
        catch (Exception ex) { _logger.LogWarning("macOS close window failed: {Error}", ex.Message); }
    }

    public async Task<(int X, int Y, int Width, int Height)> GetWindowBoundsAsync(nint handle)
    {
        try
        {
            // Get bounds of the frontmost window via AppleScript
            var script = @"
                tell application ""System Events""
                    set appList to every application process whose visible is true
                    if (count of appList) > 0 then
                        set proc to item 1 of appList
                        set winList to every window of proc
                        if (count of winList) > 0 then
                            set w to item 1 of winList
                            set wpos to position of w
                            set wsize to size of w
                            return (item 1 of wpos) & "","" & (item 2 of wpos) & "","" & (item 1 of wsize) & "","" & (item 2 of wsize)
                        end if
                    end if
                end tell";
            var psi = new global::System.Diagnostics.ProcessStartInfo("osascript", "-e " + EscapeShellArg(script))
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var proc = global::System.Diagnostics.Process.Start(psi);
            if (proc == null) return (0, 0, 0, 0);
            var output = (await proc.StandardOutput.ReadToEndAsync()).Trim();
            await proc.WaitForExitAsync();
            var parts = output.Split(',');
            if (parts.Length >= 4 &&
                int.TryParse(parts[0].Trim(), out var x) &&
                int.TryParse(parts[1].Trim(), out var y) &&
                int.TryParse(parts[2].Trim(), out var w) &&
                int.TryParse(parts[3].Trim(), out var h))
                return (x, y, w, h);
        }
        catch { /* window geometry detection failed */ }
        return (0, 0, 0, 0);
    }

    private static (int X, int Y) ParsePair(string s)
    {
        var parts = s.Split(',');
        if (parts.Length >= 2 && int.TryParse(parts[0].Trim(), out var x) && int.TryParse(parts[1].Trim(), out var y))
            return (x, y);
        return (0, 0);
    }


    private static string EscapeShellArg(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
