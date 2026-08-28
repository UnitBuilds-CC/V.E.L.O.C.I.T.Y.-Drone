using Drone.Agent.UI;
using Drone.Core;
using Drone.Core.Config;
using Drone.Core.Custody;
using Drone.MCP;
using Drone.MCP.Tools;
using Drone.Services.Relay;
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
        // --- Diagnostics mode: dump system info and exit ---
        if (args.Any(a => a.Equals("--diagnostics", StringComparison.OrdinalIgnoreCase)))
        {
            var osDesc = global::System.Runtime.InteropServices.RuntimeInformation.OSDescription;
            var osArch = global::System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
            var fwDesc = global::System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
            var nativeDll = global::System.IO.Path.Combine(AppContext.BaseDirectory, "velocity_delta.dll");
            var nativeV2 = global::System.IO.Path.Combine(AppContext.BaseDirectory, "velocity_v2_ffi.dll");
            var mcpTokenSet = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DRONE_MCP_TOKEN"));
            var wsUrlSet = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DRONE_WS_URL"));
            Console.WriteLine("=== Velocity Drone Diagnostics ===");
            Console.WriteLine($"Version:      1.0.0");
            Console.WriteLine($"OS:           {osDesc}");
            Console.WriteLine($"Arch:         {osArch}");
            Console.WriteLine($"Runtime:      {fwDesc}");
            Console.WriteLine($"PID:          {Environment.ProcessId}");
            Console.WriteLine($"Base Dir:     {AppContext.BaseDirectory}");
            Console.WriteLine($"DRONE_MODE:   {Environment.GetEnvironmentVariable("DRONE_MODE") ?? "full"}");
            Console.WriteLine($"DRONE_ID:     {Environment.GetEnvironmentVariable("DRONE_ID") ?? "(default)"}");
            Console.WriteLine($"DRONE_MCP_URL:{Environment.GetEnvironmentVariable("DRONE_MCP_URL") ?? "http://+:9100"}");
            Console.WriteLine($"DRONE_MCP_TOKEN: {(mcpTokenSet ? "***configured***" : "(not set)")}");
            Console.WriteLine($"DRONE_WS_URL: {(wsUrlSet ? "***configured***" : "(not set)")}");
            Console.WriteLine($"DRONE_ALLOWED_PATHS: {Environment.GetEnvironmentVariable("DRONE_ALLOWED_PATHS") ?? "(all)"}");
            Console.WriteLine($"DRONE_ROLE:     {Environment.GetEnvironmentVariable("DRONE_ROLE") ?? "standalone"}");
            Console.WriteLine($"DRONE_RELAY_PORT: {Environment.GetEnvironmentVariable("DRONE_RELAY_PORT") ?? "9200"}");
            Console.WriteLine($"DRONE_RELAY_URL:  {Environment.GetEnvironmentVariable("DRONE_RELAY_URL") ?? "(not set)"}");
            var tlsCert = Environment.GetEnvironmentVariable("DRONE_RELAY_TLS_CERT");
            Console.WriteLine($"DRONE_RELAY_TLS:  {(tlsCert != null ? $"enabled ({tlsCert})" : "disabled")}");
            Console.WriteLine($"DRONE_RATE_LIMIT: {Environment.GetEnvironmentVariable("DRONE_RELAY_RATE_LIMIT") ?? "30"} msg/s");
            Console.WriteLine($"Native DLL (delta):  {(global::System.IO.File.Exists(nativeDll) ? "present" : "MISSING")}");
            Console.WriteLine($"Native DLL (v2 FFI): {(global::System.IO.File.Exists(nativeV2) ? "present" : "MISSING")}");
            Console.WriteLine("=== End Diagnostics ===");
            return;
        }

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
        // Declare all disposable resources before try so finally can clean them up
        MessengerConnector? messenger = null;
        ShareConnector? share = null;
        RemoteConnector? remote = null;
        CustodyReporter? custodyReporter = null;
        VelocityConnection? uplink = null;
        McpServer? mcpServer = null;
        AutonomyEngine? autonomy = null;
        CustodyAuditLogger? custodyLogger = null;
        Drone.Services.Share.EmbeddedFileServer? fileServer = null;
        RelayServer? relayServer = null;
        CancellationTokenSource? masterCts = null;

        try
        {
            // --- Graceful shutdown signal handling ---
            masterCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                logger.LogInformation("Ctrl+C received, initiating graceful shutdown...");
                try { masterCts.Cancel(); } catch (ObjectDisposedException) { }
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                logger.LogInformation("Process exit received, initiating graceful shutdown...");
                try { masterCts.Cancel(); } catch (ObjectDisposedException) { }
            };

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

            // --- Role & Relay configuration ---
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

            var relayUrl = Environment.GetEnvironmentVariable("DRONE_RELAY_URL");
            if (!string.IsNullOrEmpty(relayUrl))
                droneConfig.Relay.RelayUrl = relayUrl;

            var relayKey = Environment.GetEnvironmentVariable("DRONE_RELAY_KEY");
            if (!string.IsNullOrEmpty(relayKey))
                droneConfig.Relay.ApiKey = relayKey;

            var rateLimit = Environment.GetEnvironmentVariable("DRONE_RELAY_RATE_LIMIT");
            if (int.TryParse(rateLimit, out var rps) && rps >= 0)
                droneConfig.Relay.MaxMessagesPerSecond = rps;

            var tlsCert = Environment.GetEnvironmentVariable("DRONE_RELAY_TLS_CERT");
            if (!string.IsNullOrEmpty(tlsCert))
                droneConfig.Relay.TlsCertificatePath = tlsCert;

            var tlsCertPass = Environment.GetEnvironmentVariable("DRONE_RELAY_TLS_PASS");
            if (!string.IsNullOrEmpty(tlsCertPass))
                droneConfig.Relay.TlsCertificatePassword = tlsCertPass;

            // Auto-enable relay based on role
            droneConfig.Relay.Enabled = droneConfig.Role == DroneRole.Server || droneConfig.Role == DroneRole.Standalone;

            logger.LogInformation("Mode: {Mode}, Role: {Role}",
                droneConfig.Mode.ToString().ToLower(),
                droneConfig.Role.ToString().ToLower());

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

            // Warn about missing secrets for configured services
            if (!string.IsNullOrEmpty(droneConfig.Messenger.ServerUrl) && string.IsNullOrEmpty(droneConfig.Messenger.ConnectionSecret))
                logger.LogWarning("Messenger configured without ConnectionSecret — authentication may fail");
            if (!string.IsNullOrEmpty(droneConfig.Share.ServerUrl) && string.IsNullOrEmpty(droneConfig.Share.AdminApiKey))
                logger.LogWarning("Share configured without AdminApiKey — operations may be unauthorized");
            if (!string.IsNullOrEmpty(droneConfig.Remote.ServerUrl) && string.IsNullOrEmpty(droneConfig.Remote.ApiKey))
                logger.LogWarning("Remote configured without ApiKey — authentication may fail");

            // --- Custody trail initialization ---
            var custodyLogPath = Environment.GetEnvironmentVariable("DRONE_CUSTODY_PATH")
                ?? Path.Combine(AppContext.BaseDirectory, "custody", "drone-custody.jsonl");
            custodyLogger = new CustodyAuditLogger(droneConfig.DroneId, custodyLogPath, logger: logger);
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

            Task? messengerTask = null;
            Task? remoteTask = null;

            // --- Start Relay Server (if server or standalone role) ---
            if (droneConfig.Relay.Enabled)
            {
                try
                {
                    relayServer = new RelayServer(droneConfig.Relay, droneConfig.DroneId, logger);
                    await relayServer.StartAsync(masterCts!.Token);
                    logger.LogInformation("Relay server started on port {Port}", droneConfig.Relay.Port);
                    trayApp.ShowNotification("Relay Server", $"Listening on port {droneConfig.Relay.Port}", ToolTipIcon.Info);
                }
                catch (Exception ex)
                {
                    logger.LogError("Relay server failed to start: {Error}", ex.Message);
                    relayServer = null;
                }
            }

            // --- Auto-configure client connectors for relay (if client or standalone role) ---
            if (droneConfig.Role == DroneRole.Client || droneConfig.Role == DroneRole.Standalone)
            {
                var relayBase = droneConfig.Relay.RelayUrl ?? $"http://localhost:{droneConfig.Relay.Port}";
                var wsRelayBase = relayBase.Replace("http://", "ws://").Replace("https://", "wss://");

                // Auto-set messenger URL if not explicitly configured
                if (string.IsNullOrEmpty(droneConfig.Messenger.ServerUrl) && droneConfig.Relay.Enabled)
                {
                    droneConfig.Messenger.ServerUrl = $"{wsRelayBase}/relay/messenger/";
                    droneConfig.Messenger.ConnectionSecret ??= droneConfig.Relay.ApiKey;
                    logger.LogInformation("Auto-configured Messenger -> {Url}", droneConfig.Messenger.ServerUrl);
                }

                // Auto-set share URL if not explicitly configured
                if (string.IsNullOrEmpty(droneConfig.Share.ServerUrl) && droneConfig.Relay.Enabled)
                {
                    droneConfig.Share.ServerUrl = $"{relayBase}/relay/share/";
                    droneConfig.Share.AdminApiKey ??= droneConfig.Relay.ApiKey;
                    droneConfig.Share.Enabled = true;
                    logger.LogInformation("Auto-configured Share -> {Url}", droneConfig.Share.ServerUrl);
                }

                // Auto-set remote URL if not explicitly configured
                if (string.IsNullOrEmpty(droneConfig.Remote.ServerUrl) && droneConfig.Relay.Enabled)
                {
                    droneConfig.Remote.ServerUrl = $"{wsRelayBase}/relay/remote/";
                    droneConfig.Remote.ApiKey ??= droneConfig.Relay.ApiKey;
                    logger.LogInformation("Auto-configured Remote -> {Url}", droneConfig.Remote.ServerUrl);
                }
            }

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

            mcpServer = new McpServer(logger);

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
                        logger.LogWarning("[Remote] Tool call failed: {Error}", ex.Message);
                        // Sanitize error response — don't leak internal paths or stack traces
                        var safeError = ex is UnauthorizedAccessException ? "Access denied"
                            : ex is FileNotFoundException ? "File not found"
                            : ex is OperationCanceledException ? "Operation cancelled"
                            : "Internal error";
                        return global::System.Text.Encoding.UTF8.GetBytes("{\"error\":\"" + safeError + "\"}");
                    }
                };
                remoteTask = Task.Run(() => remote.ConnectAsync());
            }

            uplink = null;
            if (!string.IsNullOrEmpty(droneConfig.Uplink.WebSocketUrl))
                uplink = new VelocityConnection(droneConfig.Uplink, logger);

            var eventBus = new EventBus();
            autonomy = new AutonomyEngine(droneConfig.Autonomy, logger);

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
                                // Configurable update paths via env var or config
                                var droneBaseDir = Environment.GetEnvironmentVariable("DRONE_INSTALL_DIR")
                                    ?? AppContext.BaseDirectory;
                                var scriptPath = Environment.GetEnvironmentVariable("DRONE_UPDATE_SCRIPT")
                                    ?? Path.Combine(droneBaseDir, "update-drone.bat");
                                var newBinaryPath = Path.Combine(droneBaseDir, "share", "velocity-drone-new.exe");
                                var targetBinaryPath = Path.Combine(droneBaseDir, "velocity-drone.exe");

                                // Validate that the new binary exists before proceeding
                                if (!global::System.IO.File.Exists(newBinaryPath))
                                {
                                    response = $"Update failed: new binary not found at {newBinaryPath}";
                                    logger.LogWarning("Update command failed: {Error}", response);
                                }
                                else
                                {
                                    // Verify SHA-256 checksum against sidecar file
                                    var checksumPath = newBinaryPath + ".sha256";
                                    if (!global::System.IO.File.Exists(checksumPath))
                                    {
                                        response = "Update failed: no checksum file found (.sha256 sidecar required)";
                                        logger.LogWarning("Update rejected: {Path}", response);
                                    }
                                    else
                                    {
                                        using var sha256 = global::System.Security.Cryptography.SHA256.Create();
                                        var hashBytes = sha256.ComputeHash(global::System.IO.File.ReadAllBytes(newBinaryPath));
                                        var actualHash = Convert.ToHexString(hashBytes);
                                        var expectedHash = global::System.IO.File.ReadAllText(checksumPath).Trim().ToUpperInvariant();

                                        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                                        {
                                            response = $"Update failed: checksum mismatch (expected {expectedHash[..16]}..., got {actualHash[..16]}...)";
                                            logger.LogWarning("Update rejected: checksum mismatch");
                                        }
                                        else
                                        {
                                            var checksum = actualHash[..16];
                                            logger.LogInformation("Update binary checksum verified: {Checksum} ({Size} bytes)", checksum, new global::System.IO.FileInfo(newBinaryPath).Length);

                                            var script = $"@echo off\r\necho Waiting for drone to stop...\r\n:wait\r\ntasklist /fi \"imagename eq velocity-drone.exe\" 2>NUL | find /I /N \"velocity-drone.exe\" >NUL\r\nif %errorlevel%==0 (timeout /t 1 /nobreak >NUL & goto wait)\r\ntimeout /t 2 /nobreak >NUL\r\ncopy /Y \"{newBinaryPath}\" \"{targetBinaryPath}\" >NUL\r\ndel \"{newBinaryPath}\" >NUL\r\ncd /d {droneBaseDir}\r\nstart \"\" run-drone.bat\r\necho Update complete!\r\ntimeout /t 3 /nobreak >NUL";
                                            global::System.IO.File.WriteAllText(scriptPath, script);
                                            response = $"Starting self-update (checksum: {checksum})...";
                                            await messenger.SendMessageAsync(from, response);
                                            global::System.Diagnostics.Process.Start(new global::System.Diagnostics.ProcessStartInfo
                                            {
                                                FileName = scriptPath,
                                                UseShellExecute = true,
                                                CreateNoWindow = false
                                            });
                                            _ = Task.Run(async () => { await Task.Delay(1000); masterCts?.Cancel(); });
                                            return;
                                        }
                                    }
                                }
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
                                // Input validation: reject commands with shell metacharacters that could be used for injection
                                if (command.Contains('|') || command.Contains('&') || command.Contains(';') ||
                                    command.Contains('`') || command.Contains('$') || command.Contains('(') || command.Contains(')') ||
                                    command.Contains('>') || command.Contains('<') || command.Contains('^') ||
                                    command.Contains('%') || command.Contains('!') || command.Contains('\n') || command.Contains('\r'))
                                {
                                    response = "Error: command contains disallowed characters (| & ; ` $ ( ) > < ^ % ! newlines)";
                                }
                                else
                                {
                                    var argsJson = global::System.Text.Json.JsonSerializer.Serialize(new { command });
                                    var result = await mcpServer.InvokeToolAsync("run_command", global::System.Text.Json.JsonDocument.Parse(argsJson).RootElement);
                                    response = $"Command result: {result}";
                                }
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
                                    // Coordinate bounds validation
                                    if (cx < -10000 || cx > 10000 || cy < -10000 || cy > 10000)
                                    {
                                        response = "Error: coordinates out of bounds (must be -10000 to 10000)";
                                    }
                                    else
                                    {
                                        var btn = parts.Length >= 3 ? parts[2] : "left";
                                        var argsJson = global::System.Text.Json.JsonSerializer.Serialize(new { x = cx, y = cy, button = btn });
                                        var result = await mcpServer.InvokeToolAsync("click", global::System.Text.Json.JsonDocument.Parse(argsJson).RootElement);
                                        response = $"Click result: {result}";
                                    }
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
                    catch (Exception ex) { logger.LogWarning("[Autonomy] Auto-reply failed for rule {Rule}: {Error}", ruleName, ex.Message); }
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
                    catch (Exception ex) { logger.LogWarning("[Autonomy] Uplink notification failed for rule {Rule}: {Error}", ruleName, ex.Message); }
                }
            };

            await autonomy.StartAsync(eventBus);
            SystemToolRegistrar.RegisterAll(mcpServer, screen, input, process, clipboard, windows, messenger, share, remote, logger, relayServer);

            var mcpToken = Environment.GetEnvironmentVariable("DRONE_MCP_TOKEN");
            if (!string.IsNullOrEmpty(mcpToken)) { mcpServer.SetAuthToken(mcpToken); logger.LogInformation("MCP auth enabled (token configured)"); }
            else if (!string.IsNullOrEmpty(droneConfig.Remote.ServerUrl))
            {
                // Remote connections configured — require auth to prevent unauthorized access
                logger.LogError("Remote connections are configured but DRONE_MCP_TOKEN is not set. " +
                    "MCP WebSocket will require authentication. Set DRONE_MCP_TOKEN environment variable.");
                logger.LogWarning("Generating temporary MCP token for this session — check DRONE_MCP_TOKEN env var on stdout.");
                var tempToken = Convert.ToHexString(global::System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                mcpServer.SetAuthToken(tempToken);
                Console.Error.WriteLine($"DRONE_TEMP_MCP_TOKEN={tempToken}");
            }
            else
            {
                logger.LogWarning("MCP WebSocket has NO authentication — only safe if bound to localhost");
            }

            // Wire up extended health checks (custody chain, connection status)
            mcpServer.SetHealthProvider(() => new HealthStatus
            {
                CustodySequence = custodyLogger?.CurrentSequence,
                CustodyHash = custodyLogger?.CurrentHash,
                MessengerConnected = messenger?.IsConnected,
                UplinkConnected = uplink?.IsConnected,
                RemoteConnected = remote?.IsConnected
            });

            if (uplink != null)
            {
                uplink.OnRequest += async request =>
                {
                    var response = await mcpServer.HandleRequestAsync(request, "uplink");
                    await uplink.SendResponseAsync(global::System.Text.Json.JsonSerializer.Serialize(response));
                };
            }

            // Use the master cancellation token for all sub-tasks
            Drone.Services.Share.EmbeddedFileServer? fileServerLocal = null;
            if (droneConfig.Share.Enabled)
            {
                try
                {
                    var storagePath = Environment.GetEnvironmentVariable("DRONE_SHARE_PATH")
                        ?? Path.Combine(AppContext.BaseDirectory, "share");
                    var serverUrl = droneConfig.Share.ServerUrl ?? "http://+:5003";
                    var uri = new global::System.Uri(serverUrl);
                    var shareListenUrl = $"http://+:{uri.Port}/";
                    if (!shareListenUrl.EndsWith("/")) shareListenUrl += "/";
                    fileServerLocal = new Drone.Services.Share.EmbeddedFileServer(logger, storagePath, shareListenUrl, droneConfig.Share.AdminApiKey ?? "");
                    fileServer = fileServerLocal;
                    await fileServerLocal.StartAsync(masterCts!.Token);
                    logger.LogInformation("File server at {Url} (storage: {Path})", shareListenUrl, storagePath);
                    trayApp.ShowNotification("File Server", $"Listening on port {uri.Port}", ToolTipIcon.Info);
                }
                catch (Exception ex) { logger.LogWarning("File server failed: {Error}", ex.Message); }
            }

            var mcpTasks = new List<Task>();
            logger.LogInformation("MCP NMCP at {Path}", droneConfig.Mcp.BufferPath);
            mcpTasks.Add(Task.Run(() => mcpServer.RunAsync(droneConfig.Mcp.BufferPath, droneConfig.Mcp.BufferSize, masterCts!.Token)));

            var mcpWsUrl = envMcpUrl ?? "http://+:9100";
            logger.LogInformation("MCP WebSocket at {Url}", mcpWsUrl);
            mcpTasks.Add(Task.Run(() => mcpServer.RunWebSocketAsync(mcpWsUrl, masterCts.Token)));

            // --- Start CustodyReporter (streams custody records to CustodyServer) ---
            var custodyServerUrl = Environment.GetEnvironmentVariable("DRONE_CUSTODY_SERVER");
            if (!string.IsNullOrEmpty(custodyServerUrl))
            {
                custodyLogger.LogConnection("custody_reporter_init", "custody_server", custodyServerUrl);
                custodyReporter = new CustodyReporter(custodyLogger, logger);
                // The send function will be wired when connected to the custody server
                // For now, start the reporter — it will queue records until connected
                await custodyReporter.StartAsync(masterCts!.Token);
                logger.LogInformation("CustodyReporter started (server: {Url})", custodyServerUrl);
            }

            if (uplink != null)
            {
                try { await uplink.ConnectAsync(masterCts!.Token); logger.LogInformation("Uplink connected"); }
                catch (Exception ex) { logger.LogWarning("Uplink failed: {Error}", ex.Message); }
            }

            logger.LogInformation("Registered {Count} MCP tools. Drone ready.", mcpServer.GetToolList().Length);
            trayApp.SetStatus("Ready", messenger?.IsConnected ?? false);
            custodyLogger?.LogConnection("agent_ready", "agent", $"tools={mcpServer.GetToolList().Length}");

            // Wait for shutdown signal or MCP task completion
            var shutdownDelay = Task.Delay(Timeout.Infinite, masterCts!.Token);
            await Task.WhenAny(shutdownDelay, Task.WhenAll(mcpTasks));

            logger.LogInformation("Shutdown initiated, disposing resources...");
            trayApp.SetStatus("Shutting down...", false);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Shutdown via cancellation — normal exit.");
        }
        catch (Exception ex)
        {
            logger.LogError("Fatal: {Error}", ex);
            trayApp.SetStatus("Fatal Error", false);
        }
        finally
        {
            // --- Graceful shutdown: dispose all resources in reverse creation order ---
            var shutdownTimeoutSec = int.TryParse(Environment.GetEnvironmentVariable("DRONE_SHUTDOWN_TIMEOUT"), out var t) ? t : 15;
            logger.LogInformation("Shutdown timeout: {Timeout}s", shutdownTimeoutSec);

            try
            {
                // Wrap entire disposal in a timeout task to enforce the shutdown deadline
                var disposalTask = Task.Run(async () =>
                {
                    // Stop relay server
                    if (relayServer != null)
                    {
                        logger.LogInformation("Disposing relay server ({Connections} connections)...", relayServer.ConnectionCount);
                        await relayServer.DisposeAsync();
                    }

                    // Stop MCP server first (rejects new connections)
                    if (mcpServer != null)
                    {
                        logger.LogInformation("Disposing MCP server ({Clients} clients connected)...", mcpServer.ConnectedClientCount);
                        await mcpServer.DisposeAsync();
                    }

                    // Stop file server
                    if (fileServer != null)
                        await fileServer.DisposeAsync();

                    // Stop custody reporter (flush pending records)
                    if (custodyReporter != null)
                        await custodyReporter.DisposeAsync();

                    // Stop autonomy engine (cancel timers)
                    if (autonomy != null)
                        await autonomy.DisposeAsync();

                    // Disconnect uplink
                    if (uplink != null)
                        await uplink.DisposeAsync();

                    // Disconnect remote
                    if (remote != null)
                        await remote.DisposeAsync();

                    // Disconnect share
                    if (share != null)
                        await share.DisposeAsync();

                    // Disconnect messenger
                    if (messenger != null)
                        await messenger.DisposeAsync();

                    // Flush custody trail (writes pending batch with Merkle root)
                    if (custodyLogger != null) await custodyLogger.DisposeAsync();

                    logger.LogInformation("All resources disposed.");
                });

                // Enforce the shutdown timeout — if disposal takes too long, log and move on
                if (await Task.WhenAny(disposalTask, Task.Delay(TimeSpan.FromSeconds(shutdownTimeoutSec))) != disposalTask)
                {
                    logger.LogWarning("Shutdown timeout ({Timeout}s) exceeded. Some resources may not have been disposed cleanly.", shutdownTimeoutSec);
                }

                logger.LogInformation("Shutdown complete.");
            }
            catch (Exception ex)
            {
                logger.LogError("Error during shutdown disposal: {Error}", ex.Message);
            }
            finally
            {
                masterCts?.Dispose();
            }
        }
    }
}

public class DroneLogger : Drone.Core.ILogger
{
    private readonly Microsoft.Extensions.Logging.ILogger _inner;
    public DroneLogger(Microsoft.Extensions.Logging.ILogger inner) => _inner = inner;
    public void LogInformation(string message, params object[] args) => _inner.LogInformation("[{Timestamp:HH:mm:ss}] " + message, PrependTimestamp(args));
    public void LogWarning(string message, params object[] args) => _inner.LogWarning("[{Timestamp:HH:mm:ss}] " + message, PrependTimestamp(args));
    public void LogError(string message, params object[] args) => _inner.LogError("[{Timestamp:HH:mm:ss}] " + message, PrependTimestamp(args));
    public void LogDebug(string message, params object[] args) => _inner.LogDebug("[{Timestamp:HH:mm:ss}] " + message, PrependTimestamp(args));
    private static object[] PrependTimestamp(object[] args) => args;
}
