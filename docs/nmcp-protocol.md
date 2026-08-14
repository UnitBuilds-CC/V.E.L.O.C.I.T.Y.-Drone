# NMCP Protocol Specification

**Version:** 1.0  
**Status:** Stable  
**Scope:** Velocity Drone agent communication protocol

---

## Overview

NMCP (Network Machine Control Protocol) is a binary-framed protocol for structured communication between Velocity Drone agents and their connected systems. It wraps JSON-RPC 2.0 and binary payloads in a compact 16-byte header format, designed for cross-platform compatibility and high-throughput streaming.

NMCP is the **unified protocol standard** across all Velocity projects.

## Design Goals

1. **Binary efficiency** — Fixed 16-byte header avoids text parsing overhead on hot paths
2. **Cross-platform** — Big-endian byte order for universal compatibility
3. **Extensible** — Frame type registry allows adding new message types without breaking existing clients
4. **Corruption-resistant** — Magic number validation + max payload size guard
5. **Streaming-friendly** — Supports multi-frame sequences over persistent connections

## Frame Format

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                         Magic (0x564E4D43)                    |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                          FrameType                            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                         PayloadLen                            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                         SequenceId                            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                          Payload ...                          |
```

### Header Fields

| Field | Offset | Size | Description |
|-------|--------|------|-------------|
| `Magic` | 0 | 4 bytes | Always `0x564E4D43` ("VNMC" in ASCII). Frames with wrong magic are silently dropped. |
| `FrameType` | 4 | 4 bytes | Message type identifier (see registry below). |
| `PayloadLen` | 8 | 4 bytes | Length of payload in bytes. Max: 16 MB (16,777,216 bytes). Frames exceeding this are rejected as corrupted. |
| `SequenceId` | 12 | 4 bytes | Monotonic sequence for request/response correlation. Sender-assigned. |

All fields are **big-endian** (network byte order).

### Wire Format

```
Total frame size = 16 (header) + PayloadLen bytes

Example: JSON-RPC request with 42-byte payload
  Header:  56 4E 4D 43  00 00 00 01  00 00 00 2A  00 00 00 01
           ^Magic        ^Type=1      ^Len=42      ^Seq=1
  Payload: {"jsonrpc":"2.0","method":"list","id":1}
```

## Frame Type Registry

### JSON-RPC Frames (1-9)

| Type | Code | Direction | Description |
|------|------|-----------|-------------|
| `JsonRpcRequest` | 1 | Bidirectional | JSON-RPC 2.0 request. Expects a response with matching `SequenceId`. |
| `JsonRpcResponse` | 2 | Bidirectional | JSON-RPC 2.0 response. `SequenceId` matches the request. |
| `JsonRpcNotification` | 3 | Bidirectional | JSON-RPC 2.0 notification. No response expected. |

### Tool Execution Frames (10-19)

| Type | Code | Direction | Description |
|------|------|-----------|-------------|
| `ToolCall` | 10 | Client → Agent | Invoke a registered tool. Payload: JSON `{tool, params}`. |
| `ToolResult` | 11 | Agent → Client | Tool execution result. Payload: JSON `{result, error}`. |

### Screen/Input Frames (20-29)

| Type | Code | Direction | Description |
|------|------|-----------|-------------|
| `ScreenCapture` | 20 | Agent → Client | Screen capture data. Payload: image bytes (PNG/WebP). |
| `InputEvent` | 21 | Client → Agent | Simulated input event. Payload: JSON `{type, x, y, key}`. |

### System Frames (30-39)

| Type | Code | Direction | Description |
|------|------|-----------|-------------|
| `SystemMetrics` | 30 | Agent → Client | System resource metrics. Payload: JSON `{cpu, mem, disk}`. |

### Custody Trail Frames (40-49)

| Type | Code | Direction | Description |
|------|------|-----------|-------------|
| `CustodyReport` | 40 | Agent → Server | Batch of hash-chained custody records. Payload: JSON array of `CustodyRecord`. |
| `CustodyQuery` | 41 | Client → Server | Query request. Payload: JSON `{droneId, from, to, correlationId, eventType}`. |
| `CustodyTimeline` | 42 | Server → Client | Query response. Payload: JSON `{count, records}`. |
| `CustodyStream` | 43 | Server → Clients | Real-time broadcast. Payload: JSON array of new `CustodyRecord`. |

### Control Frames (100+)

| Type | Code | Direction | Description |
|------|------|-----------|-------------|
| `Heartbeat` | 100 | Bidirectional | Keep-alive ping/pong. |
| `Handshake` | 101 | Bidirectional | Connection establishment. |

## Custody Frame Payloads

### CustodyReport (Type 40)

Sent by drones to the CustodyServer. Contains a batch of hash-chained records.

```json
[
  {
    "droneId": "drone-1",
    "eventId": "drone-1:42",
    "sequence": 42,
    "timestamp": "2026-08-14T10:30:00.0000000Z",
    "eventType": "tool_call",
    "targetSystem": "local",
    "action": "run_command",
    "arguments": "{\"cmd\":\"ls -la\"}",
    "result": "ok",
    "success": true,
    "correlationId": null,
    "prevHash": "A1B2C3...",
    "hash": "D4E5F6..."
  }
]
```

**Server acknowledgment:**
```json
{"acknowledged": true, "accepted": 5, "rejected": 0, "lastSeq": 46}
```

### CustodyQuery (Type 41)

```json
{
  "droneId": "drone-1",
  "from": "2026-08-14T00:00:00Z",
  "to": "2026-08-14T23:59:59Z",
  "correlationId": "corr-abc123",
  "eventType": "tool_call"
}
```

All fields are optional. The server applies filters in priority order: correlation > drone > eventType > time range.

### CustodyTimeline (Type 42)

```json
{
  "count": 3,
  "records": [...]
}
```

### CustodyStream (Type 43)

Same format as CustodyReport payload — a JSON array of new records broadcast to all connected stream clients in real-time.

## Connection Lifecycle

```
Client                                    Server
  │                                         │
  │──── Handshake (type=101) ──────────────▶│
  │◀──── Handshake response ───────────────│
  │                                         │
  │──── JsonRpcRequest (type=1) ───────────▶│
  │◀──── JsonRpcResponse (type=2) ─────────│
  │                                         │
  │──── CustodyReport (type=40) ───────────▶│
  │◀──── Acknowledgment (text) ────────────│
  │                                         │
  │──── Heartbeat (type=100) ──────────────▶│
  │◀──── Heartbeat (type=100) ─────────────│
  │                                         │
```

## Buffer Management

NMCP supports file-backed buffers for offline resilience:

- **Drone buffer** (`nmcp_drone.bin`): Queues frames when the uplink is disconnected
- **MCP buffer** (`nmcp_mcp.bin`): Queues frames for MCP WebSocket clients

Buffer configuration:
```json
{
  "Uplink": { "BufferPath": "nmcp_drone.bin", "BufferSize": 4194304 },
  "Mcp": { "BufferPath": "nmcp_mcp.bin", "BufferSize": 1048576 }
}
```

## Error Handling

- **Invalid magic**: Frame is silently dropped (no error response)
- **Payload exceeds 16 MB**: Frame is rejected as corrupted
- **Unknown frame type**: Frame is accepted but ignored
- **Custody chain break**: Server rejects the individual record and logs a warning, but continues accepting subsequent records

## Implementation Notes

- All NMCP types are defined in `Drone.Core/Protocol/NmcpFrame.cs`
- Frame reading uses `BinaryPrimitives` for zero-allocation parsing
- The `NmcpFrame` struct is `readonly` for safe concurrent access
- Sequence IDs are sender-assigned and not validated by the receiver (except for custody chain validation)
