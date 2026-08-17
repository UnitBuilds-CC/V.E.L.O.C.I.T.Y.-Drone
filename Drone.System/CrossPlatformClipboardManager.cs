using global::System.Diagnostics;
using Drone.Core;

namespace Drone.System;

public class CrossPlatformClipboardManager : IClipboardManager
{
    private readonly ILogger _logger;
    public CrossPlatformClipboardManager(ILogger logger) => _logger = logger;

    public async Task<string> GetTextAsync(CancellationToken ct = default)
    {
        if (OperatingSystem.IsLinux())
        {
            try { var r = await RunAsync("xclip", "-selection clipboard -o", ct); return r; } catch { return ""; }
        }
        if (OperatingSystem.IsMacOS())
        {
            try { var r = await RunAsync("pbpaste", "", ct); return r; } catch { return ""; }
        }
        return "";
    }

    public async Task SetTextAsync(string text, CancellationToken ct = default)
    {
        if (OperatingSystem.IsLinux())
        {
            try { var psi = new ProcessStartInfo("xclip", "-selection clipboard") { RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true }; using var p = Process.Start(psi); if (p != null) { await p.StandardInput.WriteAsync(text.AsMemory(), ct); p.StandardInput.Close(); await p.WaitForExitAsync(ct); } } catch { /* xclip may not be installed or clipboard unavailable */ }
        }
        else if (OperatingSystem.IsMacOS())
        {
            try { var psi = new ProcessStartInfo("pbcopy") { RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true }; using var p = Process.Start(psi); if (p != null) { await p.StandardInput.WriteAsync(text.AsMemory(), ct); p.StandardInput.Close(); await p.WaitForExitAsync(ct); } } catch { /* pbcopy may not be available */ }
        }
    }

    private async Task<string> RunAsync(string cmd, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(cmd, args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi);
        if (p == null) return "";
        var result = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return result.Trim();
    }
}
