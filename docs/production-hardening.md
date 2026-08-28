# Production Hardening

**Project:** Velocity Drone  
**Status:** 10/10 production ready

---

## Overview

Velocity Drone has been hardened for production deployment with comprehensive resilience, security, and operational features. This document covers the production readiness features that go beyond the core functionality.

## Circuit Breaker Pattern

All network connectors use a thread-safe `CircuitBreaker` to prevent cascading failures when downstream services are unhealthy.

### How It Works

```
                    5 consecutive failures
    ┌──────────┐ ──────────────────────────► ┌──────────┐
    │  Closed  │                              │   Open   │
    │ (normal) │                              │ (reject) │
    └──────────┘ ◄────────────────────────── └──────────┘
                    success in HalfOpen              │
                    ┌──────────┐                     │ 30s timeout
                    │ HalfOpen │ ◄───────────────────┘
                    │ (probe)  │
                    └──────────┘
                         │ failure
                         ▼
                    ┌──────────┐
                    │   Open   │
                    │ (reject) │
                    └──────────┘
```

### Configuration

| Parameter | Default | Description |
|-----------|---------|-------------|
| `failureThreshold` | 5 | Consecutive failures before opening |
| `openTimeout` | 30s | Time before transitioning to HalfOpen |

### Integration Points

| Connector | Behavior When Open |
|-----------|-------------------|
| `MessengerConnector` | Waits 30s before retrying connection |
| `RemoteConnector` | Waits 30s before retrying connection |
| `VelocityConnection` | Waits 30s before retrying WebSocket |

### Monitoring

The circuit breaker state is exposed via the `/health` endpoint:

```json
{
  "status": "healthy",
  "messengerConnected": true,
  "uplinkConnected": true,
  "remoteConnected": true
}
```

When a connector's circuit breaker is open, reconnection attempts are logged at warning level:
```
[WRN] Messenger circuit breaker open. Waiting for recovery...
[WRN] Remote circuit breaker open. Waiting for recovery...
[WRN] Uplink circuit breaker open. Waiting for recovery...
```

## Graceful Shutdown

The agent implements a comprehensive graceful shutdown pipeline that ensures all resources are disposed in the correct order.

### Shutdown Triggers

| Trigger | Mechanism |
|---------|-----------|
| Ctrl+C | `Console.CancelKeyPress` handler |
| Docker stop | `SIGTERM` via `STOPSIGNAL` |
| Process exit | `AppDomain.CurrentDomain.ProcessExit` |
| Self-update | `masterCts.Cancel()` after update script |

### Shutdown Sequence

```
1. Signal received → masterCts.Cancel()
2. All components observe cancellation token
3. Ordered disposal (reverse creation order):
   ├── AutonomyEngine.DisposeAsync()
   ├── CustodyReporter.DisposeAsync()
   ├── RemoteConnector.DisposeAsync()
   ├── ShareConnector.DisposeAsync()
   ├── MessengerConnector.DisposeAsync()
   ├── EmbeddedFileServer.DisposeAsync()
   ├── McpServer.DisposeAsync()
   ├── CustodyAuditLogger.DisposeAsync()
   └── VelocityConnection.DisposeAsync()
4. Master CTS disposed
```

### Timeout Enforcement

Shutdown is bounded by `DRONE_SHUTDOWN_TIMEOUT` (default: 15 seconds). If disposal exceeds this timeout, a warning is logged and the process exits:

```
[WRN] Shutdown timeout (15s) exceeded. Forcing exit.
```

### Docker Configuration

```dockerfile
STOPSIGNAL SIGTERM
ENV DRONE_SHUTDOWN_TIMEOUT=15
```

## Security Features

### Input Validation

| Input | Validation | Location |
|-------|-----------|----------|
| Shell commands | Reject `\| & ; \` $ ( ) > < ^ % ! \n \r` + null bytes + length limit | `Program.cs`, `CrossPlatformProcessManager.cs` |
| Click coordinates | Bounds: -10000 to 10000 | `Program.cs` click handler |
| File paths | Path traversal + symlink/reparse point check + null byte rejection | `EmbeddedFileServer.IsPathSafe()`, `EmbeddedRelayFileServer.IsPathSafe()` |
| Config values | Range validation | `DroneConfig.Validate()` |
| API keys | Constant-time comparison (`FixedTimeEquals`) | `RelayServer`, `EmbeddedFileServer`, `McpServer` |
| Upload size | 100MB max (bounded read loop) | `EmbeddedFileServer`, `EmbeddedRelayFileServer` |
| WebSocket messages | 10-64MB max per message | All receive loops |
| Self-update | SHA-256 sidecar checksum required | `Program.cs` update handler |

### Path Traversal Protection

Both file servers validate all file paths to prevent directory traversal and symlink escape attacks:

```csharp
private bool IsPathSafe(string relativePath)
{
    if (relativePath.Contains('~') || relativePath.Contains('\0')) return false;
    var combined = Path.Combine(_storagePath, relativePath);
    var fullPath = Path.GetFullPath(combined);
    var storageRoot = Path.GetFullPath(_storagePath);
    if (!fullPath.StartsWith(storageRoot, StringComparison.OrdinalIgnoreCase))
        return false;
    // Check each path component for symlinks/reparse points
    // ... (walks each directory component checking FileAttributes.ReparsePoint)
    return true;
}
```

Attempts to access paths outside the storage directory or via symlinks return `403 Forbidden`.

### Error Sanitization

Error responses to clients never expose internal exception details:

| Exception Type | Response |
|---------------|----------|
| `UnauthorizedAccessException` | "Access denied" |
| `FileNotFoundException` | "File not found" |
| `OperationCanceledException` | "Operation cancelled" |
| All others | "Internal error" |

### MCP Authentication & TLS

The MCP server enforces bearer token authentication and TLS:

- If `DRONE_MCP_TOKEN` is set: required for all WebSocket connections
- If remote connections configured but no token: auto-generates temporary token (written to stderr)
- If no remote connections: auth optional (localhost-only safe)
- **TLS required**: MCP server refuses to start without TLS unless `DRONE_ALLOW_INSECURE_HTTP=1` is explicitly set

### Secret Redaction

URLs logged to output have query parameters redacted to prevent secret leakage:

```
[INF] Connected to Messenger at https://messenger.example.com/?***
```

### HttpClient Timeouts

All `HttpClient` instances have explicit timeouts to prevent infinite hangs:

| Client | Timeout |
|--------|---------|
| `MessengerConnector._http` | 30 seconds |
| `ShareConnector._http` | 30 seconds |

## Exception Handling

All `catch` blocks in production code log the failure. No silent exception swallowing exists in any production project.

### Pattern

```csharp
// Disposal path — failure is expected and non-fatal, but logged
try { await _ws.CloseAsync(...); }
catch (Exception ex) { _logger?.LogWarning("WebSocket close failed: {Error}", ex.Message); }
```

### Categories

| Category | Example | Behavior |
|----------|---------|----------|
| Disposal cleanup | WebSocket close during Dispose | Logged at warning level |
| Listener cleanup | HttpListener.Stop in finally | Logged at warning level |
| Audit writes | File I/O failure in audit logger | Logged + failure counter incremented |
| File rotation | Delete old rotation files | Logged at warning level |
| Custody writes | Record write failure | Logged via injected ILogger |

## Health Check

The `/health` endpoint provides comprehensive status information:

```bash
curl http://localhost:9100/health
```

```json
{
  "status": "healthy",
  "uptimeSec": 3600,
  "connectedClients": 2,
  "totalRequests": 1500,
  "totalErrors": 3,
  "totalRejected": 0,
  "toolsAvailable": 12,
  "tls": false,
  "maxConnections": 10,
  "custodySequence": 42,
  "custodyHash": "a1b2c3d4...",
  "messengerConnected": true,
  "uplinkConnected": true,
  "remoteConnected": true
}
```

### Health States

| Status | Meaning |
|--------|---------|
| `healthy` | All systems operational |
| `degraded` | Some connections down but core functional |
| `unhealthy` | Critical failure |

### Docker Health Check

```dockerfile
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3
  CMD wget -q --spider http://localhost:9100/health/live || exit 1
```

## Logging Standards

All production code uses structured logging via `ILogger`:

```csharp
_logger.LogInformation("Connected to {Service} at {Url}", "Messenger", SanitizeUrl(url));
_logger.LogWarning("Circuit breaker open. Waiting for recovery...");
_logger.LogError("Max reconnect ({Max}) reached. Error: {Error}", maxAttempts, ex.Message);
```

### Log Levels

| Level | Usage |
|-------|-------|
| `LogDebug` | Detailed diagnostic info (frame processing, state changes) |
| `LogInformation` | Normal operations (connections, disconnections) |
| `LogWarning` | Recoverable issues (reconnects, circuit breaker, timeouts) |
| `LogError` | Critical failures (max retries, auth failures) |

## Testing

### Unit Tests

181 tests covering:
- NMCP binary protocol (frame serialization, header parsing)
- Custody trail (hash chain, Merkle tree, correlation tracking, binary serialization)
- Custody server (log store, query engine, multi-drone storage)
- Circuit breaker (state transitions, thresholds, recovery)
- MCP server (tool registration, JSON-RPC, auth)
- Autonomy engine (rule evaluation, triggers, event bus)
- Relay server (file upload/download, rate limiting, WebSocket E2E)
- Drone.System (command execution, input validation, system info)
- Drone.Native FFI (graceful degradation, Merkle frame validation)
- Concurrency stress tests (EventBus, CustodyChain, AuditLogger, LogStore)

### Running Tests

```bash
# Unit tests
dotnet test tests/Drone.Tests/Drone.Tests.csproj

# E2E tests (requires running agent)
dotnet run --project tests/Drone.E2E/Drone.E2E.csproj
```

## Additional Security Measures

### Rate Limiting

| Component | Limit | Scope |
|-----------|-------|-------|
| MCP WebSocket | 20 req/s per client | Per-connection |
| EmbeddedFileServer | 30 req/s per IP | Per-IP token bucket |
| MessengerRelay | Configurable (default 30 msg/s) | Per-drone token bucket |
| RelayServer | 64 concurrent requests | Global semaphore |

### WebSocket Send Locks

Every WebSocket connection uses per-client `SemaphoreSlim` write locks to prevent concurrent `SendAsync` corruption:

| Component | Lock Type |
|-----------|-----------|
| `McpServer` | Per-client `SemaphoreSlim` in `_clientWriteLocks` |
| `MessengerRelay` | Per-client `SemaphoreSlim` in `_clientSendLocks` |
| `RemoteBridge` | Per-connection `SemaphoreSlim` in `_sendLocks` |
| `EmbeddedRelayFileServer` | Per-client `SemaphoreSlim` in `_notificationSendLocks` |
| `VelocityConnection` | Instance-level `_wsSendLock` |
| `MessengerConnector` | Instance-level `_sendLock` |
| `RemoteConnector` | Instance-level `_sendLock` |

### Message Size Limits

All WebSocket receive loops enforce maximum message sizes to prevent memory exhaustion:

| Component | Limit |
|-----------|-------|
| `McpServer` | 10 MB |
| `VelocityConnection` | 10 MB |
| `MessengerConnector` | 10 MB |
| `RemoteConnector` | 64 MB |
| `CustodyServerHost` | 50 MB |
| `MessengerRelay` | 256 KB |

### Constant-Time Comparisons

All security-critical comparisons use `CryptographicOperations.FixedTimeEquals`:

- MCP auth tokens (`McpServer.SecureCompare`)
- Relay API keys (`RelayServer.SecureCompare`)
- File server API keys (`EmbeddedFileServer`)
- Custody record hash verification (`CustodyRecord.VerifyHash`, `VerifyChain`)
- Custody binary Merkle root verification (`CustodyBinarySerializer`)
- Custody chain Merkle root verification (`CustodyChain`, `CustodyLogStore`)

### Security Headers

All HTTP endpoints include security headers:

| Header | Value | Endpoints |
|--------|-------|-----------|
| `X-Content-Type-Options` | `nosniff` | MCP health, CustodyServer query/health |
| `X-Frame-Options` | `DENY` | MCP health, CustodyServer query/health |
| `Cache-Control` | `no-store` | MCP health, CustodyServer query/health |

### TLS Enforcement

The MCP WebSocket server refuses to start without TLS unless explicitly opted out:

```bash
# Production: use HTTPS URL
DRONE_MCP_URL=https://drone.example.com:9100

# Development: explicit opt-out required
DRONE_ALLOW_INSECURE_HTTP=1 DRONE_MCP_URL=http://0.0.0.0:9100
```

### Supply Chain Security

- **CI actions SHA-pinned**: All GitHub Actions reference specific commit SHAs, not mutable tags
- **DLL integrity manifest**: `Drone.Native/checksums.sha256` contains SHA-256 hashes of pre-built native DLLs
- **Dependabot**: Automated dependency updates for NuGet, GitHub Actions, and Cargo
- **Deploy gate**: CI deploy workflow requires build+test job to pass first

### Custody Trail Integrity

- **Length-prefixed hash encoding**: `CustodyRecord.ComputeHash()` uses binary length-prefixed fields instead of delimiter-separated strings, preventing field boundary ambiguity
- **Constant-time Merkle verification**: All Merkle root comparisons use `FixedTimeEquals`
- **TOCTOU guards**: Shared memory state transitions re-verify state before committing

## Production Checklist

Before deploying to production:

- [ ] Set `DRONE_MCP_TOKEN` for MCP authentication
- [ ] Configure `DRONE_SHUTDOWN_TIMEOUT` appropriate for your environment
- [ ] Set `DRONE_CUSTODY_PATH` for persistent custody trail
- [ ] Configure `DRONE_ALLOWED_PATHS` to restrict file access
- [ ] Set connection secrets (`MESSENGER_SECRET`, `SHARE_API_KEY`, `REMOTE_API_KEY`)
- [ ] Verify health endpoint responds correctly
- [ ] Review log output for sensitive data
- [ ] Test graceful shutdown (`docker stop`, Ctrl+C)
- [ ] Confirm circuit breaker thresholds appropriate for your SLA
