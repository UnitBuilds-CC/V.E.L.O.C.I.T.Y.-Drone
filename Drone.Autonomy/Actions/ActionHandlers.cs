using global::System.Diagnostics;
using Drone.Core;

namespace Drone.Autonomy.Actions;

public static class ActionHandlers
{
    public static Task LogAction(DroneEvent evt, Dictionary<string, string> parms, ILogger logger)
    {
        var level = parms.GetValueOrDefault("level", "info");
        logger.LogInformation("[Autonomy] " + level + ": Event " + evt.Type);
        return Task.CompletedTask;
    }

    public static async Task RunCommandAction(DroneEvent evt, Dictionary<string, string> parms, ILogger logger)
    {
        var command = parms.GetValueOrDefault("command", "");
        var args = parms.GetValueOrDefault("args", "");
        if (string.IsNullOrEmpty(command)) return;
        var psi = new ProcessStartInfo { FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash", Arguments = OperatingSystem.IsWindows() ? "/c " + command + " " + args : "-c \"" + command + " " + args + "\"", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(psi);
        if (process != null) { var stdout = await process.StandardOutput.ReadToEndAsync(); await process.WaitForExitAsync(); logger.LogInformation("[Autonomy] Command exited with " + process.ExitCode); }
    }
}
