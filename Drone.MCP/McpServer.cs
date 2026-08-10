using global::System.Text;
using global::System.Text.Json;
using Drone.Core;
using Drone.Core.Protocol;

namespace Drone.MCP;

public class McpServer : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly Dictionary<string, Func<JsonElement, Task<JsonElement>>> _tools = new();
    private CancellationTokenSource? _cts;

    public McpServer(ILogger logger) => _logger = logger;

    public void RegisterTool(string name, Func<JsonElement, Task<JsonElement>> handler) => _tools[name] = handler;

    public ToolInfo[] GetToolList() => _tools.Keys.Select(name => new ToolInfo(name, GetToolDescription(name))).ToArray();

    // â”€â”€ stdio mode: JSON-RPC v2.0 over stdin/stdout â”€â”€
    public async Task RunStdioAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _logger.LogInformation("MCP stdio server starting...");
        var reader = Console.In;
        var writer = Console.Out;
        Console.Error.WriteLine("[MCP] Server started on stdio");
        while (!_cts.Token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(_cts.Token);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var response = await HandleRequestAsync(doc.RootElement);
                if (response != null)
                {
                    var json = JsonSerializer.Serialize(response);
                    await writer.WriteLineAsync(json);
                    await writer.FlushAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("MCP request error: " + ex.Message);
                var errorResponse = new { jsonrpc = "2.0", error = new { code = -32603, message = ex.Message }, id = (object?)null };
                await writer.WriteLineAsync(JsonSerializer.Serialize(errorResponse));
                await writer.FlushAsync();
            }
        }
    }

    // â”€â”€ shmem mode: NMCP binary over shared memory â”€â”€
    public async Task RunShmemAsync(string bufferPath = "nmcp_drone.shm", int bufferSize = 1048576, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _logger.LogInformation("MCP shmem server starting at " + bufferPath);

        var mmf = global::System.IO.MemoryMappedFiles.MemoryMappedFile.CreateOrOpen(
            bufferPath, bufferSize, global::System.IO.MemoryMappedFiles.MemoryMappedFileAccess.ReadWrite);
        var view = mmf.CreateViewAccessor();

        try
        {
            var readBuffer = new byte[bufferSize];
            while (!_cts.Token.IsCancellationRequested)
            {
                // Read header
                var header = new byte[NmcpFrame.HeaderSize];
                view.ReadArray(0, header, 0, header.Length);

                if (NmcpFrame.TryReadHeader(header, out var frameType, out var payloadLen, out var seqId))
                {
                    if (payloadLen > 0 && payloadLen < readBuffer.Length)
                    {
                        view.ReadArray(NmcpFrame.HeaderSize, readBuffer, 0, (int)payloadLen);
                        var json = Encoding.UTF8.GetString(readBuffer, 0, (int)payloadLen);

                        try
                        {
                            using var doc = JsonDocument.Parse(json);
                            var response = await HandleRequestAsync(doc.RootElement);
                            if (response != null)
                            {
                                var responseJson = JsonSerializer.Serialize(response);
                                var responseBytes = Encoding.UTF8.GetBytes(responseJson);
                                var responseFrame = new NmcpFrame(frameType, seqId, responseBytes);
                                var responseHeader = new byte[NmcpFrame.HeaderSize];
                                responseFrame.WriteHeader(responseHeader);
                                // Write response at offset 0 (simple single-slot protocol)
                                view.WriteArray(0, responseHeader, 0, responseHeader.Length);
                                view.WriteArray(NmcpFrame.HeaderSize, responseBytes, 0, responseBytes.Length);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError("Shmem request error: " + ex.Message);
                        }
                    }
                }

                await Task.Delay(1, _cts.Token); // 1ms poll
            }
        }
        finally
        {
            view.Dispose();
            mmf.Dispose();
        }
    }

    public async Task<object?> HandleRequestAsync(JsonElement request)
    {
        var id = request.TryGetProperty("id", out var idProp) ? idProp : default;
        var method = request.GetProperty("method").GetString() ?? "";
        switch (method)
        {
            case "initialize":
                return new
                {
                    jsonrpc = "2.0",
                    id = id.GetRawText(),
                    result = new
                    {
                        protocolVersion = "2024-11-05",
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "velocity-drone", version = "1.0.0" }
                    }
                };
            case "tools/list":
                var toolList = GetToolList().Select(t => new
                {
                    name = t.Name,
                    description = t.Description,
                    inputSchema = GetToolSchema(t.Name)
                }).ToArray();
                return new { jsonrpc = "2.0", id = id.GetRawText(), result = new { tools = toolList } };
            case "tools/call":
                var toolName = request.GetProperty("params").GetProperty("name").GetString() ?? "";
                var args = request.GetProperty("params").TryGetProperty("arguments", out var argsProp)
                    ? argsProp : JsonDocument.Parse("{}").RootElement;
                if (!_tools.TryGetValue(toolName, out var handler))
                    return new { jsonrpc = "2.0", id = id.GetRawText(), result = new { content = new[] { new { type = "text", text = "Unknown tool: " + toolName } }, isError = true } };
                try
                {
                    var result = await handler(args);
                    return new { jsonrpc = "2.0", id = id.GetRawText(), result = new { content = new[] { new { type = "text", text = result.GetRawText() } } } };
                }
                catch (Exception ex)
                {
                    return new { jsonrpc = "2.0", id = id.GetRawText(), result = new { content = new[] { new { type = "text", text = "Error: " + ex.Message } }, isError = true } };
                }
            case "notifications/initialized": return null;
            default: return new { jsonrpc = "2.0", id = id.GetRawText(), error = new { code = -32601, message = "Method not found: " + method } };
        }
    }

    private string GetToolDescription(string name) => name switch
    {
        "capture_screen" => "Capture the entire screen as a base64-encoded PNG image",
        "capture_window" => "Capture a specific window by title as base64-encoded PNG",
        "get_pixel_color" => "Get the RGB color of a pixel at the specified coordinates",
        "find_image_on_screen" => "Search for a template image on screen (template matching)",
        "type_text" => "Type text using keyboard simulation",
        "press_key" => "Press a single key (e.g., Enter, Escape, F1)",
        "move_mouse" => "Move the mouse cursor to specified coordinates",
        "click" => "Click at specified coordinates",
        "drag" => "Drag from one position to another",
        "scroll" => "Scroll at current mouse position",
        "run_command" => "Execute a shell command and return stdout/stderr",
        "list_processes" => "List all running processes",
        "kill_process" => "Terminate a process by ID",
        "read_file" => "Read file contents",
        "write_file" => "Write content to a file",
        "list_dir" => "List files and directories at a path",
        "get_system_info" => "Get system information (CPU, memory, disk, OS)",
        "clipboard_get" => "Get clipboard text content",
        "clipboard_set" => "Set clipboard text content",
        "send_message" => "Send a text message via Velocity Messenger",
        "send_group_message" => "Send a group message via Velocity Messenger",
        "get_contacts" => "Get the contacts list from Velocity Messenger",
        "upload_media" => "Upload media (image/audio/video) via Velocity Messenger",
        "download_media" => "Download media from a URL to local path",
        "get_status" => "Get Drone connection status for all services",
        "upload_file" => "Upload a file to Velocity Share",
        "download_file" => "Download a file from Velocity Share",
        "list_files" => "List files on Velocity Share",
        "sync_folder" => "Sync a local folder to Velocity Share",
        "delete_file" => "Delete a file from Velocity Share",
        "get_share_status" => "Get Velocity Share connection status",
        "get_screen_stream" => "Request a screen stream from the Remote service",
        "send_input" => "Send input events via the Remote service",
        "get_hosts" => "Query available hosts from the Remote service",
        "get_address_book" => "Query the address book from the Remote service",
        "launch_app" => "Launch an application by name or path",
        "close_app" => "Close an application by window title",
        "list_windows" => "List all open windows",
        "focus_window" => "Bring a window to the foreground",
        "get_app_state" => "Get application state",
        _ => "Drone tool"
    };

    private object GetToolSchema(string name) => name switch
    {
        "capture_screen" => new { type = "object", properties = new { format = new { type = "string", description = "Image format (png/jpg)" } } },
        "capture_window" => new { type = "object", properties = new { title = new { type = "string", description = "Window title" } }, required = new[] { "title" } },
        "get_pixel_color" => new { type = "object", properties = new { x = new { type = "integer" }, y = new { type = "integer" } }, required = new[] { "x", "y" } },
        "find_image_on_screen" => new { type = "object", properties = new { template = new { type = "string", description = "Base64-encoded template image" }, threshold = new { type = "number", description = "Match threshold 0-1" } }, required = new[] { "template" } },
        "type_text" => new { type = "object", properties = new { text = new { type = "string" } }, required = new[] { "text" } },
        "press_key" => new { type = "object", properties = new { key = new { type = "string" } }, required = new[] { "key" } },
        "move_mouse" => new { type = "object", properties = new { x = new { type = "integer" }, y = new { type = "integer" } }, required = new[] { "x", "y" } },
        "click" => new { type = "object", properties = new { x = new { type = "integer" }, y = new { type = "integer" }, button = new { type = "string", description = "left or right" } }, required = new[] { "x", "y" } },
        "drag" => new { type = "object", properties = new { fromX = new { type = "integer" }, fromY = new { type = "integer" }, toX = new { type = "integer" }, toY = new { type = "integer" } }, required = new[] { "fromX", "fromY", "toX", "toY" } },
        "scroll" => new { type = "object", properties = new { deltaX = new { type = "integer" }, deltaY = new { type = "integer" } }, required = new[] { "deltaX", "deltaY" } },
        "run_command" => new { type = "object", properties = new { command = new { type = "string" }, args = new { type = "string" }, workingDir = new { type = "string" } }, required = new[] { "command" } },
        "kill_process" => new { type = "object", properties = new { processId = new { type = "integer" } }, required = new[] { "processId" } },
        "read_file" => new { type = "object", properties = new { path = new { type = "string" } }, required = new[] { "path" } },
        "write_file" => new { type = "object", properties = new { path = new { type = "string" }, content = new { type = "string" } }, required = new[] { "path", "content" } },
        "list_dir" => new { type = "object", properties = new { path = new { type = "string" } } },
        "clipboard_set" => new { type = "object", properties = new { text = new { type = "string" } }, required = new[] { "text" } },
        "send_message" => new { type = "object", properties = new { to = new { type = "string" }, content = new { type = "string" } }, required = new[] { "to", "content" } },
        "send_group_message" => new { type = "object", properties = new { groupId = new { type = "string" }, content = new { type = "string" } }, required = new[] { "groupId", "content" } },
        "get_contacts" => new { type = "object", properties = new { } },
        "upload_media" => new { type = "object", properties = new { filePath = new { type = "string" }, mediaType = new { type = "string" } }, required = new[] { "filePath" } },
        "download_media" => new { type = "object", properties = new { url = new { type = "string" }, localPath = new { type = "string" } }, required = new[] { "url", "localPath" } },
        "upload_file" => new { type = "object", properties = new { localPath = new { type = "string" }, remotePath = new { type = "string" } }, required = new[] { "localPath", "remotePath" } },
        "download_file" => new { type = "object", properties = new { remotePath = new { type = "string" }, localPath = new { type = "string" } }, required = new[] { "remotePath", "localPath" } },
        "sync_folder" => new { type = "object", properties = new { localFolder = new { type = "string" }, remoteFolder = new { type = "string" } }, required = new[] { "localFolder", "remoteFolder" } },
        "get_share_status" => new { type = "object", properties = new { } },
        "get_screen_stream" => new { type = "object", properties = new { quality = new { type = "integer" }, maxWidth = new { type = "integer" } } },
        "send_input" => new { type = "object", properties = new { inputType = new { type = "string" }, data = new { type = "object" } }, required = new[] { "inputType", "data" } },
        "get_hosts" => new { type = "object", properties = new { } },
        "get_address_book" => new { type = "object", properties = new { } },
        "launch_app" => new { type = "object", properties = new { app = new { type = "string" }, args = new { type = "string" } }, required = new[] { "app" } },
        "close_app" => new { type = "object", properties = new { title = new { type = "string" } }, required = new[] { "title" } },
        "focus_window" => new { type = "object", properties = new { title = new { type = "string" } }, required = new[] { "title" } },
        _ => new { type = "object", properties = new { } }
    };

    public ValueTask DisposeAsync() { _cts?.Cancel(); _cts?.Dispose(); return ValueTask.CompletedTask; }
}

public record ToolInfo(string Name, string Description);
