using global::System.Diagnostics;
using global::System.Text.Json;
using Drone.Core;
using Drone.Services.Messenger;
using Drone.Services.Share;
using Drone.Services.Remote;

namespace Drone.MCP.Tools;

public static class SystemToolRegistrar
{
    // Allowed base directories for file operations (configurable via env var)
    private static readonly string[] AllowedPaths = GetAllowedPaths();

    /// <summary>Cached empty JSON object to avoid repeated allocation.</summary>
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement;

    private static string[] GetAllowedPaths()
    {
        var envPaths = Environment.GetEnvironmentVariable("DRONE_ALLOWED_PATHS");
        if (!string.IsNullOrEmpty(envPaths))
            return envPaths.Split(';', StringSplitOptions.RemoveEmptyEntries);
        return Array.Empty<string>();
    }

    private static bool IsPathAllowed(string path)
    {
        if (AllowedPaths.Length == 0) return true;
        var fullPath = Path.GetFullPath(path);
        return AllowedPaths.Any(allowed =>
        {
            var fullAllowed = Path.GetFullPath(allowed);
            // Exact match or subdirectory (with separator boundary to prevent prefix attacks)
            // e.g. "/data" must NOT match "/data-secret/file.txt"
            return fullPath.Equals(fullAllowed, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(fullAllowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(fullAllowed + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>Drone start time for uptime calculation.</summary>
    private static readonly long StartTimeMs = Environment.TickCount64;

    /// <summary>Timeout for run_command tool (60 seconds).</summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Blocked command substrings for security.</summary>
    private static readonly string[] BlockedCommands = { "format", "del /s", "rm -rf /", "mkfs", "dd if=/dev/zero" };

    public static void RegisterAll(
        McpServer server,
        Drone.System.IScreenCapture? screen,
        Drone.System.IInputSimulator? input,
        Drone.System.IProcessManager process,
        Drone.System.IClipboardManager clipboard,
        Drone.System.IWindowManager? windows,
        MessengerConnector? messenger,
        ShareConnector? share,
        RemoteConnector? remote,
        ILogger logger)
    {
        // Screen tools
        if (screen != null)
        {
            server.RegisterTool("capture_screen", async args =>
            {
                var data = await screen.CaptureScreenAsync();
                return JsonSerializer.SerializeToElement(new { image = Convert.ToBase64String(data), format = "png" });
            });
            server.RegisterTool("capture_window", async args =>
            {
                var title = args.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                if (windows != null && !string.IsNullOrEmpty(title))
                {
                    var wins = await windows.ListWindowsAsync();
                    var win = wins.FirstOrDefault(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
                    if (win != null)
                    {
                        var data = await screen.CaptureWindowAsync(win.Handle);
                        return JsonSerializer.SerializeToElement(new { image = Convert.ToBase64String(data), format = "png", title = win.Title });
                    }
                }
                var fallback = await screen.CaptureScreenAsync();
                return JsonSerializer.SerializeToElement(new { image = Convert.ToBase64String(fallback), format = "png" });
            });
            server.RegisterTool("get_pixel_color", async args =>
            {
                var x = args.GetProperty("x").GetInt32();
                var y = args.GetProperty("y").GetInt32();
                var (r, g, b) = await screen.GetPixelColorAsync(x, y);
                return JsonSerializer.SerializeToElement(new { r, g, b });
            });
            server.RegisterTool("find_image_on_screen", async args =>
            {
                return JsonSerializer.SerializeToElement(new
                {
                    found = false, x = 0, y = 0, confidence = 0.0,
                    message = "Template matching requires native backend. Use capture_screen + AI vision instead."
                });
            });
            server.RegisterTool("get_screen_size", async _ =>
            {
                var (w, h) = await screen.GetScreenSizeAsync();
                return JsonSerializer.SerializeToElement(new { width = w, height = h });
            });
        }

        // Input tools
        if (input != null)
        {
            server.RegisterTool("type_text", async args =>
            {
                await input.TypeTextAsync(args.GetProperty("text").GetString() ?? "");
                return JsonSerializer.SerializeToElement(new { success = true });
            });
            server.RegisterTool("press_key", async args =>
            {
                if (Enum.TryParse<Drone.System.VirtualKey>(args.GetProperty("key").GetString(), true, out var key))
                {
                    await input.PressKeyAsync(key);
                    return JsonSerializer.SerializeToElement(new { success = true });
                }
                return JsonSerializer.SerializeToElement(new { success = false, error = "Unknown key" });
            });
            server.RegisterTool("move_mouse", async args =>
            {
                await input.MoveMouseAsync(args.GetProperty("x").GetInt32(), args.GetProperty("y").GetInt32());
                return JsonSerializer.SerializeToElement(new { success = true });
            });
            server.RegisterTool("click", async args =>
            {
                var btn = args.TryGetProperty("button", out var b) && b.GetString() == "right"
                    ? Drone.System.MouseButton.Right : Drone.System.MouseButton.Left;
                await input.ClickAsync(args.GetProperty("x").GetInt32(), args.GetProperty("y").GetInt32(), btn);
                return JsonSerializer.SerializeToElement(new { success = true });
            });
            server.RegisterTool("drag", async args =>
            {
                await input.DragAsync(
                    args.GetProperty("fromX").GetInt32(), args.GetProperty("fromY").GetInt32(),
                    args.GetProperty("toX").GetInt32(), args.GetProperty("toY").GetInt32());
                return JsonSerializer.SerializeToElement(new { success = true });
            });
            server.RegisterTool("scroll", async args =>
            {
                var deltaX = args.TryGetProperty("deltaX", out var dx) ? dx.GetInt32() : 0;
                var deltaY = args.TryGetProperty("deltaY", out var dy) ? dy.GetInt32() : 0;
                await input.ScrollAsync(deltaX, deltaY);
                return JsonSerializer.SerializeToElement(new { success = true });
            });
        }

        // System tools (always available)
        server.RegisterTool("run_command", async args =>
        {
            var command = args.GetProperty("command").GetString() ?? "";
            if (BlockedCommands.Any(b => command.Contains(b, StringComparison.OrdinalIgnoreCase)))
                return JsonSerializer.SerializeToElement(new { error = "Command blocked by security policy" });

            var cmdArgs = args.TryGetProperty("args", out var a) ? a.GetString() ?? "" : "";
            var workingDir = args.TryGetProperty("workingDir", out var w) ? w.GetString() : null;

            // Apply timeout to prevent hanging commands
            using var timeoutCts = new CancellationTokenSource(CommandTimeout);
            try
            {
                var r = await process.RunCommandAsync(command, cmdArgs, workingDir).WaitAsync(timeoutCts.Token);
                return JsonSerializer.SerializeToElement(new
                {
                    exitCode = r.ExitCode,
                    stdout = r.StandardOutput.Length > 100000 ? r.StandardOutput[..100000] + "..." : r.StandardOutput,
                    stderr = r.StandardError,
                    durationMs = r.Duration.TotalMilliseconds
                });
            }
            catch (OperationCanceledException)
            {
                return JsonSerializer.SerializeToElement(new
                {
                    error = $"Command timed out after {CommandTimeout.TotalSeconds}s",
                    command, exitCode = -1
                });
            }
        });
        server.RegisterTool("list_processes", async _ =>
        {
            var procs = await process.ListProcessesAsync();
            return JsonSerializer.SerializeToElement(new
            {
                count = procs.Length,
                top50 = procs.OrderByDescending(p => p.WorkingSet64).Take(50)
                    .Select(p => new { pid = p.Id, name = p.Name, memoryMB = p.WorkingSet64 / 1024 / 1024, threads = p.ThreadCount, status = p.Status })
            });
        });
        server.RegisterTool("kill_process", async args =>
        {
            var pid = args.GetProperty("processId").GetInt32();
            var ok = await process.KillProcessAsync(pid);
            return JsonSerializer.SerializeToElement(new { success = ok, processId = pid });
        });
        server.RegisterTool("read_file", async args =>
        {
            var path = args.GetProperty("path").GetString() ?? "";
            if (!IsPathAllowed(path))
                return JsonSerializer.SerializeToElement(new { error = "Path not in allowed directories" });
            if (!File.Exists(path))
                return JsonSerializer.SerializeToElement(new { error = "File not found" });
            var content = await File.ReadAllTextAsync(path);
            return JsonSerializer.SerializeToElement(new { content, path = Path.GetFullPath(path) });
        });
        server.RegisterTool("write_file", async args =>
        {
            var path = args.GetProperty("path").GetString() ?? "";
            if (!IsPathAllowed(path))
                return JsonSerializer.SerializeToElement(new { error = "Path not in allowed directories" });
            var content = args.GetProperty("content").GetString() ?? "";
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(path, content);
            return JsonSerializer.SerializeToElement(new { success = true, path = Path.GetFullPath(path) });
        });
        server.RegisterTool("list_dir", async args =>
        {
            var path = args.TryGetProperty("path", out var p) ? p.GetString() ?? "." : ".";
            if (!IsPathAllowed(path))
                return JsonSerializer.SerializeToElement(new { error = "Path not in allowed directories" });
            if (!Directory.Exists(path))
                return JsonSerializer.SerializeToElement(new { error = "Directory not found" });
            var entries = Directory.GetFileSystemEntries(path)
                .Select(e => new { name = Path.GetFileName(e), isDir = Directory.Exists(e) })
                .OrderBy(e => e.name).ToArray();
            return JsonSerializer.SerializeToElement(new { path = Path.GetFullPath(path), count = entries.Length, entries });
        });
        server.RegisterTool("find_file", async args =>
        {
            var path = args.TryGetProperty("path", out var p) ? p.GetString() ?? "." : ".";
            var pattern = args.TryGetProperty("pattern", out var pat) ? pat.GetString() ?? "*" : "*";
            if (!IsPathAllowed(path))
                return JsonSerializer.SerializeToElement(new { error = "Path not in allowed directories" });
            if (!Directory.Exists(path))
                return JsonSerializer.SerializeToElement(new { error = "Directory not found" });
            // Limit recursion depth to 5 levels to prevent CPU/memory exhaustion on large trees
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 5 };
            var files = Directory.GetFiles(path, pattern, options)
                .Take(100) // Limit result count
                .Select(f => new { path = f, size = new FileInfo(f).Length })
                .ToArray();
            return JsonSerializer.SerializeToElement(new { count = files.Length, files });
        });
        server.RegisterTool("get_system_info", async _ =>
        {
            var info = await process.GetSystemInfoAsync();
            return JsonSerializer.SerializeToElement(info);
        });
        server.RegisterTool("clipboard_get", async _ =>
        {
            var text = await clipboard.GetTextAsync();
            return JsonSerializer.SerializeToElement(new { text = text ?? "", length = text?.Length ?? 0 });
        });
        server.RegisterTool("clipboard_set", async args =>
        {
            await clipboard.SetTextAsync(args.GetProperty("text").GetString() ?? "");
            return JsonSerializer.SerializeToElement(new { success = true });
        });

        // Comprehensive drone status — ALWAYS registered regardless of connector availability
        server.RegisterTool("get_drone_status", async _ =>
        {
            var uptimeSec = (Environment.TickCount64 - StartTimeMs) / 1000;
            // Use 'using' to properly dispose the Process object (it holds native handles)
            using var proc = Process.GetCurrentProcess();
            return JsonSerializer.SerializeToElement(new
            {
                agent = "velocity-drone",
                version = "1.0.0",
                uptimeSec,
                memoryMB = proc.WorkingSet64 / 1024 / 1024,
                threads = proc.Threads.Count,
                platform = global::System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                architecture = global::System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                mode = Environment.GetEnvironmentVariable("DRONE_MODE") ?? "full",
                connections = new
                {
                    messenger = messenger?.IsConnected ?? false,
                    share = share?.IsConnected ?? false,
                    remote = remote?.IsConnected ?? false,
                    mcpWebSocketClients = server.ConnectedClientCount
                },
                capabilities = new
                {
                    screenCapture = screen != null,
                    inputSimulation = input != null,
                    windowManagement = windows != null,
                    processManagement = true,
                    clipboard = true,
                    fileOperations = true,
                    commandExecution = true
                },
                metrics = new
                {
                    totalRequests = server.TotalRequests,
                    totalErrors = server.TotalErrors
                }
            });
        });

        // Legacy alias
        server.RegisterTool("get_status", async _ =>
        {
            var uptimeSec = (Environment.TickCount64 - StartTimeMs) / 1000;
            return JsonSerializer.SerializeToElement(new
            {
                messenger = messenger?.IsConnected ?? false,
                share = share?.IsConnected ?? false,
                remote = remote?.IsConnected ?? false,
                mcpClients = server.ConnectedClientCount,
                uptimeSec
            });
        });

        // Messenger tools
        if (messenger != null)
        {
            server.RegisterTool("send_message", async args =>
            {
                await messenger.SendMessageAsync(
                    args.GetProperty("to").GetString() ?? "",
                    args.GetProperty("content").GetString() ?? "");
                return JsonSerializer.SerializeToElement(new { success = true });
            });
            server.RegisterTool("send_group_message", async args =>
            {
                await messenger.SendGroupMessageAsync(
                    args.GetProperty("groupId").GetString() ?? "",
                    args.GetProperty("content").GetString() ?? "");
                return JsonSerializer.SerializeToElement(new { success = true });
            });
            server.RegisterTool("get_contacts", async _ =>
            {
                await messenger.SendControlMessageAsync("get_contacts", "{}");
                return JsonSerializer.SerializeToElement(new { status = "requested", message = "Contacts will be delivered via event bus" });
            });
            server.RegisterTool("upload_media", async args =>
            {
                var filePath = args.GetProperty("filePath").GetString() ?? "";
                if (!IsPathAllowed(filePath))
                    return JsonSerializer.SerializeToElement(new { error = "Path not in allowed directories" });
                var mediaType = args.TryGetProperty("mediaType", out var mt) ? mt.GetString() ?? "file" : "file";
                var fileName = await messenger.UploadMediaAsync(filePath, mediaType);
                return JsonSerializer.SerializeToElement(new { success = true, fileName });
            });
            server.RegisterTool("download_media", async args =>
            {
                var localPath = args.GetProperty("localPath").GetString() ?? "";
                if (!IsPathAllowed(localPath))
                    return JsonSerializer.SerializeToElement(new { error = "Path not in allowed directories" });
                await messenger.DownloadMediaAsync(
                    args.GetProperty("url").GetString() ?? "",
                    localPath);
                return JsonSerializer.SerializeToElement(new { success = true, localPath });
            });
        }

        // Share tools
        if (share != null)
        {
            server.RegisterTool("upload_file", async args =>
            {
                var ok = await share.UploadFileAsync(
                    args.GetProperty("localPath").GetString() ?? "",
                    args.GetProperty("remotePath").GetString() ?? "");
                return JsonSerializer.SerializeToElement(new { success = ok });
            });
            server.RegisterTool("download_file", async args =>
            {
                var ok = await share.DownloadFileAsync(
                    args.GetProperty("remotePath").GetString() ?? "",
                    args.GetProperty("localPath").GetString() ?? "");
                return JsonSerializer.SerializeToElement(new { success = ok });
            });
            server.RegisterTool("list_files", async args =>
            {
                var files = await share.ListFilesAsync(
                    args.TryGetProperty("path", out var p) ? p.GetString() : null);
                return JsonSerializer.SerializeToElement(new { count = files.Length, files });
            });
            server.RegisterTool("sync_folder", async args =>
            {
                var n = await share.SyncFolderAsync(
                    args.GetProperty("localFolder").GetString() ?? "",
                    args.GetProperty("remoteFolder").GetString() ?? "");
                return JsonSerializer.SerializeToElement(new { success = true, uploaded = n });
            });
            server.RegisterTool("delete_file", async args =>
            {
                var path = args.GetProperty("path").GetString() ?? "";
                if (!IsPathAllowed(path))
                    return JsonSerializer.SerializeToElement(new { error = "Path not in allowed directories" });
                var ok = await share.DeleteFileAsync(path);
                return JsonSerializer.SerializeToElement(new { success = ok });
            });
        }

        // Remote tools
        if (remote != null)
        {
            server.RegisterTool("get_screen_stream", async args =>
            {
                var quality = args.TryGetProperty("quality", out var q) ? q.GetInt32() : 80;
                var maxWidth = args.TryGetProperty("maxWidth", out var mw) ? mw.GetInt32() : 1920;
                await remote.RequestScreenAsync(quality, maxWidth);
                return JsonSerializer.SerializeToElement(new { success = true, message = "Screen stream requested" });
            });
            server.RegisterTool("send_input", async args =>
            {
                var inputType = args.GetProperty("inputType").GetString() ?? "";
                var data = args.TryGetProperty("data", out var d) ? d : EmptyObject;
                await remote.SendInputAsync(inputType, data);
                return JsonSerializer.SerializeToElement(new { success = true });
            });
            server.RegisterTool("get_hosts", async _ =>
            {
                await remote.QueryHostsAsync();
                return JsonSerializer.SerializeToElement(new { status = "requested", message = "Host list will be delivered via event bus" });
            });
            server.RegisterTool("get_address_book", async _ =>
            {
                await remote.QueryAddressBookAsync();
                return JsonSerializer.SerializeToElement(new { status = "requested", message = "Address book will be delivered via event bus" });
            });
        }

        // Window management tools
        if (windows != null)
        {
            server.RegisterTool("list_windows", async _ =>
            {
                var wins = await windows.ListWindowsAsync();
                return JsonSerializer.SerializeToElement(new
                {
                    count = wins.Length,
                    windows = wins.Select(w => new { w.Title, w.ProcessName, w.IsVisible, w.IsMinimized })
                });
            });
            server.RegisterTool("focus_window", async args =>
            {
                var title = args.GetProperty("title").GetString() ?? "";
                var wins = await windows.ListWindowsAsync();
                var t = wins.FirstOrDefault(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
                if (t != null)
                {
                    await windows.FocusWindowAsync(t.Handle);
                    return JsonSerializer.SerializeToElement(new { success = true, title = t.Title });
                }
                return JsonSerializer.SerializeToElement(new { success = false, error = "Window not found" });
            });
            server.RegisterTool("close_app", async args =>
            {
                var title = args.GetProperty("title").GetString() ?? "";
                var wins = await windows.ListWindowsAsync();
                var t = wins.FirstOrDefault(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
                if (t != null)
                {
                    await windows.CloseWindowAsync(t.Handle);
                    return JsonSerializer.SerializeToElement(new { success = true });
                }
                return JsonSerializer.SerializeToElement(new { success = false, error = "Window not found" });
            });
            server.RegisterTool("get_app_state", async args =>
            {
                var title = args.GetProperty("title").GetString() ?? "";
                var wins = await windows.ListWindowsAsync();
                var t = wins.FirstOrDefault(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
                if (t != null)
                    return JsonSerializer.SerializeToElement(new { found = true, t.Title, t.ProcessName, t.IsVisible, t.IsMinimized });
                return JsonSerializer.SerializeToElement(new { found = false });
            });
        }

        server.RegisterTool("launch_app", async args =>
        {
            var app = args.GetProperty("app").GetString() ?? "";
            // Security: block launching dangerous applications
            if (BlockedCommands.Any(b => app.Contains(b, StringComparison.OrdinalIgnoreCase)))
                return JsonSerializer.SerializeToElement(new { error = "Application blocked by security policy" });
            var r = await process.RunCommandAsync(app, args.TryGetProperty("args", out var a) ? a.GetString() ?? "" : "");
            return JsonSerializer.SerializeToElement(new { launched = r.ExitCode == 0, app });
        });

        logger.LogInformation("All MCP tools registered ({Count} tools)", server.GetToolList().Length);
    }
}
