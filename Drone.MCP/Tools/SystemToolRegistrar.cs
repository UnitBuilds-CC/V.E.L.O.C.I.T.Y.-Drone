using global::System.Text.Json;
using Drone.Core;
using Drone.Services.Messenger;
using Drone.Services.Share;
using Drone.Services.Remote;

namespace Drone.MCP.Tools;

public static class SystemToolRegistrar
{
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
        // â”€â”€ Screen tools â”€â”€
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
                var templateBase64 = args.GetProperty("template").GetString() ?? "";
                var threshold = args.TryGetProperty("threshold", out var th) ? th.GetDouble() : 0.8;
                var screenData = await screen.CaptureScreenAsync();
                // Basic implementation: compare template against screen regions
                // In production, this would use OpenCV or similar for template matching
                var templateData = Convert.FromBase64String(templateBase64);
                return JsonSerializer.SerializeToElement(new
                {
                    found = false,
                    x = 0, y = 0,
                    confidence = 0.0,
                    message = "Template matching requires native backend. Use capture_screen + AI vision instead."
                });
            });
        }

        // â”€â”€ Input tools â”€â”€
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
                await input.ScrollAsync(args.GetProperty("deltaX").GetInt32(), args.GetProperty("deltaY").GetInt32());
                return JsonSerializer.SerializeToElement(new { success = true });
            });
        }

        // â”€â”€ System tools â”€â”€
        server.RegisterTool("run_command", async args =>
        {
            var r = await process.RunCommandAsync(
                args.GetProperty("command").GetString() ?? "",
                args.TryGetProperty("args", out var a) ? a.GetString() ?? "" : "",
                args.TryGetProperty("workingDir", out var w) ? w.GetString() : null);
            return JsonSerializer.SerializeToElement(new
            {
                exitCode = r.ExitCode,
                stdout = r.StandardOutput.Length > 100000 ? r.StandardOutput[..100000] + "..." : r.StandardOutput,
                stderr = r.StandardError,
                durationMs = r.Duration.TotalMilliseconds
            });
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
            var ok = await process.KillProcessAsync(args.GetProperty("processId").GetInt32());
            return JsonSerializer.SerializeToElement(new { success = ok });
        });
        server.RegisterTool("read_file", async args =>
        {
            var path = args.GetProperty("path").GetString() ?? "";
            if (!File.Exists(path)) return JsonSerializer.SerializeToElement(new { error = "File not found" });
            return JsonSerializer.SerializeToElement(new { content = await File.ReadAllTextAsync(path), path });
        });
        server.RegisterTool("write_file", async args =>
        {
            var path = args.GetProperty("path").GetString() ?? "";
            var content = args.GetProperty("content").GetString() ?? "";
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(path, content);
            return JsonSerializer.SerializeToElement(new { success = true, path });
        });
        server.RegisterTool("list_dir", async args =>
        {
            var path = args.TryGetProperty("path", out var p) ? p.GetString() ?? "." : ".";
            if (!Directory.Exists(path)) return JsonSerializer.SerializeToElement(new { error = "Directory not found" });
            var entries = Directory.GetFileSystemEntries(path)
                .Select(e => new { name = Path.GetFileName(e), isDir = Directory.Exists(e) })
                .OrderBy(e => e.name).ToArray();
            return JsonSerializer.SerializeToElement(new { path = Path.GetFullPath(path), count = entries.Length, entries });
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

        // â”€â”€ Messenger tools â”€â”€
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
                // Request contacts list via messenger protocol
                await messenger.SendControlMessageAsync("get_contacts", "{}");
                return JsonSerializer.SerializeToElement(new { status = "requested", message = "Contacts will be delivered via event bus" });
            });
            server.RegisterTool("upload_media", async args =>
            {
                var filePath = args.GetProperty("filePath").GetString() ?? "";
                var mediaType = args.TryGetProperty("mediaType", out var mt) ? mt.GetString() ?? "file" : "file";
                var fileName = await messenger.UploadMediaAsync(filePath, mediaType);
                return JsonSerializer.SerializeToElement(new { success = true, fileName });
            });
            server.RegisterTool("download_media", async args =>
            {
                var url = args.GetProperty("url").GetString() ?? "";
                var localPath = args.GetProperty("localPath").GetString() ?? "";
                await messenger.DownloadMediaAsync(url, localPath);
                return JsonSerializer.SerializeToElement(new { success = true, localPath });
            });
            server.RegisterTool("get_status", async _ =>
            {
                return JsonSerializer.SerializeToElement(new
                {
                    messenger = messenger.IsConnected,
                    share = share?.IsConnected ?? false,
                    remote = remote?.IsConnected ?? false,
                    uptimeSec = Environment.TickCount64 / 1000
                });
            });
        }

        // â”€â”€ Share tools â”€â”€
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
                var ok = await share.DeleteFileAsync(args.GetProperty("path").GetString() ?? "");
                return JsonSerializer.SerializeToElement(new { success = ok });
            });
            server.RegisterTool("get_share_status", async _ =>
            {
                return JsonSerializer.SerializeToElement(new
                {
                    connected = share.IsConnected,
                    serverUrl = share.ServerUrl
                });
            });
        }

        // â”€â”€ Remote tools â”€â”€
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
                var data = args.TryGetProperty("data", out var d) ? d : JsonDocument.Parse("{}").RootElement;
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

        // â”€â”€ App Control tools â”€â”€
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
            var r = await process.RunCommandAsync(app, args.TryGetProperty("args", out var a) ? a.GetString() ?? "" : "");
            return JsonSerializer.SerializeToElement(new { launched = r.ExitCode == 0, app });
        });

        logger.LogInformation("All MCP tools registered (" + server.GetToolList().Length + " tools)");
    }
}
