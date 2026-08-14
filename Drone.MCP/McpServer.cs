using global::System.Diagnostics;
using global::System.Net;
using global::System.Net.WebSockets;
using global::System.Text;
using global::System.Text.Json;
using Drone.Core;
using Drone.Core.Protocol;

namespace Drone.MCP;

/// <summary>
/// MCP server supporting two transports:
///   1. NMCP binary protocol over shared memory (local, zero-copy, atomic state machine)
///   2. JSON-RPC over WebSocket (remote LAN/cloud access, with optional TLS)
/// Both transports share the same HandleRequestAsync pipeline and tool registry.
/// </summary>
public class McpServer : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly Dictionary<string, Func<JsonElement, Task<JsonElement>>> _tools = new();
    private CancellationTokenSource? _cts;
    private HttpListener? _wsListener;
    private readonly List<WebSocket> _activeWsClients = new();
    /// <summary>Per-client write locks keyed by WebSocket instance.</summary>
    private readonly Dictionary<WebSocket, SemaphoreSlim> _clientWriteLocks = new();
    private readonly object _wsClientsLock = new();

    /// <summary>Bearer token required to connect. Null/empty = no auth required.</summary>
    private string? _authToken;

    /// <summary>Audit logger for security-sensitive operations.</summary>
    private AuditLogger? _audit;

    /// <summary>Maximum allowed WebSocket message size (10 MB). Prevents DoS via memory exhaustion.</summary>
    private const int MaxMessageSize = 10 * 1024 * 1024;

    /// <summary>Default per-request timeout for tool calls.</summary>
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Maximum concurrent WebSocket connections.</summary>
    private const int MaxConnections = 16;

    /// <summary>Minimum interval between requests per client (50ms = 20 req/s).</summary>
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Timeout for sending responses to clients (prevents slow-client hangs).</summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Cached empty JSON object to avoid repeated allocation.</summary>
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement;

    // --- Atomic shared memory layout (compatible with V.E.L.O.C.I.T.Y.-MCP) ---
    // Request channel:  [0] = state byte, [1..4] = payload length (int32 LE), [5..4099] = payload (4096 bytes)
    // Response channel: [4100] = state byte, [4101..4104] = payload length (int32 LE), [4105..65535] = payload (61431 bytes)
    private const int ShmemTotalSize = 65536;
    private const int ReqStateOffset = 0;
    private const int ReqLenOffset = 1;
    private const int ReqPayloadOffset = 5;
    private const int ReqPayloadSize = 4096;
    private const int ResStateOffset = 4100;
    private const int ResLenOffset = 4101;
    private const int ResPayloadOffset = 4105;
    private const int ResPayloadSize = ShmemTotalSize - ResPayloadOffset;

    // Atomic state machine (matches V.E.L.O.C.I.T.Y.-MCP shmem protocol)
    private const byte StateIdle = 0;
    private const byte StateReqReady = 1;
    private const byte StateProcessing = 2;
    private const byte StateResReady = 3;
    private const byte StateError = 4;

    /// <summary>Polling interval for shared memory (100 microseconds via spin-wait).</summary>
    private const int ShmemSpinWaitIterations = 30;

    // Metrics
    private long _totalRequests;
    private long _totalErrors;
    private long _totalRejected;
    private readonly long _startTimeMs = Environment.TickCount64;

    public McpServer(ILogger logger) => _logger = logger;

    /// <summary>Set the bearer token required for WebSocket connections.</summary>
    public void SetAuthToken(string? token) => _authToken = token;

    /// <summary>Set the audit logger for security event tracking.</summary>
    public void SetAuditLogger(AuditLogger? audit) => _audit = audit;

    public void RegisterTool(string name, Func<JsonElement, Task<JsonElement>> handler) => _tools[name] = handler;

    public ToolInfo[] GetToolList() => _tools.Keys.Select(name => new ToolInfo(name, GetToolDescription(name))).ToArray();
    /// <summary>Directly invoke a tool by name with arguments. Returns the result JsonElement.</summary>
    public async Task<JsonElement> InvokeToolAsync(string toolName, JsonElement args)
    {
        if (_tools.TryGetValue(toolName, out var handler))
        {
            return await handler(args);
        }
        return JsonDocument.Parse("{\"error\":\"Unknown tool\"}").RootElement;
    }

    /// <summary>Number of currently connected WebSocket clients.</summary>
    public int ConnectedClientCount
    {
        get { lock (_wsClientsLock) return _activeWsClients.Count(w => w.State == WebSocketState.Open); }
    }

    /// <summary>Total requests handled since startup.</summary>
    public long TotalRequests => Interlocked.Read(ref _totalRequests);

    /// <summary>Total errors since startup.</summary>
    public long TotalErrors => Interlocked.Read(ref _totalErrors);

    /// <summary>Total rejected requests (auth/rate-limit/connection limit).</summary>
    public long TotalRejected => Interlocked.Read(ref _totalRejected);

    /// <summary>
    /// Run the MCP server over NMCP shared memory with atomic state machine.
    /// Uses 5-state protocol: IDLE -> REQ_READY -> PROCESSING -> RES_READY -> IDLE
    /// Polls at 100 microsecond intervals for low-latency local IPC.
    /// </summary>
    public async Task RunAsync(string bufferPath = "nmcp_drone.shm", int bufferSize = ShmemTotalSize, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _logger.LogInformation("MCP NMCP server starting at {Path} ({Size} bytes, atomic shmem)", bufferPath, bufferSize);

#pragma warning disable CA1416 // MemoryMappedFile is Windows-only, NMCP shmem is opt-in
        var mmf = global::System.IO.MemoryMappedFiles.MemoryMappedFile.CreateOrOpen(
            bufferPath, bufferSize, global::System.IO.MemoryMappedFiles.MemoryMappedFileAccess.ReadWrite);
        var view = mmf.CreateViewAccessor();

        try
        {
            // Initialize both channels to IDLE
            view.Write(ReqStateOffset, StateIdle);
            view.Write(ResStateOffset, StateIdle);

            var spinWait = new SpinWait();

            while (!_cts.Token.IsCancellationRequested)
            {
                // Poll for REQ_READY state (atomic read)
                var reqState = view.ReadByte(ReqStateOffset);

                if (reqState == StateReqReady)
                {
                    // Transition to PROCESSING (single-client shmem, safe without CAS)
                    view.Write(ReqStateOffset, StateProcessing);
                    try
                    {
                        // Read request payload
                        var reqLen = view.ReadInt32(ReqLenOffset);
                        if (reqLen <= 0 || reqLen > ReqPayloadSize)
                        {
                            view.Write(ReqStateOffset, StateIdle);
                            continue;
                        }

                        var payload = new byte[reqLen];
                        view.ReadArray(ReqPayloadOffset, payload, 0, reqLen);

                        // Parse and handle request
                        var json = Encoding.UTF8.GetString(payload);
                        using var doc = JsonDocument.Parse(json);
                        var response = await HandleRequestAsync(doc.RootElement, "shmem");

                        // Write response
                        var responseJson = JsonSerializer.Serialize(response);
                        var responseBytes = Encoding.UTF8.GetBytes(responseJson);

                        if (responseBytes.Length <= ResPayloadSize)
                        {
                            view.Write(ResLenOffset, responseBytes.Length);
                            if (responseBytes.Length > 0)
                                view.WriteArray(ResPayloadOffset, responseBytes, 0, responseBytes.Length);
                            view.Write(ResStateOffset, StateResReady);
                        }
                        else
                        {
                            // Response too large — write error
                            var errMsg = Encoding.UTF8.GetBytes("{\"error\":\"Response too large for shmem buffer\"}");
                            view.Write(ResLenOffset, errMsg.Length);
                            view.WriteArray(ResPayloadOffset, errMsg, 0, errMsg.Length);
                            view.Write(ResStateOffset, StateResReady);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("MCP shmem request error: {Error}", ex.Message);
                        view.Write(ReqStateOffset, StateError);
                    }
                }
                else if (reqState == StateError)
                {
                    // Client signaled error, reset to idle
                    view.Write(ReqStateOffset, StateIdle);
                }

                // 100 microsecond polling via spin-wait (much lower latency than Task.Delay(1))
                spinWait.SpinOnce();
                if (spinWait.Count > ShmemSpinWaitIterations)
                {
                    // After spinning, yield to avoid burning CPU
                    await Task.Delay(0, _cts.Token);
                    spinWait.Reset();
                }
            }
        }
        finally
        {
            view.Dispose();
            mmf.Dispose();
        }
#pragma warning restore CA1416
    }

    /// <summary>
    /// Run the MCP server as a JSON-RPC WebSocket endpoint.
    /// Supports both http:// (ws://) and https:// (wss://) URLs.
    /// For TLS: bind a certificate at the OS level, then use https:// prefix.
    ///   Linux: use openssl to create cert, configure HttpListener
    ///   Docker: terminate TLS at reverse proxy (nginx/caddy) and forward to http://
    ///   Windows: netsh http add sslcert
    /// </summary>
    public async Task RunWebSocketAsync(string url = "http://+:9100", CancellationToken ct = default)
    {
        _cts ??= CancellationTokenSource.CreateLinkedTokenSource(ct);
        var isTls = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        _logger.LogInformation("MCP WebSocket server starting at {Url} (max {MaxConn} connections, TLS: {Tls})",
            url, MaxConnections, isTls ? "enabled" : "disabled");

        _wsListener = new HttpListener();
        var listenUrl = url;
        _wsListener.Prefixes.Add(listenUrl.EndsWith("/") ? listenUrl + "mcp/" : listenUrl + "/mcp/");
        // Also listen on root for health checks
        _wsListener.Prefixes.Add(listenUrl.EndsWith("/") ? listenUrl : listenUrl + "/");

        try
        {
            _wsListener.Start();
        }
        catch (Exception ex) when (url.Contains("+") || url.Contains("0.0.0.0"))
        {
            // On Windows, HttpListener requires admin for 0.0.0.0 — fall back to localhost
            _logger.LogWarning("Cannot bind to all interfaces. Falling back to localhost...");
            _wsListener.Close();
            listenUrl = url.Replace("+", "localhost").Replace("0.0.0.0", "localhost");
            _wsListener = new HttpListener();
            _wsListener.Prefixes.Add(listenUrl.EndsWith("/") ? listenUrl + "mcp/" : listenUrl + "/mcp/");
            _wsListener.Prefixes.Add(listenUrl.EndsWith("/") ? listenUrl : listenUrl + "/");
            try
            {
                _wsListener.Start();
                _logger.LogInformation("MCP WebSocket server started at {Url}", listenUrl);
            }
            catch (Exception ex2)
            {
                _logger.LogError("Failed to start HTTP listener on fallback URL {Url}: {Error}", listenUrl, ex2.Message);
                throw;
            }
        }
        catch (Exception ex)
        {
            if (isTls)
            {
                _logger.LogError("Failed to start HTTPS listener. Ensure a TLS certificate is bound for the URL. " +
                    "Linux: configure cert with openssl. Docker: use nginx/caddy TLS termination. Error: {Error}", ex.Message);
            }
            else
            {
                _logger.LogError("Failed to start HTTP listener: {Error}", ex.Message);
            }
            throw;
        }

        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var context = await _wsListener.GetContextAsync();
                var clientAddr = context.Request.RemoteEndPoint?.ToString() ?? "unknown";

                // Health check endpoint — responds to GET /health with 200 OK
                if (!context.Request.IsWebSocketRequest &&
                    context.Request.Url?.AbsolutePath.EndsWith("/health") == true)
                {
                    var healthJson = JsonSerializer.Serialize(new
                    {
                        status = "healthy",
                        uptimeSec = (Environment.TickCount64 - _startTimeMs) / 1000,
                        connectedClients = ConnectedClientCount,
                        totalRequests = TotalRequests,
                        totalErrors = TotalErrors,
                        totalRejected = TotalRejected,
                        toolsAvailable = _tools.Count,
                        tls = isTls
                    });
                    var healthBytes = Encoding.UTF8.GetBytes(healthJson);
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = healthBytes.Length;
                    context.Response.AddHeader("Access-Control-Allow-Origin", "*");
                    // Security headers
                    context.Response.AddHeader("X-Content-Type-Options", "nosniff");
                    context.Response.AddHeader("X-Frame-Options", "DENY");
                    await context.Response.OutputStream.WriteAsync(healthBytes, _cts.Token);
                    context.Response.Close();
                    continue;
                }

                if (context.Request.IsWebSocketRequest)
                {
                    // Connection limit check
                    if (ConnectedClientCount >= MaxConnections)
                    {
                        Interlocked.Increment(ref _totalRejected);
                        _audit?.LogSecurity(clientAddr, "connection_limit", $"Max {MaxConnections} reached");
                        context.Response.StatusCode = 503;
                        var msg = Encoding.UTF8.GetBytes("{\"error\":\"Server at maximum connections\"}");
                        await context.Response.OutputStream.WriteAsync(msg, _cts.Token);
                        context.Response.Close();
                        _logger.LogWarning("Rejected connection: max connections ({Max}) from {Remote}", MaxConnections, clientAddr);
                        continue;
                    }

                    // Authenticate if token is configured (constant-time comparison)
                    if (!string.IsNullOrEmpty(_authToken))
                    {
                        var queryToken = context.Request.QueryString["token"];
                        if (!SecureCompare(queryToken, _authToken))
                        {
                            Interlocked.Increment(ref _totalRejected);
                            _audit?.LogSecurity(clientAddr, "auth_failure", "Invalid or missing token");
                            context.Response.StatusCode = 401;
                            var msg = Encoding.UTF8.GetBytes("{\"error\":\"Unauthorized\"}");
                            await context.Response.OutputStream.WriteAsync(msg, _cts.Token);
                            context.Response.Close();
                            _logger.LogWarning("Rejected connection: invalid token from {Remote}", clientAddr);
                            continue;
                        }
                    }

                    var wsContext = await context.AcceptWebSocketAsync(null);
                    var ws = wsContext.WebSocket;
                    var clientWriteLock = new SemaphoreSlim(1, 1);
                    lock (_wsClientsLock)
                    {
                        _activeWsClients.Add(ws);
                        _clientWriteLocks[ws] = clientWriteLock;
                    }
                    _audit?.LogConnection(clientAddr, "connect", $"Total: {ConnectedClientCount}");
                    _logger.LogInformation("MCP WebSocket client connected from {Addr} ({Count} total)", clientAddr, ConnectedClientCount);
                    _ = Task.Run(() => HandleWebSocketClient(ws, clientWriteLock, clientAddr, _cts.Token));
                }
                else
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                }
            }
        }
        catch (ObjectDisposedException) { /* listener closed during shutdown */ }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError("MCP WebSocket server error: {Error}", ex.Message);
        }
        finally
        {
            try { _wsListener?.Stop(); } catch { }
            try { _wsListener?.Close(); } catch { }
        }
    }

    private async Task HandleWebSocketClient(WebSocket ws, SemaphoreSlim writeLock, string clientAddr, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        var lastRequestTime = DateTime.MinValue;

        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Ack", CancellationToken.None); } catch { }
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);

                    // Enforce max message size
                    if (ms.Length > MaxMessageSize)
                    {
                        Interlocked.Increment(ref _totalRejected);
                        _audit?.LogSecurity(clientAddr, "message_too_large", $"Size: {ms.Length} bytes");
                        _logger.LogWarning("WebSocket client {Addr} sent message exceeding {Max}MB limit", clientAddr, MaxMessageSize / 1024 / 1024);
                        var errorMsg = JsonSerializer.Serialize(new
                        {
                            jsonrpc = "2.0", id = (string?)null,
                            error = new { code = -32600, message = "Message too large" }
                        });
                        var errorBytes = Encoding.UTF8.GetBytes(errorMsg);
                        await SafeSendAsync(ws, errorBytes, writeLock, ct);
                        await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too large", CancellationToken.None);
                        return;
                    }
                } while (!result.EndOfMessage);

                // Per-client rate limiting
                var now = DateTime.UtcNow;
                if (now - lastRequestTime < MinRequestInterval)
                {
                    Interlocked.Increment(ref _totalRejected);
                    _audit?.LogSecurity(clientAddr, "rate_limit", "Request too frequent");
                    var rateLimitError = JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0", id = (string?)null,
                        error = new { code = -32000, message = "Rate limit exceeded" }
                    });
                    var rlBytes = Encoding.UTF8.GetBytes(rateLimitError);
                    await SafeSendAsync(ws, rlBytes, writeLock, ct);
                    continue;
                }
                lastRequestTime = now;

                var json = Encoding.UTF8.GetString(ms.ToArray());
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var response = await HandleRequestAsync(doc.RootElement, clientAddr);
                    var responseJson = JsonSerializer.Serialize(response);
                    var responseBytes = Encoding.UTF8.GetBytes(responseJson);
                    await SafeSendAsync(ws, responseBytes, writeLock, ct);
                }
                catch (JsonException)
                {
                    Interlocked.Increment(ref _totalErrors);
                    var errorResponse = JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id = (string?)null,
                        error = new { code = -32700, message = "Parse error" }
                    });
                    var errorBytes = Encoding.UTF8.GetBytes(errorResponse);
                    await SafeSendAsync(ws, errorBytes, writeLock, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { _logger.LogWarning("WebSocket client {Addr} disconnected: {Error}", clientAddr, ex.Message); }
        catch (Exception ex) { _logger.LogWarning("WebSocket client {Addr} error: {Error}", clientAddr, ex.Message); }
        finally
        {
            lock (_wsClientsLock)
            {
                _activeWsClients.Remove(ws);
                _clientWriteLocks.Remove(ws);
            }
            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
            {
                try
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", closeCts.Token);
                }
                catch { }
            }
            writeLock.Dispose();
            ws.Dispose();
            _audit?.LogConnection(clientAddr, "disconnect", $"Remaining: {ConnectedClientCount}");
            _logger.LogInformation("MCP WebSocket client disconnected from {Addr} ({Count} remaining)", clientAddr, ConnectedClientCount);
        }
    }

    /// <summary>Thread-safe WebSocket send with timeout.</summary>
    private static async Task SafeSendAsync(WebSocket ws, byte[] data, SemaphoreSlim writeLock, CancellationToken ct)
    {
        if (!await writeLock.WaitAsync(SendTimeout, ct)) return;
        try
        {
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(SendTimeout);
            await ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, sendCts.Token);
        }
        catch (OperationCanceledException) { /* send timeout or cancel */ }
        finally
        {
            writeLock.Release();
        }
    }

    /// <summary>Constant-time string comparison to prevent timing attacks.</summary>
    private static bool SecureCompare(string? a, string b)
    {
        if (a is null) return false;
        if (a.Length != b.Length) return false;
        var result = 0;
        for (var i = 0; i < a.Length; i++)
            result |= a[i] ^ b[i];
        return result == 0;
    }

    /// <summary>Send a notification to all connected WebSocket clients.</summary>
    public async Task BroadcastNotificationAsync(string json, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        (WebSocket ws, SemaphoreSlim writeLock)[] clients;
        lock (_wsClientsLock)
        {
            clients = _activeWsClients
                .Where(w => w.State == WebSocketState.Open)
                .Select(w => (ws: w, writeLock: _clientWriteLocks.GetValueOrDefault(w)!))
                .Where(c => c.writeLock != null)
                .ToArray();
        }

        foreach (var (ws, writeLock) in clients)
        {
            try { await SafeSendAsync(ws, bytes, writeLock, ct); }
            catch { /* individual client failure is non-fatal */ }
        }
    }

    public Task<object?> HandleRequestAsync(JsonElement request, string clientAddress = "unknown")
        => HandleRequestInternalAsync(request, clientAddress);

    private async Task<object?> HandleRequestInternalAsync(JsonElement request, string clientAddress)
    {
        Interlocked.Increment(ref _totalRequests);
        // Extract id as raw text string — safely handles missing "id" field (null per JSON-RPC spec)
        var idText = request.TryGetProperty("id", out var idProp) ? idProp.GetRawText() : "null";

        if (!request.TryGetProperty("method", out var methodProp))
            return new { jsonrpc = "2.0", id = idText, error = new { code = -32600, message = "Invalid Request: missing method" } };

        var method = methodProp.GetString() ?? "";
        object? result = method switch
        {
            "initialize" => new
            {
                jsonrpc = "2.0",
                id = idText,
                result = new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "velocity-drone", version = "1.0.0" }
                }
            },
            "tools/list" => BuildToolsList(idText),
            "tools/call" => await HandleToolCall(request, idText, clientAddress),
            "notifications/initialized" => null,
            _ => new { jsonrpc = "2.0", id = idText, error = new { code = -32601, message = "Method not found: " + method } }
        };

        return result;
    }

    private object BuildToolsList(string idText)
    {
        var toolList = GetToolList().Select(t => new
        {
            name = t.Name,
            description = t.Description,
            inputSchema = GetToolSchema(t.Name)
        }).ToArray();
        return new { jsonrpc = "2.0", id = idText, result = new { tools = toolList } };
    }

    private async Task<object> HandleToolCall(JsonElement request, string idText, string clientAddress)
    {
        if (!request.TryGetProperty("params", out var paramsProp) ||
            !paramsProp.TryGetProperty("name", out var nameProp))
            return new { jsonrpc = "2.0", id = idText, error = new { code = -32602, message = "Invalid params: missing tool name" } };

        var toolName = nameProp.GetString() ?? "";
        var args = paramsProp.TryGetProperty("arguments", out var argsProp)
            ? argsProp : EmptyObject;

        if (!_tools.TryGetValue(toolName, out var handler))
            return new { jsonrpc = "2.0", id = idText, result = new { content = new[] { new { type = "text", text = "Unknown tool: " + toolName } }, isError = true } };

        var sw = Stopwatch.StartNew();
        try
        {
            // Apply per-request timeout to prevent hanging tool calls
            using var timeoutCts = new CancellationTokenSource(DefaultRequestTimeout);
            var handlerTask = handler(args);
            var completedTask = await Task.WhenAny(handlerTask, Task.Delay(DefaultRequestTimeout, timeoutCts.Token));

            sw.Stop();

            if (completedTask != handlerTask)
            {
                Interlocked.Increment(ref _totalErrors);
                _audit?.LogToolCall(clientAddress, toolName, sw.ElapsedMilliseconds, true, "Timed out");
                return new { jsonrpc = "2.0", id = idText, result = new { content = new[] { new { type = "text", text = $"Tool '{toolName}' timed out after {DefaultRequestTimeout.TotalSeconds}s" } }, isError = true } };
            }

            var handlerResult = await handlerTask;
            _audit?.LogToolCall(clientAddress, toolName, sw.ElapsedMilliseconds, false);
            return new { jsonrpc = "2.0", id = idText, result = new { content = new[] { new { type = "text", text = handlerResult.GetRawText() } } } };
        }
        catch (Exception ex)
        {
            sw.Stop();
            Interlocked.Increment(ref _totalErrors);
            // Sanitize error message — don't leak internal paths or stack traces
            var safeMessage = ex is OperationCanceledException ? "Operation cancelled"
                : ex is UnauthorizedAccessException ? "Access denied"
                : ex is FileNotFoundException ? "File not found"
                : ex is DirectoryNotFoundException ? "Directory not found"
                : ex.InnerException?.Message ?? ex.Message;
            if (safeMessage.Length > 200) safeMessage = safeMessage[..200] + "...";
            _audit?.LogToolCall(clientAddress, toolName, sw.ElapsedMilliseconds, true, safeMessage);
            return new { jsonrpc = "2.0", id = idText, result = new { content = new[] { new { type = "text", text = "Error: " + safeMessage } }, isError = true } };
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
        "scroll" => "Scroll at specified position",
        "get_clipboard" => "Get the current clipboard text content",
        "set_clipboard" => "Set the clipboard text content",
        "run_command" => "Run a shell command and return output (60s timeout)",
        "list_dir" => "List files in a directory",
        "read_file" => "Read a file's contents",
        "write_file" => "Write content to a file",
        "find_file" => "Find files by name pattern",
        "get_screen_size" => "Get the primary screen resolution",
        "list_windows" => "List all visible windows with titles and bounds",
        "focus_window" => "Bring a window to the foreground by title",
        "close_window" => "Close a window by title",
        "get_process_list" => "List running processes",
        "kill_process" => "Kill a process by PID",
        "get_system_info" => "Get system information (OS, CPU, memory)",
        "get_drone_status" => "Get comprehensive drone agent status (all connections, system health, uptime)",
        "send_message" => "Send a message to a user via Messenger",
        "sync_folder" => "Trigger folder sync via Share",
        "get_status" => "Get drone agent status (legacy alias for get_drone_status)",
        _ => "No description available"
    };

    private object GetToolSchema(string name) => name switch
    {
        "capture_screen" => new { type = "object", properties = new { } },
        "capture_window" => new { type = "object", properties = new { title = new { type = "string", description = "Window title" } }, required = new[] { "title" } },
        "get_pixel_color" => new { type = "object", properties = new { x = new { type = "integer" }, y = new { type = "integer" } }, required = new[] { "x", "y" } },
        "type_text" => new { type = "object", properties = new { text = new { type = "string" } }, required = new[] { "text" } },
        "press_key" => new { type = "object", properties = new { key = new { type = "string" } }, required = new[] { "key" } },
        "move_mouse" => new { type = "object", properties = new { x = new { type = "integer" }, y = new { type = "integer" } }, required = new[] { "x", "y" } },
        "click" => new { type = "object", properties = new { x = new { type = "integer" }, y = new { type = "integer" }, button = new { type = "string", @enum = new[] { "left", "right", "middle" } } }, required = new[] { "x", "y" } },
        "drag" => new { type = "object", properties = new { fromX = new { type = "integer" }, fromY = new { type = "integer" }, toX = new { type = "integer" }, toY = new { type = "integer" } }, required = new[] { "fromX", "fromY", "toX", "toY" } },
        "scroll" => new { type = "object", properties = new { deltaX = new { type = "integer" }, deltaY = new { type = "integer" } }, required = new[] { "deltaX", "deltaY" } },
        "run_command" => new { type = "object", properties = new { command = new { type = "string" }, args = new { type = "string" }, workingDir = new { type = "string" } }, required = new[] { "command" } },
        "list_dir" => new { type = "object", properties = new { path = new { type = "string" } }, required = new[] { "path" } },
        "read_file" => new { type = "object", properties = new { path = new { type = "string" } }, required = new[] { "path" } },
        "write_file" => new { type = "object", properties = new { path = new { type = "string" }, content = new { type = "string" } }, required = new[] { "path", "content" } },
        "find_file" => new { type = "object", properties = new { path = new { type = "string" }, pattern = new { type = "string" } } },
        "send_message" => new { type = "object", properties = new { to = new { type = "string" }, content = new { type = "string" } }, required = new[] { "to", "content" } },
        "focus_window" => new { type = "object", properties = new { title = new { type = "string" } }, required = new[] { "title" } },
        "close_window" => new { type = "object", properties = new { title = new { type = "string" } }, required = new[] { "title" } },
        "kill_process" => new { type = "object", properties = new { processId = new { type = "integer" } }, required = new[] { "processId" } },
        "get_drone_status" => new { type = "object", properties = new { } },
        "get_system_info" => new { type = "object", properties = new { } },
        "get_process_list" => new { type = "object", properties = new { } },
        "get_clipboard" => new { type = "object", properties = new { } },
        "clipboard_get" => new { type = "object", properties = new { } },
        "clipboard_set" => new { type = "object", properties = new { text = new { type = "string" } }, required = new[] { "text" } },
        "get_screen_size" => new { type = "object", properties = new { } },
        "list_processes" => new { type = "object", properties = new { } },
        "list_windows" => new { type = "object", properties = new { } },
        "launch_app" => new { type = "object", properties = new { app = new { type = "string" }, args = new { type = "string" } }, required = new[] { "app" } },
        _ => new { type = "object", properties = new { } }
    };

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        // Snapshot clients under lock, close outside to avoid blocking while holding lock
        WebSocket[] clientsToClose;
        lock (_wsClientsLock)
        {
            clientsToClose = _activeWsClients.ToArray();
            _activeWsClients.Clear();
            _clientWriteLocks.Clear();
        }
        foreach (var ws in clientsToClose)
        {
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutting down", closeCts.Token);
            }
            catch { }
            ws.Dispose();
        }
        try { _wsListener?.Stop(); } catch { }
        try { _wsListener?.Close(); } catch { }
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}

public record ToolInfo(string Name, string Description);
