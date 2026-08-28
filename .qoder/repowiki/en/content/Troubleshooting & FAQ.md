# Troubleshooting & FAQ

<cite>
**Referenced Files in This Document**
- [Drone.Agent/Program.cs](file://Drone.Agent/Program.cs)
- [Drone.Agent/appsettings.json](file://Drone.Agent/appsettings.json)
- [Drone.Core/VelocityConnection.cs](file://Drone.Core/VelocityConnection.cs)
- [docs/configuration.md](file://docs/configuration.md)
</cite>

## Table of Contents
1. [Common Issues](#common-issues)
2. [Build Errors](#build-errors)
3. [Connection Issues](#connection-issues)
4. [Custody Trail Issues](#custody-trail-issues)
5. [MCP Tool Issues](#mcp-tool-issues)
6. [Platform-Specific Issues](#platform-specific-issues)
7. [Performance Issues](#performance-issues)
8. [FAQ](#faq)

## Common Issues

### Agent Won't Start

**Symptom:** Agent crashes immediately on startup
**Possible Causes:**
- Invalid configuration in `appsettings.json`
- Missing required environment variables
- Port already in use (9100 for MCP WebSocket)

**Resolution:**
```powershell
# Check configuration
dotnet run --project Drone.Agent/Drone.Agent.csproj 2>&1 | Select-String "Error"

# Check if port is in use
netstat -ano | findstr :9100

# Kill process on port
taskkill /PID <pid> /F
```

### Tray Icon Not Appearing

**Symptom:** Agent running but no tray icon
**Possible Causes:**
- Running in headless mode (`DRONE_MODE=headless`)
- WinForms not available (Linux/macOS)
- Explorer not running

**Resolution:**
- Ensure `DRONE_MODE` is not set to `headless`
- Run on Windows with Explorer running
- Check logs for WinForms initialization errors

### Commands Not Responding

**Symptom:** Messenger commands sent but no response
**Possible Causes:**
- Messenger not connected
- Command syntax error
- Tool execution timeout

**Resolution:**
```powershell
# Check connection status
# Look for "Connected to Messenger" in logs

# Verify command syntax
# Correct: "status", "run dir", "screenshot"
# Wrong: "get status", "execute dir"

# Check tool execution
# Look for "Command failed" in logs
```

## Build Errors

### .NET SDK Not Found

**Error:** `The .NET 10 SDK is not installed`
**Resolution:**
```powershell
# Download from https://dotnet.microsoft.com/download/dotnet/10.0
# Verify installation
dotnet --version
```

### Rust Build Failed

**Error:** `cargo build failed` during Drone.Native compilation
**Resolution:**
```powershell
# Option 1: Install Rust
winget install Rustlang.Rustup
rustup default stable

# Option 2: Skip Rust build
dotnet build VelocityDrone.slnx /p:SkipRust=true
```

### Circular Dependency

**Error:** `Circular dependency detected between projects`
**Resolution:**
- Check project references — `Drone.Core` should have no project references
- Services should depend on Core, not vice versa
- Verify dependency graph in architecture docs

### Missing Native DLLs

**Error:** `DllNotFoundException: velocity_delta`
**Resolution:**
```powershell
# Build Rust native code
cd Drone.Native
cargo build --release

# Copy DLLs to output directory
copy target\release\velocity_delta.dll ..\Drone.Agent\bin\Debug\net10.0\
```

## Connection Issues

### WebSocket Connection Failed

**Error:** `WebSocket connection failed: Unable to connect`
**Possible Causes:**
- Invalid URL format
- Firewall blocking port
- Server not running

**Resolution:**
```powershell
# Verify URL format
# Correct: ws://server:9000, wss://secure.example.com
# Wrong: server:9000, http://server:9000

# Check firewall
netsh advfirewall firewall show rule name=all | findstr 9000

# Test connectivity
Test-NetConnection -ComputerName server -Port 9000
```

### Shared Memory Connection Failed

**Error:** `Shared memory unavailable: Access denied`
**Possible Causes:**
- File already in use by another process
- Insufficient permissions
- Path doesn't exist

**Resolution:**
```powershell
# Check if file is locked
handle.exe nmcp_buffer.bin

# Delete stale buffer file
Remove-Item $env:TEMP\nmcp_buffer.bin -Force

# Run as administrator if needed
```

### Auto-Reconnect Not Working

**Symptom:** Connection lost and not reconnecting
**Possible Causes:**
- `AutoReconnect` set to false
- Max reconnect attempts reached
- Server rejecting connections

**Resolution:**
```json
// appsettings.json
{
  "Uplink": {
    "AutoReconnect": true,
    "MaxReconnectAttempts": 10
  }
}
```

## Custody Trail Issues

### Chain Validation Failed

**Error:** `Custody chain validation failed`
**Possible Causes:**
- Records modified after creation (tampering)
- Incomplete record write during crash
- Manual file editing

**Resolution:**
```powershell
# Load persisted records (auto-recovery)
# CustodyAuditLogger.LoadPersistedRecords() handles this

# Check for tampering
# Compare ContentHash with computed hash

# Restore from backup
Copy-Item custody-backup.jsonl custody.jsonl
```

### CustodyReporter Not Sending

**Symptom:** Records logged locally but not sent to server
**Possible Causes:**
- `DRONE_CUSTODY_SERVER` not set
- Server not running
- Network connectivity issues

**Resolution:**
```powershell
# Verify environment variable
echo $env:DRONE_CUSTODY_SERVER

# Check server health
Invoke-RestMethod http://localhost:5050/health

# Check reporter logs
# Look for "CustodyReporter started" and "batch sent"
```

### Sequence Gap in Logs

**Symptom:** Missing sequence numbers in custody log
**Possible Causes:**
- Multiple agents writing to same file
- Concurrent writes without proper locking
- Manual file editing

**Resolution:**
- Ensure each drone has unique `DRONE_ID`
- Check for multiple agent instances
- Verify `CustodyChain` locking in code

## MCP Tool Issues

### Tool Not Found

**Error:** `Method not found: tools/call`
**Possible Causes:**
- Tool not registered in `SystemToolRegistrar`
- Tool name mismatch (case-sensitive)
- MCP server not initialized

**Resolution:**
```csharp
// Verify tool registration in SystemToolRegistrar.RegisterAll()
server.RegisterTool("my_tool", ...);

// Check tool list
var tools = mcpServer.GetToolList();
// Tool names are case-sensitive
```

### Tool Execution Timeout

**Symptom:** Tool call hangs indefinitely
**Possible Causes:**
- Long-running operation without cancellation
- Deadlock in async code
- External service timeout

**Resolution:**
```csharp
// Add timeout to tool handler
handler: async (args) =>
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var result = await DoWorkAsync(cts.Token);
    return JsonDocument.Parse(JsonSerializer.Serialize(result));
}
```

### Tool Returns Invalid JSON

**Error:** `JSON parse error in tool result`
**Possible Causes:**
- Handler returning non-JSON string
- Serialization error
- Encoding issues

**Resolution:**
```csharp
// Always return JsonDocument
return JsonDocument.Parse(JsonSerializer.Serialize(new { success = true }));

// Don't return raw strings
// BAD: return "success";
// GOOD: return JsonDocument.Parse(@"{""result"":""success""}");
```

## Platform-Specific Issues

### Screen Capture Failed (Linux)

**Error:** `Screen capture unavailable: scrot not found`
**Resolution:**
```bash
# Install scrot
sudo apt-get install scrot

# Or use import (ImageMagick)
sudo apt-get install imagemagick
```

### Input Simulation Failed (macOS)

**Error:** `Input simulation unavailable: cliclick not found`
**Resolution:**
```bash
# Install cliclick
brew install cliclick
```

### Clipboard Failed (Linux)

**Error:** `Clipboard manager unavailable: xclip not found`
**Resolution:**
```bash
# Install xclip
sudo apt-get install xclip

# Or xsel
sudo apt-get install xsel
```

## Performance Issues

### High Memory Usage

**Symptom:** Agent consuming excessive memory
**Possible Causes:**
- Custody ring buffer too large
- Screen captures not disposed
- Memory leak in connectors

**Resolution:**
```csharp
// Reduce ring buffer size
// CustodyAuditLogger uses 1000 records by default

// Dispose screen captures
using var screenshot = await screen.CaptureScreenAsync();

// Profile memory
// Use dotnet-counters or Visual Studio Profiler
```

### Slow WebSocket Performance

**Symptom:** High latency in tool responses
**Possible Causes:**
- Large payloads
- Concurrent sends blocking
- Slow network

**Resolution:**
```csharp
// Increase send timeout
private static readonly TimeSpan WsSendTimeout = TimeSpan.FromSeconds(30);

// Use SemaphoreSlim to prevent concurrent send blocking
await _wsSendLock.WaitAsync(TimeSpan.FromSeconds(10));
```

### CPU Spike on Startup

**Symptom:** High CPU usage during initialization
**Possible Causes:**
- Loading large custody log
- Initializing native DLLs
- Multiple connection attempts

**Resolution:**
- Normal during startup — should settle after 10-30 seconds
- Reduce custody log size with daily rotation
- Stagger connection attempts

## FAQ

### Q: How do I run the agent in headless mode?

A: Set `DRONE_MODE=headless` environment variable or configure in `appsettings.json`:
```json
{ "Drone": { "Mode": "Headless" } }
```

### Q: Can I run multiple agents on the same machine?

A: Yes, but each needs a unique `DRONE_ID` and different MCP ports:
```powershell
# Agent 1
$env:DRONE_ID = "agent-1"; $env:DRONE_MCP_URL = "http://+:9100"

# Agent 2
$env:DRONE_ID = "agent-2"; $env:DRONE_MCP_URL = "http://+:9101"
```

### Q: How do I update the agent remotely?

A: Send the `update` command via Messenger:
```
update
```
The agent will download from Share and restart automatically.

### Q: Where are the custody logs stored?

A: Default location: `%APPDATA%\velocity-drone\custody\drone-custody.jsonl`
Override with `DRONE_CUSTODY_PATH` environment variable.

### Q: How do I enable debug logging?

A: Set `DRONE_LOG_LEVEL=Debug` or configure in `appsettings.json`:
```json
{ "Logging": { "LogLevel": { "Default": "Debug" } } }
```

### Q: Can I disable the custody trail?

A: No — the custody trail is mandatory for security and compliance. All actions must be auditable.

### Q: How do I backup the custody chain?

A: Copy the custody log file:
```powershell
Copy-Item $env:DRONE_CUSTODY_PATH "C:\Backups\custody-$(Get-Date -Format 'yyyyMMdd').jsonl"
```

### Q: What happens if the CustodyServer is down?

A: Records are stored locally in JSON-lines file. When server reconnects, `CustodyReporter` sends pending records automatically.

### Q: How do I test tools without connecting to Messenger?

A: Use the MCP WebSocket directly:
```powershell
# Connect to ws://localhost:9100
# Send JSON-RPC request
{"jsonrpc":"2.0","id":1,"method":"tools/list"}
```

### Q: Can I add custom tools?

A: Yes — see the `mcp-tool-writer` skill for detailed instructions on adding new MCP tools.
