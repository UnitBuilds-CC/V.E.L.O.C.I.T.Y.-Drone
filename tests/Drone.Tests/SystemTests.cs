using Xunit;
using Drone.System;

namespace Drone.Tests;

public class SystemTests
{
    private class TestLogger : Drone.Core.ILogger
    {
        public void LogInformation(string message, params object[] args) { }
        public void LogWarning(string message, params object[] args) { }
        public void LogError(string message, params object[] args) { }
        public void LogDebug(string message, params object[] args) { }
    }

    [Fact]
    public async Task RunCommandAsync_Echo_ReturnsOutput()
    {
        var mgr = new CrossPlatformProcessManager(new TestLogger());
        var result = await mgr.RunCommandAsync("echo", "hello");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.StandardOutput);
    }

    [Fact]
    public async Task RunCommandAsync_EmptyCommand_ReturnsError()
    {
        var mgr = new CrossPlatformProcessManager(new TestLogger());
        var result = await mgr.RunCommandAsync("", "");
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("empty", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunCommandAsync_NullBytes_Rejected()
    {
        var mgr = new CrossPlatformProcessManager(new TestLogger());
        var result = await mgr.RunCommandAsync("echo\0injected", "");
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("invalid", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunCommandAsync_TooLong_Rejected()
    {
        var mgr = new CrossPlatformProcessManager(new TestLogger());
        var result = await mgr.RunCommandAsync(new string('x', 9000), "");
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("maximum length", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSystemInfoAsync_ReturnsInfo()
    {
        var mgr = new CrossPlatformProcessManager(new TestLogger());
        var info = await mgr.GetSystemInfoAsync();
        Assert.False(string.IsNullOrEmpty(info.Hostname));
        Assert.True(info.TotalMemoryMB > 0);
        Assert.True(info.ProcessorCount > 0);
    }

    [Fact]
    public async Task ListProcessesAsync_ReturnsProcesses()
    {
        var mgr = new CrossPlatformProcessManager(new TestLogger());
        var procs = await mgr.ListProcessesAsync();
        Assert.NotEmpty(procs);
    }

    [Fact]
    public async Task KillProcessAsync_NonExistentPid_ReturnsFalse()
    {
        var mgr = new CrossPlatformProcessManager(new TestLogger());
        var result = await mgr.KillProcessAsync(999999);
        Assert.False(result);
    }
}
