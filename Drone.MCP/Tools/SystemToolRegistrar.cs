using global::System.Text.Json;
using Drone.Core;
using Drone.Services.Messenger;
using Drone.Services.Share;

namespace Drone.MCP.Tools;

public static class SystemToolRegistrar
{
    public static void RegisterAll(McpServer server, Drone.System.IScreenCapture? screen, Drone.System.IInputSimulator? input, Drone.System.IProcessManager process, Drone.System.IClipboardManager clipboard, Drone.System.IWindowManager? windows, MessengerConnector? messenger, ShareConnector? share, ILogger logger)
    {
        if (screen != null)
        {
            server.RegisterTool("capture_screen", async args => { var data = await screen.CaptureScreenAsync(); return JsonSerializer.SerializeToElement(new { image = Convert.ToBase64String(data), format = "png" }); });
            server.RegisterTool("capture_window", async args => { var data = await screen.CaptureScreenAsync(); return JsonSerializer.SerializeToElement(new { image = Convert.ToBase64String(data), format = "png" }); });
            server.RegisterTool("get_pixel_color", async args => { var x = args.GetProperty("x").GetInt32(); var y = args.GetProperty("y").GetInt32(); var (r, g, b) = await screen.GetPixelColorAsync(x, y); return JsonSerializer.SerializeToElement(new { r, g, b }); });
        }
        if (input != null)
        {
            server.RegisterTool("type_text", async args => { await input.TypeTextAsync(args.GetProperty("text").GetString() ?? ""); return JsonSerializer.SerializeToElement(new { success = true }); });
            server.RegisterTool("press_key", async args => { if (Enum.TryParse<Drone.System.VirtualKey>(args.GetProperty("key").GetString(), true, out var key)) { await input.PressKeyAsync(key); return JsonSerializer.SerializeToElement(new { success = true }); } return JsonSerializer.SerializeToElement(new { success = false }); });
            server.RegisterTool("move_mouse", async args => { await input.MoveMouseAsync(args.GetProperty("x").GetInt32(), args.GetProperty("y").GetInt32()); return JsonSerializer.SerializeToElement(new { success = true }); });
            server.RegisterTool("click", async args => { var btn = args.TryGetProperty("button", out var b) && b.GetString() == "right" ? Drone.System.MouseButton.Right : Drone.System.MouseButton.Left; await input.ClickAsync(args.GetProperty("x").GetInt32(), args.GetProperty("y").GetInt32(), btn); return JsonSerializer.SerializeToElement(new { success = true }); });
            server.RegisterTool("drag", async args => { await input.DragAsync(args.GetProperty("fromX").GetInt32(), args.GetProperty("fromY").GetInt32(), args.GetProperty("toX").GetInt32(), args.GetProperty("toY").GetInt32()); return JsonSerializer.SerializeToElement(new { success = true }); });
            server.RegisterTool("scroll", async args => { await input.ScrollAsync(args.GetProperty("deltaX").GetInt32(), args.GetProperty("deltaY").GetInt32()); return JsonSerializer.SerializeToElement(new { success = true }); });
        }
        server.RegisterTool("run_command", async args => { var r = await process.RunCommandAsync(args.GetProperty("command").GetString() ?? "", args.TryGetProperty("args", out var a) ? a.GetString() ?? "" : "", args.TryGetProperty("workingDir", out var w) ? w.GetString() : null); return JsonSerializer.SerializeToElement(new { exitCode = r.ExitCode, stdout = r.StandardOutput.Length > 100000 ? r.StandardOutput[..100000] + "..." : r.StandardOutput, stderr = r.StandardError, durationMs = r.Duration.TotalMilliseconds }); });
        server.RegisterTool("list_processes", async _ => { var procs = await process.ListProcessesAsync(); return JsonSerializer.SerializeToElement(new { count = procs.Length, top50 = procs.OrderByDescending(p => p.WorkingSet64).Take(50).Select(p => new { pid = p.Id, name = p.Name, memoryMB = p.WorkingSet64 / 1024 / 1024, threads = p.ThreadCount, status = p.Status }) }); });
        server.RegisterTool("kill_process", async args => { var ok = await process.KillProcessAsync(args.GetProperty("processId").GetInt32()); return JsonSerializer.SerializeToElement(new { success = ok }); });
        server.RegisterTool("read_file", async args => { var path = args.GetProperty("path").GetString() ?? ""; if (!File.Exists(path)) return JsonSerializer.SerializeToElement(new { error = "File not found" }); return JsonSerializer.SerializeToElement(new { content = await File.ReadAllTextAsync(path), path }); });
        server.RegisterTool("write_file", async args => { var path = args.GetProperty("path").GetString() ?? ""; var content = args.GetProperty("content").GetString() ?? ""; var dir = Path.GetDirectoryName(path); if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir); await File.WriteAllTextAsync(path, content); return JsonSerializer.SerializeToElement(new { success = true, path }); });
        server.RegisterTool("list_dir", async args => { var path = args.TryGetProperty("path", out var p) ? p.GetString() ?? "." : "."; if (!Directory.Exists(path)) return JsonSerializer.SerializeToElement(new { error = "Directory not found" }); var entries = Directory.GetFileSystemEntries(path).Select(e => new { name = Path.GetFileName(e), isDir = Directory.Exists(e) }).OrderBy(e => e.name).ToArray(); return JsonSerializer.SerializeToElement(new { path = Path.GetFullPath(path), count = entries.Length, entries }); });
        server.RegisterTool("get_system_info", async _ => { var info = await process.GetSystemInfoAsync(); return JsonSerializer.SerializeToElement(info); });
        server.RegisterTool("clipboard_get", async _ => { var text = await clipboard.GetTextAsync(); return JsonSerializer.SerializeToElement(new { text = text ?? "", length = text?.Length ?? 0 }); });
        server.RegisterTool("clipboard_set", async args => { await clipboard.SetTextAsync(args.GetProperty("text").GetString() ?? ""); return JsonSerializer.SerializeToElement(new { success = true }); });
        if (messenger != null)
        {
            server.RegisterTool("send_message", async args => { await messenger.SendMessageAsync(args.GetProperty("to").GetString() ?? "", args.GetProperty("content").GetString() ?? ""); return JsonSerializer.SerializeToElement(new { success = true }); });
            server.RegisterTool("send_group_message", async args => { await messenger.SendGroupMessageAsync(args.GetProperty("groupId").GetString() ?? "", args.GetProperty("content").GetString() ?? ""); return JsonSerializer.SerializeToElement(new { success = true }); });
            server.RegisterTool("get_status", async _ => { return JsonSerializer.SerializeToElement(new { messenger = messenger.IsConnected, share = share?.IsConnected ?? false, uptimeSec = Environment.TickCount64 / 1000 }); });
        }
        if (share != null)
        {
            server.RegisterTool("upload_file", async args => { var ok = await share.UploadFileAsync(args.GetProperty("localPath").GetString() ?? "", args.GetProperty("remotePath").GetString() ?? ""); return JsonSerializer.SerializeToElement(new { success = ok }); });
            server.RegisterTool("download_file", async args => { var ok = await share.DownloadFileAsync(args.GetProperty("remotePath").GetString() ?? "", args.GetProperty("localPath").GetString() ?? ""); return JsonSerializer.SerializeToElement(new { success = ok }); });
            server.RegisterTool("list_files", async args => { var files = await share.ListFilesAsync(args.TryGetProperty("path", out var p) ? p.GetString() : null); return JsonSerializer.SerializeToElement(new { count = files.Length, files }); });
            server.RegisterTool("sync_folder", async args => { var n = await share.SyncFolderAsync(args.GetProperty("localFolder").GetString() ?? "", args.GetProperty("remoteFolder").GetString() ?? ""); return JsonSerializer.SerializeToElement(new { success = true, uploaded = n }); });
            server.RegisterTool("delete_file", async args => { var ok = await share.DeleteFileAsync(args.GetProperty("path").GetString() ?? ""); return JsonSerializer.SerializeToElement(new { success = ok }); });
        }
        if (windows != null)
        {
            server.RegisterTool("list_windows", async _ => { var wins = await windows.ListWindowsAsync(); return JsonSerializer.SerializeToElement(new { count = wins.Length, windows = wins.Select(w => new { w.Title, w.ProcessName, w.IsVisible, w.IsMinimized }) }); });
            server.RegisterTool("focus_window", async args => { var title = args.GetProperty("title").GetString() ?? ""; var wins = await windows.ListWindowsAsync(); var t = wins.FirstOrDefault(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase)); if (t != null) { await windows.FocusWindowAsync(t.Handle); return JsonSerializer.SerializeToElement(new { success = true, title = t.Title }); } return JsonSerializer.SerializeToElement(new { success = false }); });
            server.RegisterTool("close_app", async args => { var title = args.GetProperty("title").GetString() ?? ""; var wins = await windows.ListWindowsAsync(); var t = wins.FirstOrDefault(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase)); if (t != null) { await windows.CloseWindowAsync(t.Handle); return JsonSerializer.SerializeToElement(new { success = true }); } return JsonSerializer.SerializeToElement(new { success = false }); });
            server.RegisterTool("get_app_state", async args => { var title = args.GetProperty("title").GetString() ?? ""; var wins = await windows.ListWindowsAsync(); var t = wins.FirstOrDefault(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase)); if (t != null) return JsonSerializer.SerializeToElement(new { found = true, t.Title, t.ProcessName, t.IsVisible, t.IsMinimized }); return JsonSerializer.SerializeToElement(new { found = false }); });
        }
        server.RegisterTool("launch_app", async args => { var app = args.GetProperty("app").GetString() ?? ""; var r = await process.RunCommandAsync(app, args.TryGetProperty("args", out var a) ? a.GetString() ?? "" : ""); return JsonSerializer.SerializeToElement(new { launched = r.ExitCode == 0, app }); });
        logger.LogInformation("All MCP tools registered (" + server.GetToolList().Length + " tools)");
    }
}
