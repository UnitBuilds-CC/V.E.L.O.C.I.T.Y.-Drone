using Drone.Core;
using Drone.Core.Config;
using Drone.MCP;
using Drone.MCP.Tools;
using Drone.Services.Messenger;
using Drone.Services.Share;
using Drone.Services.Remote;
using Drone.Autonomy;
using Drone.Autonomy.Actions;
using Drone.System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Drone.Agent;

/// <summary>
/// Velocity Drone Agent â€” main entry point.
/// Wires together all modules and starts the MCP server.
/// </summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        // â”€â”€ Logging â”€â”€
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });
        var logger = new DroneLogger(loggerFactory.CreateLogger("VelocityDrone"));

        logger.LogInformation("=== Velocity Drone Agent v1.0.0 ===");
        logger.LogInformation("Mode: {Mode}", Environment.GetEnvironmentVariable("DRONE_MODE") ?? "full");
        logger.LogInformation("Platform: {OS} {Arch}",
            global::System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            global::System.Runtime.InteropServices.RuntimeInformation.OSArchitecture);

        // â”€â”€ Configuration â”€â”€
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        var droneConfig = new DroneConfig();
        config.GetSection("Drone").Bind(droneConfig);

        // Override from environment
        droneConfig.Mode = Environment.GetEnvironmentVariable("DRONE_MODE")?.ToLower() == "headless"
            ? DroneMode.Headless : droneConfig.Mode;

        if (Environment.GetEnvironmentVariable("DRONE_ID") is string id) droneConfig.DroneId = id;

        // â”€â”€ System Modules â”€â”€
        IScreenCapture? screen = null;
        IInputSimulator? input = null;
        IWindowManager? windows = null;

        if (droneConfig.Mode != DroneMode.Headless)
        {
            try { screen = PlatformFactory.CreateScreenCapture(logger); }
            catch (Exception ex) { logger.LogWarning("Screen capture unavailable: {Error}", ex.Message); }

            try { input = PlatformFactory.CreateInputSimulator(logger); }
            catch (Exception ex) { logger.LogWarning("Input simulation unavailable: {Error}", ex.Message); }

            try { windows = PlatformFactory.CreateWindowManager(logger); }
            catch (Exception ex) { logger.LogWarning("Window management unavailable: {Error}", ex.Message); }
        }
        else
        {
            logger.LogInformation("Running in headless mode â€” screen/input disabled.");
        }

        var process = PlatformFactory.CreateProcessManager(logger);
        var clipboard = PlatformFactory.CreateClipboardManager(logger);

        // â”€â”€ Service Connectors â”€â”€
        MessengerConnector? messenger = null;
        ShareConnector? share = null;
        RemoteConnector? remote = null;

        if (!string.IsNullOrEmpty(droneConfig.Messenger.ServerUrl))
        {
            messenger = new MessengerConnector(droneConfig.Messenger, logger);
            _ = Task.Run(() => messenger.ConnectAsync());
        }

        if (!string.IsNullOrEmpty(droneConfig.Share.ServerUrl))
        {
            share = new ShareConnector(droneConfig.Share, logger);
        }

        if (!string.IsNullOrEmpty(droneConfig.Remote.ServerUrl))
        {
            remote = new RemoteConnector(droneConfig.Remote, logger);
            _ = Task.Run(() => remote.ConnectAsync());
        }

        // â”€â”€ Autonomy Engine â”€â”€
        var eventBus = new EventBus();
        var autonomy = new AutonomyEngine(droneConfig.Autonomy, logger);

        // Wire event bus to service events
        if (messenger != null)
        {
            messenger.OnMessageReceived += async (from, content, msgId) =>
            {
                await eventBus.PublishAsync(new DroneEvent(DroneEventTypes.MessageReceived,
                    new { from, content, messageId = msgId }));
            };
        }

        if (share != null)
        {
            share.OnFileChanged += async (path, changeType) =>
            {
                await eventBus.PublishAsync(new DroneEvent(DroneEventTypes.FileChanged,
                    new { path, changeType }));
            };
        }

        // Wire autonomy action output to service connectors
        autonomy.OnActionExecuted += async (ruleName, eventType, data) =>
        {
            // Auto-reply: send response via Messenger
            if (eventType == DroneEventTypes.MessageReceived && messenger != null)
            {
                // Find auto_reply rules that matched
                // The actual reply logic is handled by ActionHandlers.AutoReplyAction
            }

            // Notify AI: send event via uplink
            if (OnUplinkSend != null)
            {
                var notification = global::System.Text.Json.JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    method = "notifications/droneEvent",
                    @params = new { eventType, data, ruleName }
                });
                await OnUplinkSend(notification);
            }
        };

        await autonomy.StartAsync(eventBus);

        // â”€â”€ MCP Tool Server â”€â”€
        var mcpServer = new McpServer(logger);
        SystemToolRegistrar.RegisterAll(mcpServer, screen, input, process, clipboard, windows, messenger, share, remote, logger);

        // Determine MCP transport mode
        var mcpMode = Environment.GetEnvironmentVariable("DRONE_MCP_MODE") ?? "stdio";
        logger.LogInformation("Starting MCP server ({Mode} mode)...", mcpMode);
        logger.LogInformation("Registered {Count} tools", mcpServer.GetToolList().Length);

        // â”€â”€ Run â”€â”€
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            if (mcpMode == "shmem")
            {
                var bufferPath = droneConfig.Uplink.BufferPath;
                var bufferSize = droneConfig.Uplink.BufferSize;
                await mcpServer.RunShmemAsync(bufferPath, bufferSize, cts.Token);
            }
            else
            {
                await mcpServer.RunStdioAsync(cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError("Fatal error: {Error}", ex);
        }
        finally
        {
            logger.LogInformation("Shutting down...");
            if (messenger != null) await messenger.DisposeAsync();
            if (share != null) await share.DisposeAsync();
            if (remote != null) await remote.DisposeAsync();
            await autonomy.DisposeAsync();
            await mcpServer.DisposeAsync();
        }
    }

    // Uplink send callback (set when VelocityConnection is active)
    private static Func<string, Task>? OnUplinkSend;
}

/// <summary>Adapter to bridge Microsoft.Extensions.Logging to Drone.Core.ILogger.</summary>
public class DroneLogger : Drone.Core.ILogger
{
    private readonly Microsoft.Extensions.Logging.ILogger _inner;
    public DroneLogger(Microsoft.Extensions.Logging.ILogger inner) => _inner = inner;
    public void LogInformation(string message, params object[] args) => _inner.LogInformation(message, args);
    public void LogWarning(string message, params object[] args) => _inner.LogWarning(message, args);
    public void LogError(string message, params object[] args) => _inner.LogError(message, args);
    public void LogDebug(string message, params object[] args) => _inner.LogDebug(message, args);
}
