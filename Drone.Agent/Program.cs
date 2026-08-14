using Drone.Agent.UI;
using Drone.Core;
using Drone.Core.Config;
using Drone.Core.Custody;
using Drone.MCP;
using Drone.MCP.Tools;
using Drone.Services.Custody;
using Drone.Services.Messenger;
using Drone.Services.Share;
using Drone.Services.Remote;
using Drone.Autonomy;
using Drone.Autonomy.Actions;
using Drone.Agent.Benchmarks;
using Drone.System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Windows.Forms;

namespace Drone.Agent;

public class Program
{
    [STAThread]
    public static async Task Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var trayApp = new TrayApp();

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new TrayLoggerProvider(trayApp));
            builder.SetMinimumLevel(LogLevel.Information);
        });
        var logger = new DroneLogger(loggerFactory.CreateLogger("VelocityDrone"));

        trayApp.SetStatus("Initializing...", false);
        trayApp.Log("[INFO] === Velocity Drone Agent v3.0.0 ===");

        _ = Task.Run(async () => await RunDroneAsync(args, logger, trayApp));

        Application.Run(trayApp);
    }

    private static async Task RunDroneAsync(string[] args, DroneLogger logger, TrayApp trayApp)
    {
        try
        {
            logger.LogInformation("Mode: {Mode}", Environment.GetEnvironmentVariable("DRONE_MODE") ?? "full");
            logger.LogInformation("Platform: {OS} {Arch}",
                global::System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                global::System.Runtime.InteropServices.RuntimeInformation.OSArchitecture);

            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true)
                .Build();

            var droneConfig = new DroneConfig();
            config.GetSection("Drone").Bind(droneConfig);

            droneConfig.Mode = Environment.GetEnvironmentVariable("DRONE_MODE")?.ToLower() == "headless"
                ? DroneMode.Headless : droneConfig.Mode;

            if (Environment.GetEnvironmentVariable("DRONE_ID") is string id && !string.IsNullOrEmpty(id))
                droneConfig.DroneId = id;

            var envWsUrl = Environment.GetEnvironmentVariable("DRONE_WS_URL");
            if (!string.IsNullOrEmpty(envWsUrl)) droneConfig.Uplink.WebSocketUrl = envWsUrl;

            var envMcpUrl = Environment.GetEnvironmentVariable("DRONE_MCP_URL");

            try { droneConfig.Validate(); }
            catch (InvalidOperationException ex)
            {
                logger.LogError("Configuration validation failed: {Error}", ex.Message);
                trayApp.SetStatus("Config Error", false);
                return;
            }

            // --- Custody trail initialization ---
            var custodyLogPath = Environment.GetEnvironmentVariable("DRONE_CUSTODY_PATH")
                ?? Path.Combine(AppContext.BaseDirectory, "custody", "drone-custody.jsonl");
            var custodyLogger = new CustodyAuditLogger(droneConfig.DroneId, custodyLogPath);
            custodyLogger.LoadPersistedRecords(); // Resume chain from disk
            logger.LogInformation("Custody trail initialized (path: {Path})", custodyLogPath);

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
            else { logger.LogInformation("Running in headless mode."); }

            var process = PlatformFactory.CreateProcessManager(logger);
            var clipboard = PlatformFactory.CreateClipboardManager(logger);

            MessengerConnector? messenger = null;
            ShareConnector? share = null;
            RemoteConnector? remote = null;
            CustodyReporter? custodyReporter = null;
            Task? messengerTask = null;
            Task? remoteTask = null;

            if (!string.IsNullOrEmpty(droneConfig.Messenger.ServerUrl))
            {
                messenger = new MessengerConnector(droneConfig.Messenger, droneConfig.DroneId, logger);
                messenger.OnConnectionChanged += (connected) =>
                {
                    custodyLogger.LogConnection(connected ? "messenger_connected" : "messenger_disconnected",
                        "messenger", droneConfig.Messenger.ServerUrl, success: connected);
                    if (connected) { trayApp.SetStatus("Connected", true); trayApp.ShowNotification("Velocity Drone", "Connected to Messenger", ToolTipIcon.Info); }
                    else { trayApp.SetStatus("Disconnected", false); trayApp.ShowNotification("Velocity Drone", "Disconnected", ToolTipIcon.Warning); }
                    return Task.CompletedTask;
                };
                messengerTask = Task.Run(() => messenger.ConnectAsync());
            }

            if (!string.IsNullOrEmpty(droneConfig.Share.ServerUrl))
                share = new ShareConnector(droneConfig.Share, logger);

            var mcpServer = new McpServer(logger);

            if (!string.IsNullOrEmpty(droneConfig.Remote.ServerUrl))
            {
                remote = new RemoteConnector(droneConfig.Remote, logger, custodyLogger);
                remote.OnToolCall += async (toolName, argsData, seqId) =>
                {
                    try
                    {
                        var argsJson = argsData.Length > 0
                            ? global::System.Text.Encoding.UTF8.GetString(argsData)
                            : "{}";
                        logger.LogInformation("[Remote] Tool call: " + toolName + " seq=" + seqId);
                        using var doc = global::System.Text.Json.JsonDocument.Parse(argsJson);
                        var result = await mcpServer.InvokeToolAsync(toolName, doc.RootElement);
                        return global::System.Text.Encoding.UTF8.GetBytes(result.ToString());
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning("[Remote] Tool call failed: " + ex.Message);
                        return global::System.Text.Encoding.UTF8.GetBytes("{\"error\":\"" + ex.Message.Replace("\"", "'") + "\"}");
                    }
                };
                remoteTask = Task.Run(() => remote.ConnectAsync());
            }

            VelocityConnection? uplink = null;
            if (!string.IsNullOrEmpty(droneConfig.Uplink.WebSocketUrl))
                uplink = new VelocityConnection(droneConfig.Uplink, logger);

            var eventBus = new EventBus();
            var autonomy = new AutonomyEngine(droneConfig.Autonomy, logger);

            if (messenger != null)
            {
                messenger.OnMessageReceived += async (from, content, msgId) =>
                {
                    if (!string.IsNullOrEmpty(from) && messenger.IsConnected)
                    {
                        try
                        {
                            var cmd = content?.Trim() ?? "";
                            string response;

                            // Log incoming command to custody trail
                            custodyLogger.LogToolCall("messenger_command", $"from={from},cmd={cmd.Split(' ')[0]}", targetSystem: "messenger");

                            if (cmd.Equals("status", StringComparison.OrdinalIgnoreCase))
                            {
                                var result = await mcpServer.InvokeToolAsync("get_drone_status", global::System.Text.Json.JsonDocument.Parse("{}").RootElement);
                                response = $"Drone Status: {result}";
                            }
                            else if (cmd.StartsWith("upload ", StringComparison.OrdinalIgnoreCase))
                            {
                                var parts = cmd.Substring(7).Split(' ', 2);
                                if (parts.Length == 2)
                                {
                                    var argsJson = global::System.Text.Json.JsonSerializer.Serialize(new { localPath = parts[0], remotePath = parts[1] });
                                    var result = await mcpServer.InvokeToolAsync("upload_file", global::System.Text.Json.JsonDocument.Parse(argsJson).RootElement);
                                    response = $"Upload result: {result}";
                                }
                                else response = "Usage: upload <localPath> <remotePath>";
                            }
                            else if (cmd.StartsWith("download ", StringComparison.OrdinalIgnoreCase))
                            {
                                var parts = cmd.Substring(9).Split(' ', 2);
                                if (parts.Length == 2)
                                {
                                    var argsJson = global::System.Text.Json.JsonSerializer.Serialize(new { remotePath = parts[0], localPath = parts[1] });
                                    var result = await mcpServer.InvokeToolAsync("download_file", global::System.Text.Json.JsonDocument.Parse(argsJson).RootElement);
                                    response = $"Download result: {result}";
                                }
                                else response = "Usage: download <remotePath> <localPath>";
                            }
                            else if (cmd.StartsWith("list", StringComparison.OrdinalIgnoreCase))
                            {
                                var path = cmd.Length > 5 ? cmd.Substring(5).Trim() : null;
                                var argsJson = path != null ? global::System.Text.Json.JsonSerializer.Serialize(new { path }) : "{}";
                                var result = await mcpServer.InvokeToolAsync("list_files", global::System.Text.Json.JsonDocument.Parse(argsJson).RootElement);
                                response = $"Files: {result}";
                            }
                            else if (cmd.Equals("update", StringComparison.OrdinalIgnoreCase))
                            {
                                var scriptPath = @"C:\Drone\update-drone.bat";
                                var script = "@echo off\r\necho Waiting for drone to stop...\r\n:wait\r\ntasklist /fi \"imagename eq velocity-drone.exe\" 2>NUL | find /I /N \"velocity-drone.exe\" >NUL\r\nif %errorlevel%==0 (timeout /t 1 /nobreak >NUL & goto wait)\r\ntimeout /t 2 /nobreak >NUL\r\ncopy /Y \"C:\\Drone\\share\\velocity-drone-new.exe\" \"C:\\Drone\\velocity-drone.exe\" >NUL\r\ndel \"C:\\Drone\\share\\velocity-drone-new.exe\" >NUL\r\ncd /d C:\\Drone\r\nstart \"\" run-drone.bat\r\necho Update complete!\r\ntimeout /t 3 /nobreak >NUL";
                                global::System.IO.File.WriteAllText(scriptPath, script);
                                response = "Starting self-update...";
                                await messenger.SendMessageAsync(from, response);
                                global::System.Diagnostics.Process.Start(new global::System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = scriptPath,
                                    UseShellExecute = true,
                                    CreateNoWindow = false
                                });
                                _ = Task.Run(async () => { await Task.Delay(1000); Environment.Exit(0); });
                                return;
                            }
                            else if (cmd.Equals("benchmark", StringComparison.OrdinalIgnoreCase))
                            {
                                var fileUrl = "http://localhost:5003";
                                var ndaResults = await DroneBenchmark.RunAllAsync(fileUrl, screen, windows, process);
                                var sb = new global::System.Text.StringBuilder();
                                sb.AppendLine("=== Velocity Drone Benchmark Results ===");
                                string currentBench = "";
                                foreach (var t in ndaResults.Triples)
                                {
                                    if (t.Predicate == "name")
                                    {
                                        if (currentBench != "") sb.AppendLine();
                                        currentBench = t.Object;
                                        sb.Append($"  [{t.Object}] ");
                                    }
                                    else if (t.Predicate == "desc")
                                        sb.Append($"{t.Object}: ");
                                    else if (t.Predicate == "elapsedMs")
                                        sb.Append($"{t.Object}ms");
                                    else if (t.Predicate == "iterations")
                                        sb.Append($" ({t.Object} iters)");
                                    else if (t.Predicate == "perOpMs")
                                        sb.Append($" [{t.Object}ms/op]");
                                    else if (t.Predicate == "dataSize")
                                        sb.Append($" size={t.Object}B");
                                }
                                response = sb.ToString();
                            }
                            else if (cmd.StartsWith("run ", StringComparison.OrdinalIgnoreCase))
                            {
                                var command = cmd.Substring(4);
                                var argsJson = global::System.Text.Json.JsonSerializer.Serialize(new { command });
                                var result = await mcpServer.InvokeToolAsync("run_command", global::System.Text.Json.JsonDocument.Parse(argsJson).RootElement);
                                response = $"Command result: {result}";
                            }
                            else if (cmd.StartsWith("type ", StringComparison.OrdinalIgnoreCase))
                            {
                                var text = cmd.Substring(5);
                                var argsJson = global::System.Text.Json.JsonSerializer.Serialize(new { text });
                                var result = await mcpServer.InvokeToolAsync("type_text", global::System.Text.Json.JsonDocument.Parse(argsJson).RootElement);
                                response = $"Type result: {result}";
                            }
                            else if (cmd.StartsWith("key ", StringComparison.OrdinalIgnoreCase))
                            {
                                var key = cmd.Substring(4);
                                var argsJson = global::System.Text.Json.JsonSerializer.Serialize(new { key });
                                var result = await mcpServer.InvokeToolAsync("press_key", global::System.Text.Json.JsonDocument.Parse(argsJson).RootElement);
                                response = $"Key result: {result}";
                            }
                            else if (cmd.StartsWith("click ", StringComparison.OrdinalIgnoreCase))
                            {
                                var parts = cmd.Substring(6).Split(' ');
                                if (parts.Length >= 2 && int.TryParse(parts[0], out var cx) && int.TryParse(parts[1], out var cy))
                                {
                                    var btn = parts.Length >= 3 ? parts[2] : "left";
                                    var argsJson = global::System.Text.Json.JsonSerializer.Serialize(new { x = cx, y = cy, button = btn });
                                    var result = await mcpServer.InvokeToolAsync("click", global::System.Text.Json.JsonDocument.Parse(argsJson).RootElement);
                                    response = $"Click result: {result}";
                                }
                                else response = "Usage: click <x> <y> [left|right]";
                            }
                            else if (cmd.Equals("screenshot", StringComparison.OrdinalIgnoreCase))
                            {
                                var result = await mcpServer.InvokeToolAsync("capture_screen", global::System.Text.Json.JsonDocument.Parse("{}").RootElement);
                                response = $"Screenshot: {result}";
                            }
                            else
                            {
                                response = "[Drone-v2-MARKER] Commands: status, upload, download, list, run, type, key, click, screenshot, update, benchmark";
                            }

                            await messenger.SendMessageAsync(from, response);
                        }
                        catch (Exception ex) { logger.LogWarning("[Messenger] Command failed: {Error}", ex.Message); }
                    }
                    await eventBus.PublishAsync(new DroneEvent(DroneEventTypes.MessageReceived, new { from, content, messageId = msgId }));
                };
            }

            if (share != null)
            {
                share.OnFileChanged += async (path, changeType) =>
                {
                    await eventBus.PublishAsync(new DroneEvent(DroneEventTypes.FileChanged, new { path, changeType }));
                };
            }

            autonomy.OnActionExecuted += async (ruleName, eventType, data) =>
            {
                if (eventType == DroneEventTypes.MessageReceived && messenger != null && messenger.IsConnected)
                {
                    try
                    {
                        var json = global::System.Text.Json.JsonSerializer.Serialize(data);
                        using var doc = global::System.Text.Json.JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("from", out var fromProp))
                        {
                            var fromUser = fromProp.GetString() ?? "";
                            if (!string.IsNullOrEmpty(fromUser))
                                await messenger.SendMessageAsync(fromUser, "[Auto-reply] Acknowledged by Velocity Drone");
                        }
                    }
                    catch { }
                }
                if (uplink != null && uplink.IsConnected)
                {
                    try
                    {
                        var notification = global::System.Text.Json.JsonSerializer.Serialize(new
                        {
                            jsonrpc = "2.0",
                            method = "notifications/droneEvent",
                            @params = new { eventType, data, ruleName }
                        });
                        await uplink.SendNotificationAsync(notification);
                    }
                    catch { }
                }
            };

            await autonomy.StartAsync(eventBus);
            SystemToolRegistrar.RegisterAll(mcpServer, screen, input, process, clipboard, windows, messenger, share, remote, logger);

            var mcpToken = Environment.GetEnvironmentVariable("DRONE_MCP_TOKEN");
            if (!string.IsNullOrEmpty(mcpToken)) { mcpServer.SetAuthToken(mcpToken); logger.LogInformation("MCP auth enabled"); }
            else logger.LogWarning("MCP WebSocket has NO authentication");

            if (uplink != null)
            {
                uplink.OnRequest += async request =>
                {
                    var response = await mcpServer.HandleRequestAsync(request, "uplink");
                    await uplink.SendResponseAsync(global::System.Text.Json.JsonSerializer.Serialize(response));
                };
            }

            var cts = new CancellationTokenSource();

            Drone.Services.Share.EmbeddedFileServer? fileServer = null;
            if (droneConfig.Share.Enabled)
            {
                try
                {
                    var storagePath = Environment.GetEnvironmentVariable("DRONE_SHARE_PATH") ?? @"C:\Drone\share";
                    var serverUrl = droneConfig.Share.ServerUrl ?? "http://+:5003";
                    var uri = new global::System.Uri(serverUrl);
                    var shareListenUrl = $"http://+:{uri.Port}/";
                    if (!shareListenUrl.EndsWith("/")) shareListenUrl += "/";
                    fileServer = new Drone.Services.Share.EmbeddedFileServer(logger, storagePath, shareListenUrl, droneConfig.Share.AdminApiKey ?? "");
                    await fileServer.StartAsync(cts.Token);
                    logger.LogInformation("File server at {Url}", shareListenUrl);
                    trayApp.ShowNotification("File Server", $"Listening on port {uri.Port}", ToolTipIcon.Info);
                }
                catch (Exception ex) { logger.LogWarning("File server failed: {Error}", ex.Message); }
            }

            var mcpTasks = new List<Task>();
            logger.LogInformation("MCP NMCP at {Path}", droneConfig.Mcp.BufferPath);
            mcpTasks.Add(Task.Run(() => mcpServer.RunAsync(droneConfig.Mcp.BufferPath, droneConfig.Mcp.BufferSize, cts.Token)));

            var mcpWsUrl = envMcpUrl ?? "http://+:9100";
            logger.LogInformation("MCP WebSocket at {Url}", mcpWsUrl);
            mcpTasks.Add(Task.Run(() => mcpServer.RunWebSocketAsync(mcpWsUrl, cts.Token)));

            // --- Start CustodyReporter (streams custody records to CustodyServer) ---
            var custodyServerUrl = Environment.GetEnvironmentVariable("DRONE_CUSTODY_SERVER");
            if (!string.IsNullOrEmpty(custodyServerUrl))
            {
                custodyLogger.LogConnection("custody_reporter_init", "custody_server", custodyServerUrl);
                custodyReporter = new CustodyReporter(custodyLogger, logger);
                // The send function will be wired when connected to the custody server
                // For now, start the reporter — it will queue records until connected
                await custodyReporter.StartAsync(cts.Token);
                logger.LogInformation("CustodyReporter started (server: {Url})", custodyServerUrl);
            }

            if (uplink != null)
            {
                try { await uplink.ConnectAsync(cts.Token); logger.LogInformation("Uplink connected"); }
                catch (Exception ex) { logger.LogWarning("Uplink failed: {Error}", ex.Message); }
            }

            logger.LogInformation("Registered {Count} MCP tools. Drone ready.", mcpServer.GetToolList().Length);
            trayApp.SetStatus("Ready", messenger?.IsConnected ?? false);

            await Task.WhenAny(mcpTasks);
        }
        catch (Exception ex)
        {
            logger.LogError("Fatal: {Error}", ex);
            trayApp.SetStatus("Fatal Error", false);
        }
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
