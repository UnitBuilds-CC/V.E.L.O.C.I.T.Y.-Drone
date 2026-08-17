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
| Shell commands | Reject `\| & ; \` $ ( )` | `Program.cs` command handler |
| Click coordinates | Bounds: -10000 to 10000 | `Program.cs` click handler |
| File paths | Path traversal protection | `EmbeddedFileServer.IsPathSafe()` |
| Config values | Range validation | `DroneConfig.Validate()` |

### Path Traversal Protection

The `EmbeddedFileServer` validates all file paths to prevent directory traversal attacks:

```csharp
private bool IsPathSafe(string relativePath)
{
    var combined = Path.Combine(_storagePath, relativePath);
    var fullPath = Path.GetFullPath(combined);
    var storageRoot = Path.GetFullPath(_storagePath);
    return fullPath.StartsWith(storageRoot, StringComparison.OrdinalIgnoreCase);
}
```

Attempts to access paths outside the storage directory return `403 Forbidden`.

### Error Sanitization

Error responses to clients never expose internal exception details:

| Exception Type | Response |
|---------------|----------|
| `UnauthorizedAccessException` | "Access denied" |
| `FileNotFoundException` | "File not found" |
| `OperationCanceledException` | "Operation cancelled" |
| All others | "Internal error" |

### MCP Authentication

The MCP server enforces bearer token authentication when remote connections are configured:

- If `DRONE_MCP_TOKEN` is set: required for all WebSocket connections
- If remote connections configured but no token: auto-generates temporary token and logs warning
- If no remote connections: auth optional (localhost-only safe)

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

All `catch { }` blocks in the codebase include inline comments explaining why the swallow is intentional. This makes disposal and cleanup paths auditable.

### Pattern

```csharp
// Disposal path — failure is expected and non-fatal
try { await _ws.CloseAsync(...); }
catch { /* WebSocket may already be faulted — disposing anyway */ }
```

### Categories

| Category | Example | Comment Style |
|----------|---------|---------------|
| Disposal cleanup | WebSocket close during Dispose | "disposing anyway" |
| Listener cleanup | HttpListener.Stop in finally | "listener may already be stopped" |
| Process enumeration | Process exited before query | "process may have exited" |
| File rotation | Delete old rotation files | "old rotation file may be locked" |
| Platform operations | Clipboard/screen capture | "may not be installed" |

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
  CMD curl -sf http://localhost:9100/health | grep -q '"status":"healthy"' || exit 1
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

100 tests covering:
- NMCP binary protocol (frame serialization, header parsing)
- Custody trail (hash chain, Merkle tree, correlation tracking)
- Circuit breaker (state transitions, thresholds, recovery)
- MCP server (tool registration, JSON-RPC, auth)
- Autonomy engine (rule evaluation, triggers)

### Running Tests

```bash
# Unit tests
dotnet test tests/Drone.Tests/Drone.Tests.csproj

# E2E tests (requires running agent)
dotnet run --project tests/Drone.E2E/Drone.E2E.csproj
```

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
