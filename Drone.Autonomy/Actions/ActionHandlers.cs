using global::System.Diagnostics;
using global::System.Text.Json;
using Drone.Core;

namespace Drone.Autonomy.Actions;

public static class ActionHandlers
{
    public static Task LogAction(DroneEvent evt, Dictionary<string, string> parms, ILogger logger)
    {
        var level = parms.GetValueOrDefault("level", "info");
        logger.LogInformation("[Autonomy] " + level + ": Event " + evt.Type + " â€” " + JsonSerializer.Serialize(evt.Data));
        return Task.CompletedTask;
    }

    public static async Task RunCommandAction(DroneEvent evt, Dictionary<string, string> parms, ILogger logger)
    {
        var command = parms.GetValueOrDefault("command", "");
        var args = parms.GetValueOrDefault("args", "");
        if (string.IsNullOrEmpty(command)) return;
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
            Arguments = OperatingSystem.IsWindows() ? "/c " + command + " " + args : "-c \"" + command + " " + args + "\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi);
        if (process != null)
        {
            var stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            logger.LogInformation("[Autonomy] Command '" + command + "' exited with " + process.ExitCode);
        }
    }

    public static async Task AutoReplyAction(DroneEvent evt, Dictionary<string, string> parms, ILogger logger)
    {
        if (evt.Type != DroneEventTypes.MessageReceived) return;
        var reply = parms.GetValueOrDefault("reply", "Auto-reply: acknowledged");
        logger.LogInformation("[Autonomy] AutoReply: " + reply);
        // The actual reply is sent via the OnActionExecuted callback wired in Program.cs
        // which connects to the MessengerConnector
    }

    public static async Task FileSyncAction(DroneEvent evt, Dictionary<string, string> parms, ILogger logger)
    {
        var localFolder = parms.GetValueOrDefault("localFolder", "");
        var remoteFolder = parms.GetValueOrDefault("remoteFolder", "");
        if (string.IsNullOrEmpty(localFolder) || string.IsNullOrEmpty(remoteFolder))
        {
            logger.LogWarning("[Autonomy] FileSync: missing localFolder or remoteFolder in action params");
            return;
        }
        logger.LogInformation("[Autonomy] FileSync triggered for " + localFolder + " -> " + remoteFolder);
        // Actual sync is handled via the OnActionExecuted callback wired to ShareConnector
    }

    public static async Task ScreenMonitorAction(DroneEvent evt, Dictionary<string, string> parms, ILogger logger)
    {
        var threshold = parms.GetValueOrDefault("threshold", "0.1");
        logger.LogInformation("[Autonomy] ScreenMonitor: checking for visual changes (threshold=" + threshold + ")");
        // Screen diff is handled via the OnActionExecuted callback wired to IScreenCapture
    }

    public static async Task NotifyAIAction(DroneEvent evt, Dictionary<string, string> parms, ILogger logger)
    {
        var notification = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "notifications/droneEvent",
            @params = new { eventType = evt.Type, data = evt.Data, timestamp = evt.Timestamp }
        });
        logger.LogInformation("[Autonomy] NotifyAI: " + evt.Type);
        // The actual notification is sent via the OnActionExecuted callback wired to VelocityConnection
    }

    public static Func<DroneEvent, Dictionary<string, string>, ILogger, Task> GetHandler(string actionName)
    {
        return actionName switch
        {
            "log" => LogAction,
            "run_command" => RunCommandAction,
            "auto_reply" => AutoReplyAction,
            "file_sync" => FileSyncAction,
            "screen_monitor" => ScreenMonitorAction,
            "notify_ai" => NotifyAIAction,
            _ => LogAction
        };
    }
}
