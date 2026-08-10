using Drone.Core;

namespace Drone.System.MacOS;

public class CoreGraphicsScreenCapture : IScreenCapture
{
    private readonly ILogger _logger;
    public CoreGraphicsScreenCapture(ILogger logger) => _logger = logger;

    public async Task<byte[]> CaptureScreenAsync(CancellationToken ct = default)
    {
        var tmpFile = Path.GetTempFileName() + ".png";
        var psi = new global::System.Diagnostics.ProcessStartInfo("screencapture", "-x " + tmpFile)
        { UseShellExecute = false, CreateNoWindow = true };
        using var proc = global::System.Diagnostics.Process.Start(psi);
        if (proc != null) await proc.WaitForExitAsync(ct);
        if (File.Exists(tmpFile))
        {
            var data = await File.ReadAllBytesAsync(tmpFile, ct);
            File.Delete(tmpFile);
            return data;
        }
        return Array.Empty<byte>();
    }

    public Task<byte[]> CaptureWindowAsync(nint handle, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<byte>());
    public Task<(int Width, int Height)> GetScreenSizeAsync()
        => Task.FromResult((1920, 1080));
    public Task<(byte R, byte G, byte B)> GetPixelColorAsync(int x, int y)
        => Task.FromResult(((byte)0, (byte)0, (byte)0));
}

public class CoreGraphicsInputSimulator : IInputSimulator
{
    private readonly ILogger _logger;
    public CoreGraphicsInputSimulator(ILogger logger) => _logger = logger;

    public async Task TypeTextAsync(string text, CancellationToken ct = default)
    {
        // macOS input via cliclick or osascript (stub for now)
        await Task.CompletedTask;
    }

    public Task PressKeyAsync(VirtualKey key, CancellationToken ct = default) => Task.CompletedTask;
    public Task KeyDownAsync(VirtualKey key, CancellationToken ct = default) => Task.CompletedTask;
    public Task KeyUpAsync(VirtualKey key, CancellationToken ct = default) => Task.CompletedTask;
    public Task MoveMouseAsync(int x, int y, CancellationToken ct = default) => Task.CompletedTask;
    public Task ClickAsync(int x, int y, MouseButton button = MouseButton.Left, CancellationToken ct = default) => Task.CompletedTask;
    public Task DoubleClickAsync(int x, int y, CancellationToken ct = default) => Task.CompletedTask;
    public Task DragAsync(int fromX, int fromY, int toX, int toY, CancellationToken ct = default) => Task.CompletedTask;
    public Task ScrollAsync(int deltaX, int deltaY, CancellationToken ct = default) => Task.CompletedTask;
    public Task<(int X, int Y)> GetMousePositionAsync() => Task.FromResult((0, 0));
}

public class CoreGraphicsWindowManager : IWindowManager
{
    public Task<WindowInfo[]> ListWindowsAsync(CancellationToken ct = default)
        => Task.FromResult(Array.Empty<WindowInfo>());
    public Task FocusWindowAsync(nint handle, CancellationToken ct = default) => Task.CompletedTask;
    public Task CloseWindowAsync(nint handle, CancellationToken ct = default) => Task.CompletedTask;
    public Task<(int X, int Y, int Width, int Height)> GetWindowBoundsAsync(nint handle)
        => Task.FromResult((0, 0, 0, 0));
}
