using global::System.Diagnostics;
using global::System.Runtime.InteropServices;
using Drone.Core;

namespace Drone.System;

public class CrossPlatformProcessManager : IProcessManager
{
    private readonly ILogger _logger;
    public CrossPlatformProcessManager(ILogger logger) => _logger = logger;

    // ── Win32 Native Process Enumeration (10-50x faster than System.Diagnostics.Process) ──

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32First(nint snap, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(nint snap, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    // ── Win32 Native Memory Info ──
    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // ── Win32 Native Disk Info ──
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(string path, out ulong freeBytesAvail, out ulong totalBytes, out ulong totalFreeBytes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLogicalDriveStrings(uint bufLen, char[] buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetDriveType(string drive);

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint DRIVE_FIXED = 3;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    public Task<ProcessInfo[]> ListProcessesAsync(CancellationToken ct = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Task.FromResult(ListProcessesNative());
        
        return Task.FromResult(ListProcessesManaged());
    }

    /// <summary>
    /// Native Win32 process enumeration using CreateToolhelp32Snapshot.
    /// Single kernel call instead of opening 300+ process handles.
    /// </summary>
    private static ProcessInfo[] ListProcessesNative()
    {
        var result = new List<ProcessInfo>(256);
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == nint.Zero) return Array.Empty<ProcessInfo>();

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snap, ref entry)) return Array.Empty<ProcessInfo>();

            do
            {
                var name = entry.szExeFile;
                // Strip .exe extension for consistency
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    name = name[..^4];

                result.Add(new ProcessInfo(
                    (int)entry.th32ProcessID,
                    name,
                    "",  // Window title filled by WindowManager when needed
                    0,   // WorkingSet not available from snapshot
                    (int)entry.cntThreads,
                    "Running"));
            } while (Process32Next(snap, ref entry));
        }
        finally
        {
            CloseHandle(snap);
        }

        return result.ToArray();
    }

    /// <summary>Fallback for Linux/macOS using managed Process API.</summary>
    private static ProcessInfo[] ListProcessesManaged()
    {
        var processes = global::System.Diagnostics.Process.GetProcesses();
        try
        {
            var result = new List<ProcessInfo>();
            foreach (var p in processes)
            {
                try
                {
                    result.Add(new ProcessInfo(
                        p.Id, p.ProcessName,
                        SafeGet(() => p.MainWindowTitle) ?? "",
                        p.WorkingSet64, p.Threads.Count,
                        SafeGet(() => p.Responding ? "Running" : "Not Responding") ?? "Unknown"));
                }
                catch { /* process may have exited between enumeration and property access */ }
                finally { try { p.Dispose(); } catch { /* process may already be disposed */ } }
            }
            return result.ToArray();
        }
        catch
        {
            foreach (var p in processes) { try { p.Dispose(); } catch { /* process may already be disposed */ } }
            throw;
        }
    }

    public async Task<CommandResult> RunCommandAsync(string command, string arguments, string? workingDir = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        string shell, shellArgs;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) { shell = "cmd.exe"; shellArgs = "/c " + command + " " + arguments; }
        else { shell = "/bin/bash"; shellArgs = "-c \"" + command + " " + arguments + "\""; }
        var psi = new ProcessStartInfo
        {
            FileName = shell, Arguments = shellArgs,
            WorkingDirectory = workingDir ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        using var process = global::System.Diagnostics.Process.Start(psi);
        if (process == null) return new CommandResult(-1, "", "Failed to start process", sw.Elapsed);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        await process.WaitForExitAsync(ct);
        sw.Stop();
        return new CommandResult(process.ExitCode, stdout, stderr, sw.Elapsed);
    }

    public Task<bool> KillProcessAsync(int processId, CancellationToken ct = default)
    {
        try
        {
            using var p = global::System.Diagnostics.Process.GetProcessById(processId);
            p.Kill();
            return Task.FromResult(true);
        }
        catch { /* process not found or access denied — return false */ return Task.FromResult(false); }
    }

    public Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default)
    {
        var hostname = Environment.MachineName;
        var os = RuntimeInformation.OSDescription;
        var osVersion = Environment.OSVersion.ToString();
        var arch = RuntimeInformation.OSArchitecture.ToString();
        var procs = Environment.ProcessorCount;
        long totalMem = 0, availMem = 0;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Native Win32 memory query - single P/Invoke call
            var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref memStatus))
            {
                totalMem = (long)(memStatus.ullTotalPhys / (1024 * 1024));
                availMem = (long)(memStatus.ullAvailPhys / (1024 * 1024));
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && File.Exists("/proc/meminfo"))
        {
            foreach (var line in File.ReadAllLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:")) totalMem = ParseMemInfo(line) / 1024;
                else if (line.StartsWith("MemAvailable:")) availMem = ParseMemInfo(line) / 1024;
            }
        }
        else
        {
            totalMem = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
            availMem = totalMem / 2; // rough estimate for macOS
        }

        // Native disk enumeration - avoid DriveInfo which uses WMI on Windows
        var driveNames = new List<string>();
        var driveTotals = new List<long>();
        var driveFree = new List<long>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var buf = new char[256];
            var len = GetLogicalDriveStrings((uint)buf.Length, buf);
            var drivesStr = new string(buf, 0, (int)len);
            foreach (var drive in drivesStr.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (GetDriveType(drive) != DRIVE_FIXED) continue;
                if (GetDiskFreeSpaceEx(drive, out var freeAvail, out var total, out var totalFree))
                {
                    driveNames.Add(drive);
                    driveTotals.Add((long)(total / (1024 * 1024)));
                    driveFree.Add((long)(freeAvail / (1024 * 1024)));
                }
            }
        }
        else
        {
            var drives = global::System.IO.DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).ToArray();
            foreach (var d in drives)
            {
                driveNames.Add(d.Name);
                driveTotals.Add((long)(d.TotalSize / (1024 * 1024)));
                driveFree.Add((long)(d.AvailableFreeSpace / (1024 * 1024)));
            }
        }

        return Task.FromResult(new SystemInfo(
            hostname, os, osVersion, arch, procs, totalMem, availMem,
            driveTotals.ToArray(), driveFree.ToArray(), driveNames.ToArray()));
    }

    private static long ParseMemInfo(string line)
    {
        var parts = line.Split(':', 2);
        if (parts.Length < 2) return 0;
        var value = new string(parts[1].Trim().TakeWhile(char.IsDigit).ToArray());
        return long.TryParse(value, out var kb) ? kb : 0;
    }

    private static T? SafeGet<T>(Func<T?> getter) where T : class
    {
        try { return getter(); } catch { /* property access failed — process state unavailable */ return null; }
    }
}