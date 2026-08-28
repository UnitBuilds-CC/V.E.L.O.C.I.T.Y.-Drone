# User Guide

**Project:** Velocity Drone  
**Audience:** End users, operators, and integrators

---

## Table of Contents

1. [Getting Started](#getting-started)
2. [Messenger Commands](#messenger-commands)
3. [MCP Tools Reference](#mcp-tools-reference)
4. [Common Workflows](#common-workflows)
5. [Autonomy Rules](#autonomy-rules)
6. [Day-to-Day Operations](#day-to-day-operations)
7. [Integration Examples](#integration-examples)

---

## Getting Started

### Prerequisites

- .NET 10 preview SDK (or Docker)
- A Windows machine (for tray app) or any OS (for Docker headless)
- Optional: Velocity Messenger server, Share server, Remote server

### First-Time Setup

#### 1. Install

**Docker (recommended for servers):**
```bash
docker build -t velocity-drone:latest .
docker run -d --name drone \
  -e DRONE_MODE=headless \
  -e DRONE_MCP_TOKEN=your-secret-token \
  -p 9100:9100 \
  velocity-drone:latest
```

**Windows tray app:**
```bash
dotnet publish Drone.Agent/Drone.Agent.csproj -c Release -o ./publish
# Double-click velocity-drone.exe
```

#### 2. Configure

Edit `appsettings.json` or set environment variables:

```bash
# Minimum config (MCP server only, no external connections)
export DRONE_MCP_TOKEN=my-secret-token

# Full config (all services)
export DRONE_MCP_TOKEN=my-secret-token
export DRONE_WS_URL=wss://uplink.example.com/ws/drone
export MESSENGER_SERVER_URL=https://messenger.example.com
export MESSENGER_SECRET=your-connection-secret
export SHARE_SERVER_URL=http://share.example.com:5003
export SHARE_ADMIN_API_KEY=your-api-key
export REMOTE_SERVER_URL=wss://remote.example.com/nmcp
export REMOTE_API_KEY=your-api-key
```

#### 3. Verify

```bash
# Check health
curl http://localhost:9100/health

# Expected response:
{
  "status": "healthy",
  "uptimeSec": 42,
  "connectedClients": 0,
  "toolsAvailable": 30,
  "messengerConnected": false,
  "uplinkConnected": false,
  "remoteConnected": false
}
```

#### 4. Connect an MCP Client

Use any MCP-compatible client to connect:

```bash
# Using wscat for testing
wscat -c ws://localhost:9100/ws -H "Authorization: Bearer your-secret-token"

# Send an initialize request
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}
```

---

## Messenger Commands

Send these as direct messages to the drone via Velocity Messenger:

### System Commands

| Command | Example | Description |
|---------|---------|-------------|
| `status` | `status` | Get drone status (system info, connections, uptime, memory, capabilities) |
| `screenshot` | `screenshot` | Capture the screen and return a base64 PNG image |
| `benchmark` | `benchmark` | Run performance benchmarks (NDA serialization, delta frames, WebP compression) |
| `update` | `update` | Self-update from shared directory (downloads new binary, verifies SHA-256, restarts) |

### Input Commands

| Command | Example | Description |
|---------|---------|-------------|
| `type <text>` | `type Hello World` | Type text via simulated keystrokes |
| `key <key>` | `key enter` | Press a key (e.g., `enter`, `ctrl+c`, `alt+f4`, `tab`) |
| `click <x> <y> [btn]` | `click 500 300 left` | Click at coordinates. Button: `left` (default) or `right` |

### File Commands

| Command | Example | Description |
|---------|---------|-------------|
| `list [path]` | `list /data` | List files in shared directory |
| `upload <local> <remote>` | `upload C:\file.txt /shared/file.txt` | Upload file to shared storage |
| `download <remote> <local>` | `download /shared/file.txt C:\file.txt` | Download file from shared storage |

### Shell Commands

| Command | Example | Description |
|---------|---------|-------------|
| `run <command>` | `run dir` | Execute a shell command (60s timeout, blocked commands: format, del /s, rm -rf, mkfs, dd if=) |

**Security:** Commands containing `| & ; \` $ ( )` are rejected to prevent shell injection.

### Examples

```
# Check drone status
> status
Drone Status: {"agent":"velocity-drone","version":"1.0.0","uptimeSec":3600,"memoryMB":45,...}

# Take a screenshot
> screenshot
Screenshot: {"image":"iVBORw0KGgoAAAANSUhEUgAA...","format":"png"}

# Type text
> type Hello, this is automated text input

# Press a key combination
> key ctrl+c

# Click at coordinates
> click 500 300 left

# Run a command
> run hostname
DESKTOP-ABC123

# List files
> list /data
Files: ["report.pdf", "config.json", "logs/"]

# Upload a file
> upload C:\report.pdf /shared/report.pdf
Upload complete: report.pdf
```

---

## MCP Tools Reference

Connect via any MCP client and call these tools using JSON-RPC 2.0.

### Screen Tools

| Tool | Parameters | Returns | Description |
|------|-----------|---------|-------------|
| `capture_screen` | — | `{image, format}` | Capture full screen as base64 PNG |
| `capture_window` | `{title}` | `{image, format, title}` | Capture window by title (falls back to full screen) |
| `get_pixel_color` | `{x, y}` | `{r, g, b}` | Get RGB color at pixel coordinates |
| `get_screen_size` | — | `{width, height}` | Get screen resolution |
| `find_image_on_screen` | — | `{found, x, y, confidence}` | Template matching (requires AI vision backend) |

### Input Tools

| Tool | Parameters | Returns | Description |
|------|-----------|---------|-------------|
| `type_text` | `{text}` | `{success}` | Type text via simulated keystrokes |
| `press_key` | `{key}` | `{success}` | Press a virtual key (e.g., "Enter", "Escape", "Tab") |
| `move_mouse` | `{x, y}` | `{success}` | Move mouse cursor to coordinates |
| `click` | `{x, y, button?}` | `{success}` | Click at coordinates. Button: "left" (default) or "right" |
| `drag` | `{fromX, fromY, toX, toY}` | `{success}` | Drag from one point to another |
| `scroll` | `{deltaX?, deltaY?}` | `{success}` | Scroll by delta amount |

### System Tools

| Tool | Parameters | Returns | Description |
|------|-----------|---------|-------------|
| `run_command` | `{command, args?, workingDir?}` | `{exitCode, stdout, stderr, durationMs}` | Execute shell command (60s timeout) |
| `list_processes` | — | `{count, top50}` | List top 50 processes by memory |
| `kill_process` | `{processId}` | `{success, processId}` | Kill a process by PID |
| `get_system_info` | — | `{hostname, os, cpu, memoryMB, ...}` | Get system information |
| `get_drone_status` | — | Full status object | Comprehensive drone status |
| `launch_app` | `{app, args?}` | `{launched, app}` | Launch an application |

### File Tools

| Tool | Parameters | Returns | Description |
|------|-----------|---------|-------------|
| `read_file` | `{path}` | `{content, path}` | Read file contents (path must be in allowed directories) |
| `write_file` | `{path, content}` | `{success, path}` | Write file contents (creates directories) |
| `list_dir` | `{path?}` | `{path, count, entries}` | List directory contents |
| `find_file` | `{path?, pattern?}` | `{count, files}` | Find files by pattern (recursive, max 100 results) |

### Clipboard Tools

| Tool | Parameters | Returns | Description |
|------|-----------|---------|-------------|
| `clipboard_get` | — | `{text, length}` | Get clipboard text content |
| `clipboard_set` | `{text}` | `{success}` | Set clipboard text content |

### Window Tools

| Tool | Parameters | Returns | Description |
|------|-----------|---------|-------------|
| `list_windows` | — | `{count, windows}` | List all visible windows |
| `focus_window` | `{title}` | `{success, title}` | Focus window by title (partial match) |
| `close_app` | `{title}` | `{success}` | Close window by title |
| `get_app_state` | `{title}` | `{found, title?, processName?, ...}` | Get window state |

### Messenger Tools (requires Messenger connection)

| Tool | Parameters | Returns | Description |
|------|-----------|---------|-------------|
| `send_message` | `{to, content}` | `{success}` | Send direct message |
| `send_group_message` | `{groupId, content}` | `{success}` | Send group message |
| `get_contacts` | — | `{status, message}` | Request contact list |
| `upload_media` | `{filePath, mediaType?}` | `{success, fileName}` | Upload media file |
| `download_media` | `{url, localPath}` | `{success, localPath}` | Download media file |

### Share Tools (requires Share connection)

| Tool | Parameters | Returns | Description |
|------|-----------|---------|-------------|
| `upload_file` | `{localPath, remotePath}` | `{success}` | Upload file to share server |
| `download_file` | `{remotePath, localPath}` | `{success}` | Download file from share server |
| `list_files` | `{path?}` | `{count, files}` | List files on share server |
| `sync_folder` | `{localFolder, remoteFolder}` | `{success, uploaded}` | Sync local folder to share |
| `delete_file` | `{path}` | `{success}` | Delete file on share server |

### Remote Tools (requires Remote connection)

| Tool | Parameters | Returns | Description |
|------|-----------|---------|-------------|
| `get_screen_stream` | `{quality?, maxWidth?}` | `{success, message}` | Request screen stream from remote |
| `send_input` | `{inputType, data}` | `{success}` | Send input to remote |
| `get_hosts` | — | `{status, message}` | Query available remote hosts |
| `get_address_book` | — | `{status, message}` | Query remote address book |

### Example: MCP Tool Call

```json
// Request
{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"run_command","arguments":{"command":"hostname"}}}

// Response
{"jsonrpc":"2.0","id":1,"result":{"content":[{"type":"text","text":"{\"exitCode\":0,\"stdout\":\"DESKTOP-ABC123\",\"stderr\":\"\",\"durationMs\":15}"}]}}
```

---

## Common Workflows

### Remote Desktop Control

1. **Connect an MCP client** to the drone's WebSocket endpoint
2. **Capture the screen** to see what's on the remote machine:
   ```json
   {"name":"capture_screen","arguments":{}}
   ```
3. **Interact** using input tools:
   ```json
   {"name":"click","arguments":{"x":500,"y":300}}
   {"name":"type_text","arguments":{"text":"Hello World"}}
   {"name":"press_key","arguments":{"key":"Enter"}}
   ```
4. **Monitor windows**:
   ```json
   {"name":"list_windows","arguments":{}}
   {"name":"focus_window","arguments":{"title":"Notepad"}}
   ```

### File Sharing

1. **Upload files** to the share server:
   ```json
   {"name":"upload_file","arguments":{"localPath":"C:\\report.pdf","remotePath":"/shared/report.pdf"}}
   ```
2. **List shared files**:
   ```json
   {"name":"list_files","arguments":{}}
   ```
3. **Download files** from the share server:
   ```json
   {"name":"download_file","arguments":{"remotePath":"/shared/report.pdf","localPath":"C:\\downloads\\report.pdf"}}
   ```
4. **Sync a folder**:
   ```json
   {"name":"sync_folder","arguments":{"localFolder":"C:\\project","remoteFolder":"/shared/project"}}
   ```

### Automated Monitoring

1. **Set up autonomy rules** to monitor system health:
   ```json
   [
     {
       "Name": "HighMemoryAlert",
       "Trigger": "system_metrics",
       "Condition": "memoryMB > 8000",
       "Action": "run_command",
       "ActionParams": {"command": "echo", "args": "High memory detected"},
       "Enabled": true
     }
   ]
   ```
2. **Check drone status** periodically via MCP:
   ```json
   {"name":"get_drone_status","arguments":{}}
   ```
3. **Review custody trail** for audit:
   ```bash
   curl http://custody-server:5010/custody?drone=Drone&from=2024-01-01
   ```

### Self-Update

1. **Place new binary** in the share directory:
   ```bash
   # On the share server or local share path
   cp velocity-drone-new.exe /share/velocity-drone-new.exe
   ```
2. **Send update command** via Messenger:
   ```
   > update
   ```
3. The agent will:
   - Download the new binary
   - Verify SHA-256 checksum
   - Run the update script
   - Trigger graceful shutdown
   - The update script replaces the binary and restarts

---

## Autonomy Rules

Autonomy rules define automated behavior based on events. Rules are loaded from `rules.json` (configurable via `Autonomy.RulesPath`).

### Rule Structure

```json
{
  "Name": "RuleName",
  "Trigger": "event_type",
  "Condition": "field > value",
  "Action": "action_type",
  "ActionParams": {"key": "value"},
  "Enabled": true
}
```

### Fields

| Field | Description |
|-------|-------------|
| `Name` | Unique rule identifier |
| `Trigger` | Event type to match, or `*` for all events |
| `Condition` | Optional: `field > value`, `field < value`, `field == value`, `field != value` |
| `Action` | Action to execute: `log`, `run_command`, `send_message`, `reply`, `uplink_notify` |
| `ActionParams` | Parameters for the action |
| `Enabled` | Whether the rule is active |

### Available Actions

| Action | Parameters | Description |
|--------|-----------|-------------|
| `log` | `{level?}` | Log the event (level: info, warning, error) |
| `run_command` | `{command, args?, timeout?}` | Execute a shell command (default timeout: 30s) |
| `send_message` | `{to, content}` | Send a Messenger message |
| `reply` | `{content}` | Auto-reply to the triggering message |
| `uplink_notify` | `{eventType, data}` | Send notification to uplink |

### Example Rules

**Log all events:**
```json
[
  {"Name": "LogAll", "Trigger": "*", "Action": "log", "Enabled": true}
]
```

**Alert on high memory:**
```json
[
  {
    "Name": "HighMemoryAlert",
    "Trigger": "system_metrics",
    "Condition": "memoryMB > 8000",
    "Action": "send_message",
    "ActionParams": {"to": "admin", "content": "High memory usage detected!"},
    "Enabled": true
  }
]
```

**Auto-reply to ping:**
```json
[
  {
    "Name": "PingResponder",
    "Trigger": "message_received",
    "Condition": "content == ping",
    "Action": "reply",
    "ActionParams": {"content": "pong"},
    "Enabled": true
  }
]
```

**Run cleanup on low disk:**
```json
[
  {
    "Name": "DiskCleanup",
    "Trigger": "system_metrics",
    "Condition": "diskFreeGB < 10",
    "Action": "run_command",
    "ActionParams": {"command": "clean-temp.bat", "timeout": 60},
    "Enabled": true
  }
]
```

---

## Day-to-Day Operations

### Monitoring Health

```bash
# Basic health check
curl http://localhost:9100/health

# Check connection status
curl -s http://localhost:9100/health | jq '{messenger: .messengerConnected, uplink: .uplinkConnected, remote: .remoteConnected}'

# Check custody chain integrity
curl -s http://localhost:9100/health | jq '{seq: .custodySequence, hash: .custodyHash}'
```

### Viewing Logs

Logs are written to stdout. Capture them with your preferred log aggregator:

```bash
# Docker
docker logs -f drone

# Windows (if running from command line)
# Logs appear in the console window

# Redirect to file
dotnet run --project Drone.Agent/Drone.Agent.csproj > drone.log 2>&1
```

### Graceful Shutdown

```bash
# Docker
docker stop drone  # Sends SIGTERM, waits up to DRONE_SHUTDOWN_TIMEOUT (default: 15s)

# Windows
# Press Ctrl+C in the console window
```

The shutdown sequence disposes all components in order, flushing pending custody records and closing WebSocket connections.

### Updating Configuration

Configuration is loaded at startup. To change settings:

1. Update `appsettings.json` or environment variables
2. Restart the agent

```bash
# Docker
docker restart drone

# Windows
# Close the tray app and restart
```

### Checking the Custody Trail

```bash
# Query custody records
curl "http://custody-server:5010/custody?drone=Drone&from=2024-01-01T00:00:00Z"

# Stream real-time custody events
wscat -c ws://custody-server:5010/ws
```

### Troubleshooting

See [Troubleshooting Guide](troubleshooting.md) for common issues and diagnostics.

---

## Integration Examples

### Connecting from Claude Desktop

Add to your Claude Desktop MCP configuration:

```json
{
  "mcpServers": {
    "velocity-drone": {
      "url": "ws://localhost:9100/ws",
      "headers": {
        "Authorization": "Bearer your-secret-token"
      }
    }
  }
}
```

### Connecting from Cursor

Add to your Cursor MCP settings:

```json
{
  "mcpServers": {
    "velocity-drone": {
      "url": "ws://localhost:9100/ws",
      "headers": {
        "Authorization": "Bearer your-secret-token"
      }
    }
  }
}
```

### Python Script Example

```python
import asyncio
import websockets
import json

async def call_tool(ws_url, token, tool_name, arguments):
    async with websockets.connect(ws_url, extra_headers={"Authorization": f"Bearer {token}"}) as ws:
        # Initialize
        await ws.send(json.dumps({
            "jsonrpc": "2.0", "id": 1, "method": "initialize",
            "params": {"protocolVersion": "2024-11-05", "capabilities": {},
                       "clientInfo": {"name": "python-script", "version": "1.0"}}
        }))
        await ws.recv()
        
        # Call tool
        await ws.send(json.dumps({
            "jsonrpc": "2.0", "id": 2, "method": "tools/call",
            "params": {"name": tool_name, "arguments": arguments}
        }))
        response = await ws.recv()
        return json.loads(response)

# Example: Take a screenshot
result = asyncio.run(call_tool(
    "ws://localhost:9100/ws",
    "your-secret-token",
    "capture_screen",
    {}
))
print(result)
```

### PowerShell Script Example

```powershell
$wsUrl = "ws://localhost:9100/ws"
$token = "your-secret-token"

# Using Invoke-WebRequest for health check
$health = Invoke-RestMethod -Uri "http://localhost:9100/health"
Write-Host "Status: $($health.status)"
Write-Host "Uptime: $($health.uptimeSec) seconds"
Write-Host "Connections: messenger=$($health.messengerConnected) uplink=$($health.uplinkConnected)"
```

### Bash Automation

```bash
#!/bin/bash
DRONE_URL="http://localhost:9100"
TOKEN="your-secret-token"

# Check health
health=$(curl -sf "$DRONE_URL/health")
status=$(echo "$health" | jq -r '.status')

if [ "$status" != "healthy" ]; then
    echo "Drone is not healthy: $status"
    exit 1
fi

# Get drone status via MCP (requires wscat or similar)
echo "Drone is healthy. Uptime: $(echo "$health" | jq -r '.uptimeSec')s"
```

---

## Reference

| Document | Description |
|----------|-------------|
| [Architecture](architecture.md) | Module map, dependency graph, data flow, threading model |
| [Configuration](configuration.md) | Every setting, defaults, environment variables |
| [Custody Trail](custody-trail.md) | Full custody system architecture, API reference, security |
| [NMCP Protocol](nmcp-protocol.md) | Wire format specification, frame types, connection lifecycle |
| [Deployment](deployment.md) | Docker, Windows, CustodyServer, Azure, systemd |
| [Production Hardening](production-hardening.md) | Circuit breaker, graceful shutdown, security features |
| [Troubleshooting](troubleshooting.md) | Common issues, diagnostics, FAQ |
| [Development](development.md) | Building, testing, conventions, debugging, adding tools |
