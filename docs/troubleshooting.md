# Troubleshooting & FAQ

**Project:** Velocity Drone

---

## Common Issues

### Agent won't start

**Symptom:** Agent exits immediately or crashes on startup.

**Diagnostics:**
```bash
# Check for configuration errors
dotnet run --project Drone.Agent/Drone.Agent.csproj -- --dry-run

# Check logs for startup errors
# Look for [ERR] or [FTL] entries
```

**Common causes:**

| Cause | Fix |
|-------|-----|
| Invalid `appsettings.json` | Validate JSON syntax. Check for missing commas or brackets. |
| Port 9100 in use | Change `Mcp.WebSocketPort` or stop the conflicting process. |
| Missing native DLLs | Ensure `velocity_delta.dll` and `velocity_v2_ffi.dll` are in the output directory. |
| Permission denied on custody path | Ensure the user has write access to `DRONE_CUSTODY_PATH`. |

### Messenger connection fails

**Symptom:** Log shows repeated `Messenger disconnected. Reconnect attempt...`

**Diagnostics:**
```bash
# Check if Messenger server is reachable
curl -v https://your-messenger-server/health

# Check authentication
# Look for: "Messenger configured without ConnectionSecret"
```

**Common causes:**

| Cause | Fix |
|-------|-----|
| Wrong server URL | Verify `Messenger.ServerUrl` in config. |
| Missing/invalid secret | Set `ConnectionSecret` or `MESSENGER_SECRET` env var. |
| Network firewall | Ensure outbound WebSocket connections are allowed. |
| Circuit breaker open | Wait 30s for recovery, or check Messenger server health. |

### MCP tools not responding

**Symptom:** Tool calls hang or return timeout errors.

**Diagnostics:**
```bash
# Check health endpoint
curl http://localhost:9100/health

# Check if MCP server is listening
# Look for: "MCP WebSocket server listening on port 9100"
```

**Common causes:**

| Cause | Fix |
|-------|-----|
| Auth token mismatch | Set `DRONE_MCP_TOKEN` to match the server's token. |
| Connection limit reached | Check `connectedClients` in health endpoint. Increase `MaxConnections`. |
| Tool execution timeout | Check if the tool is blocked (e.g., waiting for user input). |
| Native library not loaded | Check for "Delta engine native library not available" warning. |

### Circuit breaker stays open

**Symptom:** Log shows `circuit breaker open. Waiting for recovery...` repeatedly.

**Diagnostics:**
```bash
# Check if the downstream service is healthy
# For Messenger: check Messenger server health
# For Remote: check Remote server health
# For Uplink: check WebSocket endpoint
```

**What's happening:**
The circuit breaker opens after 5 consecutive failures. It transitions to HalfOpen after 30 seconds, allowing one probe request. If the probe fails, it re-opens.

**Resolution:**
1. Fix the underlying connectivity issue
2. Wait for the next HalfOpen probe (automatic)
3. Or restart the agent to reset all circuit breakers

### Custody trail gaps

**Symptom:** Custody records have missing sequence numbers or broken hash chain.

**Diagnostics:**
```bash
# Check custody file integrity
# Look for: "Custody chain broken at sequence N"

# Check custody reporter status
# Look for: "CustodyReporter: flushed N records"
```

**Common causes:**

| Cause | Fix |
|-------|-----|
| Custody server unreachable | Check `CustodyReporter` connection. Records are buffered in-memory. |
| Disk full | Ensure custody path has sufficient space. |
| Concurrent writes | Ensure only one agent writes to the custody file. |

### High memory usage

**Symptom:** Agent memory grows over time.

**Diagnostics:**
```bash
# Check for buffer allocations
# Look for: "Delta pipeline: WxH, buffers=XMB"

# Check WebSocket client count
curl http://localhost:9100/health | grep connectedClients
```

**Common causes:**

| Cause | Fix |
|-------|-----|
| Too many WebSocket clients | Reduce `MaxConnections` or investigate client leaks. |
| Large screen resolution | Higher resolution = larger delta buffers. Consider reducing capture area. |
| Custody reporter batch backlog | Check custody server connectivity. Batches accumulate in memory. |

### Graceful shutdown hangs

**Symptom:** Agent takes a long time to shut down after Ctrl+C or `docker stop`.

**Diagnostics:**
```bash
# Check shutdown timeout
# Look for: "Shutdown timeout (15s) exceeded"

# Check which component is slow to dispose
# Logs show disposal order
```

**Resolution:**
- Increase `DRONE_SHUTDOWN_TIMEOUT` if components need more time
- Check for stuck WebSocket connections (clients not closing)
- Check for pending HTTP requests in connectors

## Diagnostic Commands

### Health Check

```bash
# Basic health
curl http://localhost:9100/health

# With custody chain info
curl http://localhost:9100/health | jq '.custodySequence, .custodyHash'

# Connection status
curl http://localhost:9100/health | jq '{messenger: .messengerConnected, uplink: .uplinkConnected, remote: .remoteConnected}'
```

### Log Analysis

```bash
# Find errors
grep "\[ERR\]" drone.log

# Find warnings
grep "\[WRN\]" drone.log

# Find circuit breaker events
grep "circuit breaker" drone.log

# Find reconnection attempts
grep "Reconnect" drone.log

# Find shutdown events
grep "shutdown\|disposing\|Ctrl\+C" drone.log
```

### Network Diagnostics

```bash
# Check if MCP port is listening
netstat -an | grep 9100

# Check WebSocket connectivity
wscat -c ws://localhost:9100/ws

# Check HTTP file server
curl http://localhost:9100/files/
```

## FAQ

### Q: Can I run multiple agents on the same machine?

**A:** Yes, but each needs a unique:
- MCP WebSocket port (`Mcp.WebSocketPort`)
- Custody file path (`DRONE_CUSTODY_PATH`)
- Shared memory buffer path (`Uplink.BufferPath`) if using shmem transport

### Q: What happens if the custody server is down?

**A:** The `CustodyReporter` buffers records in-memory (ring buffer, default 1000 records). When the custody server reconnects, it flushes the buffer. If the buffer fills, oldest records are dropped.

### Q: How do I rotate logs?

**A:** The `CustodyAuditLogger` automatically rotates files at 10MB, keeping the last 5 files. No manual intervention needed.

### Q: Can I disable the circuit breaker?

**A:** No. The circuit breaker is integral to production resilience. However, you can tune the threshold and timeout by modifying the constructor parameters in the connector code.

### Q: How do I update the agent remotely?

**A:** Send the `update` command via Messenger. The agent:
1. Downloads the new binary to the share directory
2. Verifies SHA-256 checksum
3. Runs the update script
4. Triggers graceful shutdown
5. The update script replaces the binary and restarts

### Q: What's the minimum disk space required?

**A:** Approximately:
- Binary: 100MB
- Custody trail: ~1KB per record (plan for 10MB/day typical usage)
- Shared files: depends on your use case
- Audit logs: 10MB per file, 5 files max = 50MB

### Q: How do I monitor the agent?

**A:** Three approaches:
1. **Health endpoint** — `GET /health` returns JSON with connection status, custody chain, client count
2. **Logs** — Structured logging to stdout (capture with your log aggregator)
3. **Custody trail** — Query the custody server for audit events

### Q: The agent works locally but not behind a corporate proxy?

**A:** Configure proxy settings:
```bash
export HTTP_PROXY=http://proxy:8080
export HTTPS_PROXY=http://proxy:8080
```

For WebSocket connections, ensure the proxy supports WebSocket upgrade.

### Q: How do I debug native library issues?

**A:** Check for these log messages:
- `"Delta engine native library not available"` — Rust FFI DLLs not found
- `"WebP compression: not available"` — WebP library not loaded

Ensure `velocity_delta.dll` and `velocity_v2_ffi.dll` are in the same directory as the agent executable.

## Support

For issues not covered here:
1. Check the logs for `[ERR]` or `[WRN]` entries
2. Verify configuration with `--dry-run`
3. Check the health endpoint
4. Review the [Production Hardening](production-hardening.md) guide
