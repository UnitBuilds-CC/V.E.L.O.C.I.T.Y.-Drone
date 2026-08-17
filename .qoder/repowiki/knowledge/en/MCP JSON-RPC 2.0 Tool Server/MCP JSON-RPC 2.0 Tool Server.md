---
kind: api_server
name: MCP JSON-RPC 2.0 Tool Server
category: api_server
scope:
    - 'Drone.MCP/**'
    - 'Drone.Agent/**'
source_files:
    - Drone.MCP/McpServer.cs
    - Drone.MCP/Tools/SystemToolRegistrar.cs
    - Drone.Agent/Program.cs
---

The MCP (Model Context Protocol) server is the central tool execution hub in Velocity Drone. It implements JSON-RPC 2.0 over WebSocket and NMCP binary framing, providing a standardized interface for remote tool invocation.

**Core Components:**

1. **McpServer** — Main server class:
   - Tool registration via `RegisterTool(name, description, parameters, handler)`
   - JSON-RPC 2.0 request/response handling
   - WebSocket transport for client connections
   - NMCP buffer management for offline resilience
   - Authentication via bearer token (`DRONE_MCP_TOKEN`)

2. **SystemToolRegistrar** — Tool registration:
   - Static `RegisterAll()` method wires all platform tools
   - Receives dependencies (screen, input, process, clipboard, windows, connectors)
   - Each tool defines: name, description, parameters, async handler

3. **Tool Parameters** — Typed parameter definitions:
   - `ToolParameter(name, type, description, required)`
   - Types: `string`, `integer`, `boolean`, `object`
   - Validation against JSON schema

**JSON-RPC 2.0 Protocol:**

**Request:**
```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/call",
  "params": {
    "name": "capture_screen",
    "arguments": {}
  }
}
```

**Response:**
```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "content": [{"type": "image", "data": "base64..."}]
  }
}
```

**Error:**
```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "error": {
    "code": -32601,
    "message": "Method not found",
    "data": {"tool": "unknown_tool"}
  }
}
```

**Registered Tools:**

| Tool | Description | Parameters |
|------|-------------|------------|
| `capture_screen` | Capture screen image | None |
| `run_command` | Execute shell command | `command` (string) |
| `type_text` | Type text via keyboard | `text` (string) |
| `press_key` | Press keyboard key | `key` (string) |
| `click` | Click mouse button | `x`, `y`, `button` |
| `list_files` | List directory contents | `path` (string, optional) |
| `upload_file` | Upload file to share | `localPath`, `remotePath` |
| `download_file` | Download file from share | `remotePath`, `localPath` |
| `get_drone_status` | Get system status | None |

**Transport Layers:**

1. **WebSocket** — Primary transport:
   - Listen URL: `http://+:9100` (configurable via `DRONE_MCP_URL`)
   - One task per client connection
   - Async message handling
   - Authentication via `DRONE_MCP_TOKEN`

2. **NMCP Buffer** — File-backed resilience:
   - Path: `nmcp_mcp.bin` (configurable)
   - Size: 1MB default (configurable)
   - Persists pending requests during outages

3. **Shared Memory** — Local IPC:
   - Atomic state machine over memory-mapped files
   - Zero-copy for local processes
   - 100μs polling latency

**Authentication:**
- Bearer token via `DRONE_MCP_TOKEN` environment variable
- Token validated on WebSocket connection
- Optional — warning logged if not set

**Error Handling:**
- JSON-RPC error codes: -32700 (parse), -32600 (invalid), -32601 (not found), -32602 (invalid params), -32603 (internal)
- Tool exceptions caught and returned as error responses
- Custody trail logs all tool calls

**Integration with Custody:**
- Every tool call logged via `CustodyAuditLogger.LogToolCall()`
- Includes tool name, arguments, target system
- Cross-machine correlation for remote calls
