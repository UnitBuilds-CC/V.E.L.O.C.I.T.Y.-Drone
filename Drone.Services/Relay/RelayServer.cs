using global::System.Collections.Concurrent;
using global::System.Net;
using global::System.Net.WebSockets;
using global::System.Security.Cryptography;
using global::System.Text;
using global::System.Text.Json;
using Drone.Core;
using Drone.Core.Config;

namespace Drone.Services.Relay;

/// <summary>
/// Unified relay server for drone-to-drone communication.
/// Hosts Messenger relay, File Share, and Remote Bridge on a single port.
/// </summary>
public class RelayServer : IAsyncDisposable
{
    private readonly RelayConfig _config;
    private readonly string _droneId;
    private readonly ILogger _logger;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, RelayConnection> _connections = new();
    private int _totalConnections;
    private DateTime _startedAt;
    private bool _tlsEnabled;
    private readonly SemaphoreSlim _requestConcurrencyLimit;

    // Sub-services
    private MessengerRelay? _messengerRelay;
    private RemoteBridge? _remoteBridge;
    private EmbeddedRelayFileServer? _fileServer;

    public bool IsRunning => _listener?.IsListening ?? false;
    public int ConnectionCount => _totalConnections;
    public bool TlsEnabled => _tlsEnabled;
    public IReadOnlyCollection<string> ConnectedDrones => _connections.Keys.ToList().AsReadOnly();

    public RelayServer(RelayConfig config, string droneId, ILogger logger)
    {
        _config = config;
        _droneId = droneId;
        _logger = logger;
        _requestConcurrencyLimit = new SemaphoreSlim(64, 64);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _startedAt = DateTime.UtcNow;

        // Initialize sub-services
        _messengerRelay = new MessengerRelay(_config, _logger);
        _remoteBridge = new RemoteBridge(_config, _logger);
        _fileServer = new EmbeddedRelayFileServer(_config, _logger);

        _listener = new HttpListener();

        // HTTP listener (always)
        var httpPrefix = $"http://localhost:{_config.Port}/";
        _listener.Prefixes.Add(httpPrefix);

        // HTTPS listener (when TLS certificate is configured)
        if (!string.IsNullOrEmpty(_config.TlsCertificatePath))
        {
            var httpsPrefix = $"https://localhost:{_config.Port}/";
            try
            {
                _listener.Prefixes.Add(httpsPrefix);
                _logger.LogInformation("TLS enabled — HTTPS on port {Port} (cert: {CertPath})", _config.Port, _config.TlsCertificatePath);
                _logger.LogWarning("Ensure the TLS certificate is bound to port {Port} via: netsh http add sslcert ipport=0.0.0.0:{Port} certhash=<thumbprint> appid={{<guid>}}", _config.Port, _config.Port);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to add HTTPS prefix: {Error}. Continuing with HTTP only.", ex.Message);
            }
        }

        _listener.Start();
        _tlsEnabled = !string.IsNullOrEmpty(_config.TlsCertificatePath);

        _logger.LogInformation("Relay server started on port {Port} (drone: {DroneId}, TLS: {Tls})", _config.Port, _droneId, _tlsEnabled ? "enabled" : "disabled");

        _ = Task.Run(() => ListenLoop(_cts.Token));
        await Task.CompletedTask;
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                if (!_requestConcurrencyLimit.Wait(0))
                {
                    ctx.Response.StatusCode = 503;
                    var msg = global::System.Text.Encoding.UTF8.GetBytes("{\"error\":\"Server busy\"}");
                    await ctx.Response.OutputStream.WriteAsync(msg);
                    ctx.Response.Close();
                    continue;
                }
                _ = Task.Run(async () =>
                {
                    try { await HandleRequest(ctx, ct); }
                    finally { _requestConcurrencyLimit.Release(); }
                });
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("Relay listen error: {Error}", ex.Message);
            }
        }
    }

    private async Task HandleRequest(HttpListenerContext ctx, CancellationToken ct)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        var method = ctx.Request.HttpMethod;

        try
        {
            // Health endpoint (no auth required)
            if (path == "/health" || path == "/health/")
            {
                await WriteJson(ctx, new
                {
                    status = "healthy",
                    drone_id = _droneId,
                    connections = _totalConnections,
                    tls = _tlsEnabled,
                    uptime_seconds = (DateTime.UtcNow - _startedAt).TotalSeconds
                });
                return;
            }

            // Root info endpoint
            if (path == "/" || path == "")
            {
                await WriteJson(ctx, new
                {
                    service = "Velocity Drone Relay",
                    version = "1.0.0",
                    drone_id = _droneId,
                    tls = _tlsEnabled,
                    endpoints = new[] { "/relay/messenger/", "/relay/share/", "/relay/remote/", "/health" },
                    connections = _totalConnections,
                    connected_drones = _connections.Keys.ToArray()
                });
                return;
            }

            // Auth check for relay endpoints
            if (path.StartsWith("/relay/"))
            {
                if (!AuthenticateRequest(ctx))
                {
                    ctx.Response.StatusCode = 401;
                    await WriteJson(ctx, new { error = "Unauthorized. Provide X-Api-Key header." });
                    return;
                }

                // Connection limit check
                if (_totalConnections >= _config.MaxConnections)
                {
                    ctx.Response.StatusCode = 503;
                    await WriteJson(ctx, new { error = "Max connections reached", max = _config.MaxConnections });
                    return;
                }
            }

            // WebSocket origin validation — reject cross-origin WebSocket upgrades
            if (ctx.Request.IsWebSocketRequest)
            {
                var origin = ctx.Request.Headers["Origin"];
                if (!string.IsNullOrEmpty(origin) && !IsOriginAllowed(origin))
                {
                    ctx.Response.StatusCode = 403;
                    await WriteJson(ctx, new { error = "Origin not allowed" });
                    return;
                }
            }

            // Route to sub-services
            if (path.StartsWith("/relay/messenger") && ctx.Request.IsWebSocketRequest)
            {
                await HandleMessengerWebSocket(ctx, ct);
            }
            else if (path.StartsWith("/relay/remote") && ctx.Request.IsWebSocketRequest)
            {
                await HandleRemoteWebSocket(ctx, ct);
            }
            else if (path.StartsWith("/relay/share/ws") && ctx.Request.IsWebSocketRequest)
            {
                await HandleShareWebSocket(ctx, ct);
            }
            else if (path.StartsWith("/relay/share"))
            {
                if (_fileServer != null)
                    await _fileServer.HandleRequest(ctx, ct);
                else
                {
                    ctx.Response.StatusCode = 503;
                    await WriteJson(ctx, new { error = "File share not initialized" });
                }
            }
            else
            {
                ctx.Response.StatusCode = 404;
                await WriteJson(ctx, new { error = "Not found", path });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Relay request error on {Path}: {Error}", path, ex.Message);
            try
            {
                ctx.Response.StatusCode = 500;
                await WriteJson(ctx, new { error = "Internal server error" });
            }
            catch { /* response may already be sent */ }
        }
    }

    private bool AuthenticateRequest(HttpListenerContext ctx)
    {
        if (string.IsNullOrEmpty(_config.ApiKey)) return true;

        var headerKey = ctx.Request.Headers["X-Api-Key"];
        if (!string.IsNullOrEmpty(headerKey) && SecureCompare(headerKey, _config.ApiKey)) return true;

        return false;
    }

    /// <summary>Constant-time string comparison to prevent timing attacks.</summary>
    private static bool SecureCompare(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }

    /// <summary>
    /// Validates WebSocket Origin header. Allows:
    /// - No origin (native drone clients don't send Origin)
    /// - localhost/127.0.0.1 origins (same-machine connections)
    /// </summary>
    private bool IsOriginAllowed(string origin)
    {
        if (string.IsNullOrEmpty(origin)) return true;
        try
        {
            var uri = new Uri(origin);
            var host = uri.Host.ToLowerInvariant();
            return host == "localhost" || host == "127.0.0.1" || host == "[::1]";
        }
        catch { return false; }
    }

    private async Task HandleMessengerWebSocket(HttpListenerContext ctx, CancellationToken ct)
    {
        var wsContext = await ctx.AcceptWebSocketAsync(subProtocol: null);
        var ws = wsContext.WebSocket;
        var username = ctx.Request.QueryString["username"] ?? $"drone-{Guid.NewGuid():N}";

        var conn = new RelayConnection(username, "messenger", ws);
        _connections.TryAdd(username, conn);
        Interlocked.Increment(ref _totalConnections);

        _logger.LogInformation("Messenger relay: {Username} connected (total: {Count})", username, _totalConnections);

        try
        {
            await _messengerRelay!.HandleConnection(username, ws, ct);
        }
        finally
        {
            _connections.TryRemove(username, out _);
            Interlocked.Decrement(ref _totalConnections);
            _logger.LogInformation("Messenger relay: {Username} disconnected (total: {Count})", username, _totalConnections);
        }
    }

    private async Task HandleRemoteWebSocket(HttpListenerContext ctx, CancellationToken ct)
    {
        var wsContext = await ctx.AcceptWebSocketAsync(subProtocol: null);
        var ws = wsContext.WebSocket;
        var role = ctx.Request.QueryString["role"] ?? "target";
        var droneId = ctx.Request.QueryString["drone_id"] ?? $"drone-{Guid.NewGuid():N}";
        var target = ctx.Request.QueryString["target"];

        var connId = $"{droneId}:{role}";
        var conn = new RelayConnection(droneId, "remote", ws) { TargetDrone = target };
        _connections.TryAdd(connId, conn);
        Interlocked.Increment(ref _totalConnections);

        _logger.LogInformation("Remote bridge: {DroneId} connected as {Role} (target: {Target})", droneId, role, target ?? "none");

        try
        {
            await _remoteBridge!.HandleConnection(droneId, role, target, ws, ct);
        }
        finally
        {
            _connections.TryRemove(connId, out _);
            Interlocked.Decrement(ref _totalConnections);
            _logger.LogInformation("Remote bridge: {DroneId} disconnected (total: {Count})", droneId, _totalConnections);
        }
    }

    private async Task HandleShareWebSocket(HttpListenerContext ctx, CancellationToken ct)
    {
        var wsContext = await ctx.AcceptWebSocketAsync(subProtocol: null);
        var ws = wsContext.WebSocket;
        var droneId = ctx.Request.QueryString["drone_id"] ?? $"drone-{Guid.NewGuid():N}";

        var conn = new RelayConnection(droneId, "share", ws);
        _connections.TryAdd($"{droneId}:share", conn);
        Interlocked.Increment(ref _totalConnections);

        try
        {
            await _fileServer!.HandleNotifications(droneId, ws, ct);
        }
        finally
        {
            _connections.TryRemove($"{droneId}:share", out _);
            Interlocked.Decrement(ref _totalConnections);
        }
    }

    private static async Task WriteJson(HttpListenerContext ctx, object data)
    {
        ctx.Response.ContentType = "application/json";
        if (ctx.Response.StatusCode == 200 || ctx.Response.StatusCode == 0)
            ctx.Response.StatusCode = 200;
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = false });
        var bytes = Encoding.UTF8.GetBytes(json);
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private bool _stopped;

    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;

        _cts?.Cancel();

        try { _listener?.Stop(); } catch (ObjectDisposedException) { }
        try { _listener?.Close(); } catch (ObjectDisposedException) { }

        if (_messengerRelay != null) await _messengerRelay.DisposeAsync();
        if (_remoteBridge != null) await _remoteBridge.DisposeAsync();
        if (_fileServer != null) await _fileServer.DisposeAsync();

        _logger.LogInformation("Relay server stopped");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
    }
}

/// <summary>
/// Represents a connected drone in the relay.
/// </summary>
public class RelayConnection
{
    public string DroneId { get; }
    public string Service { get; }
    public WebSocket WebSocket { get; }
    public string? TargetDrone { get; set; }
    public DateTime ConnectedAt { get; } = DateTime.UtcNow;

    public RelayConnection(string droneId, string service, WebSocket webSocket)
    {
        DroneId = droneId;
        Service = service;
        WebSocket = webSocket;
    }
}
