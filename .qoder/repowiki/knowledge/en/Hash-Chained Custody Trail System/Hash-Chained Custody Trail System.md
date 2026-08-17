---
kind: security_system
name: Hash-Chained Custody Trail System
category: security
scope:
    - 'Drone.Core/Custody/**'
    - 'Drone.Services/Custody/**'
    - 'Drone.Custody/**'
source_files:
    - Drone.Core/Custody/CustodyRecord.cs
    - Drone.Core/Custody/CustodyChain.cs
    - Drone.Core/Custody/CorrelationId.cs
    - Drone.Core/Custody/CustodyAuditLogger.cs
    - Drone.Services/Custody/CustodyReporter.cs
    - Drone.Custody/CustodyLogStore.cs
    - Drone.Custody/CustodyServerHost.cs
    - Drone.Custody/CustodyQueryEngine.cs
---

The Custody Trail is a tamper-evident, hash-chained audit trail that records every action taken by the drone. It provides offline-resilient local logging with real-time streaming to a central CustodyServer.

**Core Components:**

1. **CustodyRecord** — Individual audit entry:
   - `DroneId` — Source drone identifier
   - `Sequence` — Monotonic sequence number
   - `PrevHash` — SHA-256 hash of previous record (chain link)
   - `ContentHash` — SHA-256 hash of this record's content
   - `EventType` — Action type (tool_call, connection, etc.)
   - `Timestamp` — UTC timestamp
   - `CorrelationId` — Cross-machine correlation tracking
   - `Data` — JSON payload with action details

2. **CustodyChain** — Thread-safe chain manager:
   - Maintains monotonic sequence ordering
   - Computes and verifies hash chains
   - `AddRecord()` — Appends record with computed hashes
   - `VerifyChain()` — Validates entire chain integrity
   - `ResetTo(sequence)` — Truncates chain for recovery

3. **CorrelationTracker** — Cross-machine correlation:
   - Generates correlation IDs (`corr-{guid}`)
   - Tracks step counts per correlation
   - `ConcurrentDictionary` for lock-free reads
   - Atomic step increment with `Interlocked`

4. **CustodyAuditLogger** — Record producer:
   - Writes local JSON-lines file (`custody.jsonl`)
   - Ring buffer (1000 records) for memory efficiency
   - Daily file rotation
   - `LoadPersistedRecords()` — Resumes chain from disk
   - `LogToolCall()`, `LogConnection()`, `LogEvent()` — Typed logging methods

5. **CustodyReporter** — Background batch service:
   - Flushes every 5 seconds or 50 records
   - Sends NMCP Frame Type 40 (CustodyReport)
   - Retry on reconnect
   - `Task.Run` background loop with cancellation

**Server-Side (Drone.Custody):**

- **CustodyLogStore** — Append-only JSON-lines storage:
  - Per-drone files (`{droneId}.jsonl`)
  - Merged global timeline file
  - In-memory indexes for fast queries

- **CustodyServerHost** — WebSocket + HTTP server:
  - Receives custody reports via WebSocket
  - Validates hash chains on receipt
  - HTTP query API: `GET /custody?drone=X&from=T1&to=T2`
  - Real-time broadcast to connected clients

- **CustodyQueryEngine** — Flexible querying:
  - By drone ID, time range, correlation ID, event type
  - Verified trail retrieval (validates chain)
  - Pagination support

**NMCP Frame Types for Custody:**
| Type | Code | Direction | Purpose |
|------|------|-----------|---------|
| CustodyReport | 40 | Drone→Server | Batch of hash-chained records |
| CustodyQuery | 41 | Drone→Server | Query request |
| CustodyTimeline | 42 | Server→Client | Query response with records |
| CustodyStream | 43 | Server→Clients | Real-time broadcast |

**Security Properties:**
- **Tamper-evident:** Any modification breaks hash chain
- **Monotonic:** Sequence numbers prevent insertion/deletion
- **Offline-resilient:** Local file persists during outages
- **Cross-machine:** Correlation IDs link related actions across drones

**Configuration:**
- `DRONE_CUSTODY_PATH` — Local log file path
- `DRONE_CUSTODY_SERVER` — CustodyServer URL
- `CUSTODY_STORAGE_PATH` — Server-side storage directory
- `CUSTODY_LISTEN_URL` — Server listen URL
