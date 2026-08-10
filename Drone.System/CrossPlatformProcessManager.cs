using global::System.Diagnostics;
using global::System.Runtime.InteropServices;
using Drone.Core;

namespace Drone.System;

public class CrossPlatformProcessManager : IProcessManager
{
    private readonly ILogger _logger;
    public CrossPlatformProcessManager(ILogger logger) => _logger = logger;

    public Task<ProcessInfo[]> ListProcessesAsync(CancellationToken ct = default)
    {
        var processes = global::System.Diagnostics.Process.GetProcesses()
            .Select(p => { try { return new ProcessInfo(p.Id, p.ProcessName, SafeGet(() => p.MainWindowTitle, ""), p.WorkingSet64, p.Threads.Count, SafeGet(() => p.Responding ? "Running" : "Not Responding")); } catch { return null; } })
            .Where(p => p != null).ToArray()!;
        return Task.FromResult(processes);
    }

    public async Task<CommandResult> RunCommandAsync(string command, string arguments, string? workingDir = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        string shell, shellArgs;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) { shell = "cmd.exe"; shellArgs = "/c " + command + " " + arguments; }
        else { shell = "/bin/bash"; shellArgs = "-c \"" + command + " " + arguments + "\""; }
        var psi = new ProcessStartInfo { FileName = shell, Arguments = shellArgs, WorkingDirectory = workingDir ?? Environment.CurrentDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var process = global::System.Diagnostics.Process.Start(psi);
        if (process == null) return new CommandResult(-1, "", "Failed to start process", sw.Elapsed);
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        sw.Stop();
        return new CommandResult(process.ExitCode, stdout, stderr, sw.Elapsed);
    }

    public Task<bool> KillProcessAsync(int processId, CancellationToken ct = default)
    {
        try { global::System.Diagnostics.Process.GetProcessById(processId).Kill(); return Task.FromResult(true); }
        catch { return Task.FromResult(false); }
    }

    public Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default)
    {
        var hostname = Environment.MachineName;
        var os = RuntimeInformation.OSDescription;
        var osVersion = Environment.OSVersion.ToString();
        var arch = RuntimeInformation.OSArchitecture.ToString();
        var procs = Environment.ProcessorCount;
        long totalMem = 0, availMem = 0;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && File.Exists("/proc/meminfo"))
        {
            foreach (var line in File.ReadAllLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:")) totalMem = ParseMemInfo(line) / 1024;
                else if (line.StartsWith("MemAvailable:")) availMem = ParseMemInfo(line) / 1024;
            }
        }
        else { totalMem = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024); availMem = totalMem - (global::System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)); }
        var drives = global::System.IO.DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).ToArray();
        return Task.FromResult(new SystemInfo(hostname, os, osVersion, arch, procs, totalMem, availMem, drives.Select(d => (long)(d.TotalSize / (1024 * 1024))).ToArray(), drives.Select(d => (long)(d.AvailableFreeSpace / (1024 * 1024))).ToArray(), drives.Select(d => d.Name).ToArray()));
    }

    private static long ParseMemInfo(string line) { var parts = line.Split(':', 2); if (parts.Length < 2) return 0; var value = new string(parts[1].Trim().TakeWhile(char.IsDigit).ToArray()); return long.TryParse(value, out var kb) ? kb : 0; }
    private static T SafeGet<T>(Func<T> getter, T defaultValue = default!) { try { return getter(); } catch { return defaultValue; } }
}
