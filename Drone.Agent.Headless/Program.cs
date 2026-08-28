using Drone.Core;
using Drone.Core.Config;
using Drone.Core.Custody;
using Drone.MCP;
using Drone.MCP.Tools;
using Drone.Services.Relay;
using Drone.Services.Custody;
using Drone.Autonomy;
using Drone.System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Drone.Agent.Headless;

public class Program
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("=== Velocity Drone Headless v3.0.0 ===");

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });
        var logger = new DroneLogger(loggerFactory.CreateLogger("VelocityDrone"));

        logger.LogInformation("Platform: {OS} {Arch}",
            global::System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            global::System.Runtime.InteropServices.RuntimeInformation.OSArchitecture);

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        var droneConfig = new DroneConfig();
        config.GetSection("Drone").Bind(droneConfig);

        droneConfig.Mode = DroneMode.Headless;

        // Environment variable overrides
        var envId = Environment.GetEnvironmentVariable("DRONE_ID");
        if (!string.IsNullOrEmpty(envId)) droneConfig.DroneId = envId;

        var roleStr = Environment.GetEnvironmentVariable("DRONE_ROLE")?.ToLower();
        droneConfig.Role = roleStr switch
        {
            "server" => DroneRole.Server,
            "client" => DroneRole.Client,
            _ => DroneRole.Standalone
        };

        var relayPort = Environment.GetEnvironmentVariable("DRONE_RELAY_PORT");
        if (int.TryParse(relayPort, out var port) && port > 0 && port <= 65535)
            droneConfig.Relay.Port = port;

        var relayKey = Environment.GetEnvironmentVariable("DRONE_RELAY_KEY");
        if (!string.IsNullOrEmpty(relayKey))
            droneConfig.Relay.ApiKey = relayKey;

        droneConfig.Relay.Enabled = droneConfig.Role == DroneRole.Server || droneConfig.Role == DroneRole.Standalone;

        try { droneConfig.Validate(); }
        catch (InvalidOperationException ex)
        {
            logger.LogError("Configuration validation failed: {Error}", ex.Message);
            return;
        }

        logger.LogInformation("Mode: headless, Role: {Role}, DroneId: {Id}",
            droneConfig.Role.ToString().ToLower(), droneConfig.DroneId);

        // Graceful shutdown
        var masterCts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            logger.LogInformation("Shutdown signal received...");
            try { masterCts.Cancel(); } catch (ObjectDisposedException) { }
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { masterCts.Cancel(); } catch (ObjectDisposedException) { }
        };

        // Custody trail
        var custodyLogPath = Environment.GetEnvironmentVariable("DRONE_CUSTODY_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "custody", "drone-custody.jsonl");
        var custodyLogger = new CustodyAuditLogger(droneConfig.DroneId, custodyLogPath, logger: logger);
        custodyLogger.LoadPersistedRecords();
        logger.LogInformation("Custody trail initialized");

        // System components
        var process = PlatformFactory.CreateProcessManager(logger);
        var clipboard = PlatformFactory.CreateClipboardManager(logger);

        // Relay server
        RelayServer? relayServer = null;
        if (droneConfig.Relay.Enabled)
        {
            try
            {
                relayServer = new RelayServer(droneConfig.Relay, droneConfig.DroneId, logger);
                await relayServer.StartAsync(masterCts.Token);
                logger.LogInformation("Relay server started on port {Port}", droneConfig.Relay.Port);
            }
            catch (Exception ex)
            {
                logger.LogError("Relay server failed to start: {Error}", ex.Message);
            }
        }

        // MCP server
        var mcpServer = new McpServer(logger);
        var mcpToken = Environment.GetEnvironmentVariable("DRONE_MCP_TOKEN");
        if (!string.IsNullOrEmpty(mcpToken))
        {
            mcpServer.SetAuthToken(mcpToken);
            logger.LogInformation("MCP auth enabled");
        }

        // Register system tools
        SystemToolRegistrar.RegisterAll(mcpServer, null, null, process, clipboard, null, null, null, null, logger, relayServer);

        // Autonomy engine
        var eventBus = new EventBus();
        var autonomy = new AutonomyEngine(droneConfig.Autonomy, logger);
        await autonomy.StartAsync(eventBus);

        // Start MCP servers
        var mcpWsUrl = Environment.GetEnvironmentVariable("DRONE_MCP_URL") ?? "http://+:9100";
        logger.LogInformation("MCP WebSocket at {Url}", mcpWsUrl);

        var mcpTasks = new List<Task>
        {
            Task.Run(() => mcpServer.RunAsync(droneConfig.Mcp.BufferPath, droneConfig.Mcp.BufferSize, masterCts.Token)),
            Task.Run(() => mcpServer.RunWebSocketAsync(mcpWsUrl, masterCts.Token))
        };

        logger.LogInformation("Registered {Count} MCP tools. Drone ready.", mcpServer.GetToolList().Length);
        custodyLogger.LogConnection("agent_ready", "agent", $"tools={mcpServer.GetToolList().Length}");

        // Wait for shutdown
        try
        {
            await Task.WhenAny(Task.Delay(Timeout.Infinite, masterCts.Token), Task.WhenAll(mcpTasks));
        }
        catch (OperationCanceledException) { }

        logger.LogInformation("Shutdown initiated...");

        // Dispose in reverse order
        var shutdownTimeoutSec = int.TryParse(Environment.GetEnvironmentVariable("DRONE_SHUTDOWN_TIMEOUT"), out var t) ? t : 15;
        try
        {
            var disposalTask = Task.Run(async () =>
            {
                if (mcpServer != null) await mcpServer.DisposeAsync();
                if (relayServer != null) await relayServer.DisposeAsync();
                if (autonomy != null) await autonomy.DisposeAsync();
                if (custodyLogger != null) await custodyLogger.DisposeAsync();
            });

            if (await Task.WhenAny(disposalTask, Task.Delay(TimeSpan.FromSeconds(shutdownTimeoutSec))) != disposalTask)
                logger.LogWarning("Shutdown timeout ({Timeout}s) exceeded", shutdownTimeoutSec);
        }
        catch (Exception ex)
        {
            logger.LogError("Error during shutdown: {Error}", ex.Message);
        }
        finally
        {
            masterCts.Dispose();
        }

        logger.LogInformation("Shutdown complete.");
    }
}

public class DroneLogger : Drone.Core.ILogger
{
    private readonly Microsoft.Extensions.Logging.ILogger _inner;
    public DroneLogger(Microsoft.Extensions.Logging.ILogger inner) => _inner = inner;
    public void LogInformation(string message, params object[] args) => _inner.LogInformation(message, args);
    public void LogWarning(string message, params object[] args) => _inner.LogWarning(message, args);
    public void LogError(string message, params object[] args) => _inner.LogError(message, args);
    public void LogDebug(string message, params object[] args) => _inner.LogDebug(message, args);
}
