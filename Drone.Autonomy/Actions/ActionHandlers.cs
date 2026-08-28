using global::System.Diagnostics;
using global::System.Text.Json;
using Drone.Core;

namespace Drone.Autonomy.Actions;

public static class ActionHandlers
{
    /// <summary>Default command timeout in seconds.</summary>
    private const int DefaultTimeoutSec = 30;

    public static Task LogAction(DroneEvent evt, Dictionary<string, JsonElement> parms, ILogger logger)
    {
        var level = parms.TryGetValue("level", out var lv) ? lv.GetString() ?? "info" : "info";
        logger.LogInformation("[Autonomy] {Level}: Event {EventType} data={Data}", level, evt.Type, JsonSerializer.Serialize(evt.Data));
        return Task.CompletedTask;
    }

    public static async Task RunCommandAction(DroneEvent evt, Dictionary<string, JsonElement> parms, ILogger logger)
    {
        var command = parms.TryGetValue("command", out var cmd) ? cmd.GetString() ?? "" : "";
        var args = parms.TryGetValue("args", out var a) ? a.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(command)) return;

        // Security: block dangerous commands
        var blocked = new[] { "format", "del /", "rm -rf", "mkfs", "dd if=" };
        var lowerCmd = command.ToLowerInvariant();
        foreach (var b in blocked)
        {
            if (lowerCmd.Contains(b))
            {
                logger.LogWarning("[Autonomy] Blocked dangerous command: {Command}", command);
                return;
            }
        }

        var timeoutSec = parms.TryGetValue("timeout", out var to) && to.TryGetInt32(out var t) ? t : DefaultTimeoutSec;

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
            // Read stdout and stderr concurrently to avoid deadlock from full buffers
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                logger.LogInformation("[Autonomy] Command '{Command}' exited with {ExitCode} (stdout={Len} chars)",
                    command, process.ExitCode, stdout.Length);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* process may have already exited */ }
                logger.LogWarning("[Autonomy] Command '{Command}' timed out after {Timeout}s, killed", command, timeoutSec);
            }
        }
    }

    public static Task AutoReplyAction(DroneEvent evt, Dictionary<string, JsonElement> parms, ILogger logger)
    {
        if (evt.Type != DroneEventTypes.MessageReceived) return Task.CompletedTask;
        var reply = parms.TryGetValue("reply", out var r) ? r.GetString() ?? "Auto-reply: acknowledged" : "Auto-reply: acknowledged";
        logger.LogInformation("[Autonomy] AutoReply: {Reply}", reply);
        // Actual reply is sent via OnActionExecuted callback in Program.cs
        return Task.CompletedTask;
    }

    public static Task FileSyncAction(DroneEvent evt, Dictionary<string, JsonElement> parms, ILogger logger)
    {
        var localFolder = parms.TryGetValue("localFolder", out var lf) ? lf.GetString() ?? "" : "";
        var remoteFolder = parms.TryGetValue("remoteFolder", out var rf) ? rf.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(localFolder) || string.IsNullOrEmpty(remoteFolder))
        {
            logger.LogWarning("[Autonomy] FileSync: missing localFolder or remoteFolder in action params");
            return Task.CompletedTask;
        }
        logger.LogInformation("[Autonomy] FileSync triggered for {Local} -> {Remote}", localFolder, remoteFolder);
        // Actual sync handled via OnActionExecuted callback wired to ShareConnector
        return Task.CompletedTask;
    }

    public static Task ScreenMonitorAction(DroneEvent evt, Dictionary<string, JsonElement> parms, ILogger logger)
    {
        var threshold = parms.TryGetValue("threshold", out var th) ? th.GetString() ?? "0.1" : "0.1";
        logger.LogInformation("[Autonomy] ScreenMonitor: checking for visual changes (threshold={Threshold})", threshold);
        return Task.CompletedTask;
    }

    public static Task NotifyAIAction(DroneEvent evt, Dictionary<string, JsonElement> parms, ILogger logger)
    {
        logger.LogInformation("[Autonomy] NotifyAI: {EventType}", evt.Type);
        // Actual notification sent via OnActionExecuted callback wired to VelocityConnection
        return Task.CompletedTask;
    }

    public static Func<DroneEvent, Dictionary<string, JsonElement>, ILogger, Task> GetHandler(string actionName)
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
