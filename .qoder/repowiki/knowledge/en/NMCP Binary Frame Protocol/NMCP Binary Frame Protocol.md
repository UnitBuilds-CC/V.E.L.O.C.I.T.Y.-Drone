---
kind: protocol_specification
name: NMCP Binary Frame Protocol
category: protocol
scope:
    - 'Drone.Core/**'
    - 'Drone.MCP/**'
    - 'Drone.Services/**'
source_files:
    - Drone.Core/Protocol/NmcpFrame.cs
    - Drone.Core/Protocol/NmcpFrameTypes.cs
    - Drone.Core/VelocityConnection.cs
    - Drone.MCP/McpServer.cs
---

NMCP (Neural Mesh Communication Protocol) is a custom binary framing protocol used for all inter-component communication in Velocity Drone. It provides efficient, length-prefixed message wrapping over both WebSocket and shared memory transports.

**Frame Structure (16-byte header):**
- **Type (4 bytes, big-endian):** Frame type identifier (e.g., 0x01=Request, 0x02=Response, 0x03=Notification, 0x04=Heartbeat)
- **Sequence (4 bytes, big-endian):** Monotonic sequence ID for request correlation
- **Length (4 bytes, big-endian):** Payload length in bytes
- **Reserved (4 bytes):** Future use, currently zeroed

**Frame Type Registry:**
| Type | Code | Direction | Purpose |
|------|------|-----------|---------|
| JsonRpcRequest | 0x01 | Client→Server | MCP tool invocation |
| JsonRpcResponse | 0x02 | Server→Client | Tool result |
| JsonRpcNotification | 0x03 | Bidirectional | Event notifications |
| Heartbeat | 0x04 | Bidirectional | Keep-alive ping |
| CustodyReport | 40 | Drone→Server | Batch custody records |
| CustodyQuery | 41 | Drone→Server | Query request |
| CustodyTimeline | 42 | Server→Client | Query response |
| CustodyStream | 43 | Server→Clients | Real-time broadcast |

**Key Implementation Details:**
- `NmcpFrame` class in `Drone.Core.Protocol` handles serialization/deserialization
- `WriteHeader(byte[] buffer)` writes the 16-byte header to buffer
- `ReadHeader(byte[] buffer)` parses header and returns frame metadata
- Payload follows immediately after header
- Big-endian byte order for network compatibility

**Usage Patterns:**
- **WebSocket:** Frame sent as binary message, payload is UTF-8 JSON
- **Shared Memory:** Frame payload written to memory-mapped file regions
- **File Buffer:** Frames written to `nmcp_mcp.bin` for offline resilience

**Integration Points:**
- `VelocityConnection` wraps all sends in NMCP frames
- `McpServer` parses incoming frames to extract JSON-RPC
- `CustodyReporter` batches records into CustodyReport frames
- `RemoteConnector` uses NMCP/NDA for authenticated remote calls
