---
kind: transport_layer
name: WebSocket and Shared Memory Dual Transport
category: transport
scope:
    - 'Drone.Core/**'
    - 'Drone.Services/**'
source_files:
    - Drone.Core/VelocityConnection.cs
    - Drone.Core/Protocol/NmcpFrame.cs
---

VelocityConnection provides bidirectional communication with dual transport support: WebSocket for remote connections and shared memory for local IPC. The connection automatically falls back from shared memory to WebSocket when local transport is unavailable.

**Transport Modes:**

1. **WebSocket Transport** — Remote, network-based:
   - Protocol: RFC 6455 with NMCP binary framing
   - Reconnection: Exponential backoff (1s, 2s, 3s...) with attempt limit
   - Heartbeat: 30-second interval to detect dead peers
   - Message fragmentation: Handles large messages via chunked transfer
   - Thread safety: `SemaphoreSlim` serializes concurrent sends

2. **Shared Memory Transport** — Local, zero-copy IPC:
   - Protocol: Atomic state machine over memory-mapped files
   - Layout: Request channel (4KB) + Response channel (61KB) = 65KB total
   - Polling: 100μs spin-wait before yielding to Task.Delay
   - Atomic operations: State bytes coordinate request/response handshake
   - Use case: Local IPC with V.E.L.O.C.I.T.Y.-MCP or other processes

**Shared Memory Layout:**

```
┌─────────────────────────────────────────────────────────────────┐
│  Request Channel (4100 bytes)                                    │
│  [0]      State byte (0=Idle, 1=ReqReady, 2=Processing)         │
│  [1..4]   Payload length (int32 LE)                              │
│  [5..4099] Payload (4096 bytes max)                              │
├─────────────────────────────────────────────────────────────────┤
│  Response Channel (61431 bytes)                                  │
│  [4100]   State byte (0=Idle, 3=ResReady, 4=Error)               │
│  [4101..4104] Payload length (int32 LE)                          │
│  [4105..65535] Payload (61431 bytes max)                         │
└─────────────────────────────────────────────────────────────────┘
```

**Atomic State Machine:**

```
Client writes payload → Set REQ_READY (1)
Server detects REQ_READY → Read payload → Set PROCESSING (2)
Server processes → Write response → Set RES_READY (3)
Client detects RES_READY → Read response → Set IDLE (0)
```

**Connection Lifecycle:**

```csharp
var connection = new VelocityConnection(config, logger);
connection.OnRequest += async request => { /* handle request */ };
connection.OnNotification += async notification => { /* handle notification */ };

await connection.ConnectAsync(cancellationToken);
// Connection established, read loop running in background

await connection.SendResponseAsync(jsonResponse);
await connection.SendNotificationAsync(jsonNotification);

await connection.DisposeAsync();  // Cleanup resources
```

**Configuration:**

```json
{
  "Uplink": {
    "Transport": "auto",  // "shmem", "websocket", or "auto"
    "BufferPath": "nmcp_buffer.bin",
    "BufferSize": 65536,
    "WebSocketUrl": "ws://server:9000",
    "AutoReconnect": true,
    "MaxReconnectAttempts": 5
  }
}
```

**Fallback Behavior:**

1. Attempt shared memory connection first (if transport is "shmem" or "auto")
2. If shared memory fails (file access denied, etc.), fall back to WebSocket
3. If WebSocket fails, connection attempt fails with exception

**Heartbeat Mechanism:**

- Sends NMCP Heartbeat frame every 30 seconds
- Detects dead peers (connection closed by server)
- Uses `SemaphoreSlim` to prevent concurrent sends with data
- Timeout: 10 seconds per send operation

**Error Handling:**

- WebSocket errors logged, triggers reconnection
- Shared memory errors logged, continues polling
- JSON parse errors logged, message discarded
- Max reconnect attempts reached → gives up with error log

**Thread Safety:**

- `SemaphoreSlim` (_wsSendLock) serializes WebSocket sends
- `volatile bool _connected` for connection state
- `Interlocked.Increment` for sequence IDs
- Separate locks for heartbeat and data sends

**Performance Characteristics:**

| Transport | Latency | Throughput | Use Case |
|-----------|---------|------------|----------|
| Shared Memory | ~100μs | High | Local IPC |
| WebSocket | ~1ms | Medium | Remote connections |

**Integration Points:**

- `Drone.Agent` creates VelocityConnection for uplink
- `McpServer` uses NMCP framing for tool calls
- `RemoteConnector` uses NMCP/NDA for authenticated calls
- `CustodyReporter` sends CustodyReport frames
