# Configuration Reference

**Project:** Velocity Drone  
**Config file:** `Drone.Agent/appsettings.json`

---

## Overview

Configuration is resolved in priority order (highest first):

1. **Environment variables** — Override any setting at runtime
2. **appsettings.json** — Persistent configuration file
3. **Defaults** — Hardcoded in `DroneConfig`

## appsettings.json Structure

```json
{
  "Drone": {
    "DroneId": "Drone",
    "Mode": "Full",
    "Uplink": { ... },
    "Messenger": { ... },
    "Share": { ... },
    "Remote": { ... },
    "Autonomy": { ... },
    "Mcp": { ... }
  }
}
```

## Core Settings

| Setting | Env Variable | Default | Description |
|---------|-------------|---------|-------------|
| `DroneId` | `DRONE_ID` | `"Drone"` | Unique identity for this agent. Used in custody trail, event IDs, and log files. |
| `Mode` | `DRONE_MODE` | `"Full"` | Operating mode: `Full` (tray app + all features) or `Headless` (no GUI, for Docker/VMs). |

## Uplink (Velocity WebSocket Connection)

Connects the drone to the Velocity uplink server for remote control.

```json
"Uplink": {
  "Transport": "auto",
  "WebSocketUrl": "",
  "BufferPath": "nmcp_drone.bin",
  "BufferSize": 4194304,
  "AutoReconnect": true,
  "MaxReconnectAttempts": 10
}
```

| Setting | Env Variable | Default | Description |
|---------|-------------|---------|-------------|
| `Transport` | — | `"auto"` | Transport selection: `auto`, `websocket`, `sharedmemory`. |
| `WebSocketUrl` | `DRONE_WS_URL` | `""` | WebSocket URL for uplink connection. Empty = disabled. |
| `BufferPath` | — | `"nmcp_drone.bin"` | File-backed NMCP buffer path for offline resilience. |
| `BufferSize` | — | `4194304` (4 MB) | Maximum buffer size in bytes. |
| `AutoReconnect` | — | `true` | Automatically reconnect on disconnect. |
| `MaxReconnectAttempts` | — | `10` | Maximum reconnection attempts before giving up. |

## Messenger (Command Reception)

Connects to Velocity Messenger for receiving commands via direct messages.

```json
"Messenger": {
  "ServerUrl": "",
  "ConnectionSecret": "",
  "AutoReconnect": true
}
```

| Setting | Env Variable | Default | Description |
|---------|-------------|---------|-------------|
| `ServerUrl` | — | `""` | Messenger server WebSocket URL. Empty = disabled. |
| `ConnectionSecret` | — | `""` | Authentication secret for the Messenger connection. |
| `AutoReconnect` | — | `true` | Automatically reconnect on disconnect. |

## Share (File Sharing)

Connects to the Share server for file upload/download operations.

```json
"Share": {
  "ServerUrl": "",
  "AdminApiKey": "",
  "WebSocketToken": ""
}
```

| Setting | Env Variable | Default | Description |
|---------|-------------|---------|-------------|
| `ServerUrl` | — | `""` | Share server URL. Empty = disabled. |
| `AdminApiKey` | — | `""` | Admin API key for file operations. |
| `WebSocketToken` | — | `""` | WebSocket authentication token. |

## Remote (NMCP Remote Control)

Accepts remote NMCP connections for tool execution from other agents.

```json
"Remote": {
  "ServerUrl": "",
  "ApiKey": ""
}
```

| Setting | Env Variable | Default | Description |
|---------|-------------|---------|-------------|
| `ServerUrl` | — | `""` | Remote server WebSocket URL. Empty = disabled. |
| `ApiKey` | — | `""` | API key for authentication. |

## Autonomy (Rule Engine)

Controls autonomous behavior based on trigger conditions.

```json
"Autonomy": {
  "Enabled": true,
  "RulesPath": "rules.json",
  "ScreenMonitorIntervalSec": 0,
  "SystemMetricsIntervalSec": 30,
  "ProcessMonitorIntervalSec": 10,
  "ScheduledTaskPollSec": 60
}
```

| Setting | Env Variable | Default | Description |
|---------|-------------|---------|-------------|
| `Enabled` | — | `true` | Enable/disable autonomous behavior. |
| `RulesPath` | — | `"rules.json"` | Path to behavior rules JSON file. |
| `ScreenMonitorIntervalSec` | — | `0` | Screen change monitoring interval. `0` = disabled. |
| `SystemMetricsIntervalSec` | — | `30` | System metrics collection interval (CPU, memory, disk). |
| `ProcessMonitorIntervalSec` | — | `10` | Process list monitoring interval. |
| `ScheduledTaskPollSec` | — | `60` | Scheduled task polling interval. |

## MCP (Model Context Protocol)

MCP WebSocket server configuration.

```json
"Mcp": {
  "BufferPath": "nmcp_mcp.bin",
  "BufferSize": 1048576
}
```

| Setting | Env Variable | Default | Description |
|---------|-------------|---------|-------------|
| `BufferPath` | — | `"nmcp_mcp.bin"` | File-backed NMCP buffer for MCP clients. |
| `BufferSize` | — | `1048576` (1 MB) | Maximum MCP buffer size in bytes. |
| — | `DRONE_MCP_URL` | `http://+:9100` | MCP WebSocket listen URL. |
| — | `DRONE_MCP_TOKEN` | `""` | MCP authentication token. |
| — | `DRONE_MCP_TLS` | `0` | Enable TLS for MCP (`1` = enabled). |

## Custody Trail

| Setting | Env Variable | Default | Description |
|---------|-------------|---------|-------------|
| — | `DRONE_CUSTODY_PATH` | `./custody/drone-custody.jsonl` | Local custody log file path. |
| — | `DRONE_CUSTODY_SERVER` | _(none)_ | CustodyServer URL for streaming. If set, `CustodyReporter` starts automatically. |

## CustodyServer (Standalone)

| Setting | Env Variable | Default | Description |
|---------|-------------|---------|-------------|
| — | `CUSTODY_STORAGE_PATH` | `./custody-data` | Server-side storage directory for custody records. |
| — | `CUSTODY_LISTEN_URL` | `http://+:5010/` | CustodyServer HTTP/WebSocket listen URL. |

## Miscellaneous

| Setting | Env Variable | Default | Description |
|---------|-------------|---------|-------------|
| — | `DRONE_AUDIT_LOG` | `/data/audit/drone-audit.jsonl` | General audit log path (non-custody). |
| — | `DRONE_ALLOWED_PATHS` | `/data` | Comma-separated list of allowed file system paths. |
| — | `DRONE_SHARE_PATH` | _(none)_ | Shared files storage path. |

## Docker Environment

When running in Docker (`DRONE_MODE=headless`), these are the default environment variables set in the Dockerfile:

```dockerfile
ENV DRONE_MODE=headless
ENV DRONE_ID=Drone
ENV DRONE_MCP_URL=http://0.0.0.0:9100
ENV DRONE_MCP_TOKEN=""
ENV DRONE_MCP_TLS=0
ENV DRONE_AUDIT_LOG=/data/audit/drone-audit.jsonl
ENV DRONE_WS_URL=""
ENV DRONE_ALLOWED_PATHS=/data
ENV Drone__Uplink__Transport=auto
ENV Drone__Uplink__BufferSize=4194304
```

Override at runtime:
```bash
docker run -d \
  -e DRONE_MODE=headless \
  -e DRONE_MCP_URL=http://0.0.0.0:9100 \
  -e DRONE_MCP_TOKEN=your-secret-token \
  -e DRONE_WS_URL=wss://your-host/ws/drone \
  -e DRONE_ALLOWED_PATHS=/data \
  -p 9100:9100 \
  velocity-drone
```

## Configuration Validation

`DroneConfig` validates settings on load:
- `DroneId` must not be empty
- `Mode` must be `Full` or `Headless` (case-insensitive)
- `Uplink.BufferSize` must be > 0
- `Mcp.BufferSize` must be > 0
- URLs must be valid if provided

Invalid configuration throws an exception at startup — the agent will not start with bad config.
