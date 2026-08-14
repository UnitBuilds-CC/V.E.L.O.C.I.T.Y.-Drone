# Custody Trail — Architecture & Reference

**Module:** `Drone.Core.Custody`, `Drone.Services.Custody`, `Drone.Custody`  
**Status:** Production  
**Version:** 1.0

---

## Overview

The Custody Trail is a **centralized, tamper-evident audit system** for the Velocity Drone agent. Every action the agent takes — tool calls, connections, security events, cross-machine operations — is recorded as a hash-chained SHA-256 audit record. Records are produced locally (offline-resilient), streamed in real-time to a central CustodyServer, and queryable via HTTP and WebSocket APIs.

### Key Properties

| Property | Mechanism |
|----------|-----------|
| **Tamper-evident** | SHA-256 hash chain — any deletion, reordering, or modification breaks the chain |
| **Offline-resilient** | Local JSON-lines file persists records even when the server is unreachable |
| **Cross-machine correlation** | Shared correlation IDs link multi-step operations across drones |
| **Real-time streaming** | Records broadcast to connected clients within 5 seconds |
| **Thread-safe** | All chain operations and file writes are synchronized |

## System Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                         DRONE (local)                                │
│                                                                      │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │                  CustodyAuditLogger                           │   │
│  │                                                               │   │
│  │  ┌──────────────┐  ┌──────────────────┐  ┌───────────────┐  │   │
│  │  │ CustodyChain  │  │ CorrelationTracker│  │ Ring Buffer   │  │   │
│  │  │ (hash chain,  │  │ (cross-machine   │  │ (1000 recent  │  │   │
│  │  │  sequence)    │  │  correlation IDs) │  │  records)     │  │   │
│  │  └──────┬───────┘  └────────┬──────────┘  └──────┬────────┘  │   │
│  │         │                   │                     │            │   │
│  │         ▼                   ▼                     ▼            │   │
│  │  ┌──────────────────────────────────────────────────────┐     │   │
│  │  │              Local JSON-Lines File                     │     │   │
│  │  │  drone-custody-2026-08-14.jsonl (daily rotation)      │     │   │
│  │  │  Size rotation at 50MB, keeps last 5 files           │     │   │
│  │  └──────────────────────────────────────────────────────┘     │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                              │                                       │
│                              │ OnRecordCreated event                  │
│                              ▼                                       │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │                    CustodyReporter                             │   │
│  │                                                               │   │
│  │  Flush loop: every 5s or when records pending                 │   │
│  │  Batch size: max 50 records per CustodyReport frame           │   │
│  │  Retry: unacknowledged records retried on next flush          │   │
│  │  Transport: NMCP CustodyReport frame (type 40)                │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                              │                                       │
└──────────────────────────────┼───────────────────────────────────────┘
                               │ WebSocket / NMCP
                               ▼
┌─────────────────────────────────────────────────────────────────────┐
│                      CUSTODYSERVER (central)                         │
│                                                                      │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │                  CustodyServerHost                             │   │
│  │                                                               │   │
│  │  WebSocket ingestion: accepts CustodyReport frames            │   │
│  │  Server-side chain validation: verifies sequence + prevHash   │   │
│  │  HTTP query endpoint: /custody?drone=X&from=T1&to=T2          │   │
│  │  Health endpoint: /health                                     │   │
│  │  Real-time broadcast: forwards new records to stream clients  │   │
│  └──────────────────────────┬───────────────────────────────────┘   │
│                              │                                       │
│                              ▼                                       │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │                    CustodyQueryEngine                          │   │
│  │                                                               │   │
│  │  Query(droneId, from, to, correlationId, eventType, limit)    │   │
│  │  GetVerifiedDroneTrail(droneId) → (records, chainValid)       │   │
│  │  GetGlobalTimeline(limit)                                     │   │
│  │  GetSummary() → CustodyTrailSummary                           │   │
│  └──────────────────────────┬───────────────────────────────────┘   │
│                              │                                       │
│                              ▼                                       │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │                     CustodyLogStore                            │   │
│  │                                                               │   │
│  │  Per-drone files: drones/{id}-custody-{date}.jsonl            │   │
│  │  Merged timeline: custody-merged-{date}.jsonl                 │   │
│  │  In-memory index: 10K records per drone, 100K merged          │   │
│  │  Hash chain validation on receipt                             │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

## Core Components

### CustodyRecord

**File:** `Drone.Core/Custody/CustodyRecord.cs`  
**Size:** 125 lines

The fundamental data unit. Each record captures a single auditable event with cryptographic chaining.

#### Fields

| Field | Type | Description |
|-------|------|-------------|
| `droneId` | `string` | Which drone agent produced this record |
| `eventId` | `string` | Globally unique: `{droneId}:{sequence}` |
| `sequence` | `long` | Monotonic sequence number within this drone's timeline |
| `timestamp` | `DateTime` | UTC timestamp with high resolution |
| `eventType` | `string` | Category: `tool_call`, `connection`, `security`, `cross_machine` |
| `targetSystem` | `string` | Affected system: `local`, `drone:xyz`, `share-server`, `messenger` |
| `action` | `string` | What was performed: `run_command`, `read_file`, `send_message`, etc. |
| `arguments` | `string?` | Sanitized arguments (no secrets). Null if none. |
| `result` | `string?` | Result summary: success/failure + brief description |
| `success` | `bool` | Whether the action succeeded |
| `correlationId` | `string?` | Links multi-step cross-machine sequences |
| `prevHash` | `string` | SHA-256 hash of the previous record. Empty for genesis. |
| `hash` | `string` | SHA-256 hash of this record's content |

#### Hash Computation

The content hash is computed over a deterministic string representation:

```
{droneId}|{eventId}|{sequence}|{timestamp:O}|{eventType}|{targetSystem}|{action}|{arguments}|{result}|{success}|{correlationId}
```

This is hashed with SHA-256 and stored as uppercase hex. The `prevHash` and `hash` fields are **excluded** from the hash computation to allow verification without circular dependency.

#### Key Methods

| Method | Description |
|--------|-------------|
| `ComputeHash()` | Compute SHA-256 of content (excluding prevHash/hash) |
| `Seal()` | Compute and set the Hash field |
| `VerifyHash()` | Verify this record's hash matches its content |
| `VerifyChain(prev)` | Verify hash + that PrevHash matches previous record's Hash |
| `ToJson()` / `FromJson()` | JSON serialization for storage/transmission |

### CustodyChain

**File:** `Drone.Core/Custody/CustodyChain.cs`  
**Size:** 129 lines

Thread-safe chain manager. Tracks the previous record's hash and assigns monotonic sequence numbers.

#### Key Operations

| Method | Description |
|--------|-------------|
| `NextRecord(eventType, action, ...)` | Create the next record in the chain. Assigns sequence, event ID, prev-hash, computes hash. |
| `VerifyChain(records)` (static) | Verify integrity of a sequence: hash validity, chain continuity, sequence monotonicity. |
| `VerifyContinuation(newRecords)` | Verify records that extend from this chain's current state. |
| `ResetTo(sequence, hash, record)` | Reset chain state (e.g., after loading persisted records). |

#### Chain Integrity Guarantees

1. **Sequence monotonicity** — Each record's sequence is exactly previous + 1
2. **Hash continuity** — Each record's `prevHash` equals the previous record's `hash`
3. **Content integrity** — Each record's `hash` matches its computed content hash
4. **Tamper detection** — Any modification (insert, delete, reorder, alter) breaks the chain

### CorrelationTracker

**File:** `Drone.Core/Custody/CorrelationId.cs`  
**Size:** 137 lines

Generates and tracks correlation IDs for cross-machine action sequences.

#### ID Generation

```
corr-{first 16 hex chars of SHA256("{droneId}|{timestamp}|{triggerEventId}")}
```

Example: `corr-a1b2c3d4e5f67890`

#### Lifecycle

```
Create(droneId, triggerEventId) → correlationId
    │
    ├─ RecordStep(correlationId, droneId, action)  [repeat]
    │
    └─ Complete(correlationId)
```

#### Limits

| Limit | Value | Purpose |
|-------|-------|---------|
| Max active correlations | 10,000 | Prevents memory leak |
| Max correlation age | 24 hours | Auto-evicts stale entries |

### CustodyAuditLogger

**File:** `Drone.Core/Custody/CustodyAuditLogger.cs`  
**Size:** 305 lines

The main entry point for producing custody records. Wraps `CustodyChain` and `CorrelationTracker`.

#### Logging Methods

| Method | Event Type | Description |
|--------|-----------|-------------|
| `LogToolCall(action, args, result, success, target, correlation)` | `tool_call` | Log a tool execution |
| `LogConnection(action, target, details, success, correlation)` | `connection` | Log connect/disconnect events |
| `LogSecurity(action, details, target, correlation)` | `security` | Log auth checks, token validation |
| `LogCrossMachine(action, targetDrone, args, correlation, result, success)` | `cross_machine` | Log cross-drone operations (auto-creates correlation) |
| `Log(eventType, action, args, result, success, target, correlation)` | custom | Log a generic event |

#### Storage Architecture

```
CustodyAuditLogger
    │
    ├── CustodyChain ──▶ Hash-chained record creation
    │
    ├── Local File (JSON-lines)
    │   ├── Daily rotation: drone-custody-2026-08-14.jsonl
    │   ├── Size rotation: at 50MB, rename to .HHmmss suffix
    │   ├── Keeps last 5 rotated files
    │   └── Append-only, one JSON object per line
    │
    ├── Ring Buffer (in-memory)
    │   ├── Default size: 1000 records
    │   ├── Used by CustodyReporter for streaming
    │   └── GetRecentRecords(count, afterSequence)
    │
    └── OnRecordCreated event
        └── Fires for each new record (used by CustodyReporter)
```

#### Chain Resumption

On startup, call `LoadPersistedRecords()` to:
1. Scan all `*-custody-*.jsonl` files in the log directory
2. Parse each JSON line back into `CustodyRecord` objects
3. Restore the chain state from the last valid record (sequence + hash)

This ensures the hash chain continues seamlessly across restarts.

### CustodyReporter

**File:** `Drone.Services/Custody/CustodyReporter.cs`  
**Size:** 209 lines

Background service that batches and streams records to the CustodyServer.

#### Behavior

| Parameter | Default | Description |
|-----------|---------|-------------|
| Flush interval | 5 seconds | How often to check for pending records |
| Max batch size | 50 records | Maximum records per CustodyReport frame |
| Send timeout | 10 seconds | Per-batch send timeout |
| Retry | Automatic | Unacknowledged batches are retried on next flush |

#### Transport Wiring

```csharp
// In Drone.Agent/Program.cs:
reporter.SetSendFunction(async (bytes) =>
{
    // Wrap in NMCP frame and send via WebSocket/uplink
    var frame = new NmcpFrame(NmcpFrameTypes.CustodyReport, seq++, bytes);
    return await connection.SendFrameAsync(frame);
});
```

#### Offline Resilience

- Records are always written to the local JSON-lines file first
- The ring buffer keeps the last 1000 records in memory
- When disconnected, `CustodyReporter` skips flushes but doesn't lose records
- On reconnect, it resumes from `_lastAckedSequence`, sending all unacknowledged records

## CustodyServer Components

### CustodyLogStore

**File:** `Drone.Custody/CustodyLogStore.cs`  
**Size:** 281 lines

Server-side append-only storage with dual indexing.

#### Storage Layout

```
custody-data/
├── custody-merged-2026-08-14.jsonl      # Global timeline
├── custody-merged-2026-08-15.jsonl
└── drones/
    ├── drone-1-custody-2026-08-14.jsonl  # Per-drone timeline
    ├── drone-2-custody-2026-08-14.jsonl
    └── ...
```

#### In-Memory Indexes

| Index | Capacity | Purpose |
|-------|----------|---------|
| Per-drone index | 10,000 records per drone | Fast drone-specific queries |
| Merged index | 100,000 records total | Cross-drone timeline queries |

When capacity is exceeded, oldest records are evicted from memory but remain on disk.

#### Validation on Receipt

1. Verify each record's hash (`VerifyHash()`)
2. Verify sequence continuity against server-side chain state
3. Verify `prevHash` matches the last known hash for that drone
4. Reject invalid records individually (don't reject the whole batch)

### CustodyServerHost

**File:** `Drone.Custody/CustodyServerHost.cs`  
**Size:** 327 lines

WebSocket + HTTP server.

#### Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/custody` | GET | Query custody records (see query parameters below) |
| `/health` | GET | Server health + summary statistics |
| `/` (WebSocket) | WS | Accept CustodyReport frames from drones |

#### Query Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `drone` | string | Filter by drone ID |
| `from` | ISO 8601 | Start of time range |
| `to` | ISO 8601 | End of time range |
| `correlation` | string | Filter by correlation ID |
| `eventType` | string | Filter by event type |

#### Query Response

```json
{
  "count": 42,
  "records": [
    { "droneId": "drone-1", "sequence": 1, ... },
    { "droneId": "drone-1", "sequence": 2, ... }
  ]
}
```

#### Health Response

```json
{
  "status": "healthy",
  "totalRecords": 1234,
  "droneCount": 3,
  "streamClients": 2,
  "droneIds": ["drone-1", "drone-2", "drone-3"]
}
```

#### Stream Broadcasting

When new records are accepted, they are broadcast to all connected WebSocket stream clients. Maximum 256 concurrent connections.

### CustodyQueryEngine

**File:** `Drone.Custody/CustodyQueryEngine.cs`  
**Size:** 138 lines

Higher-level query operations wrapping `CustodyLogStore`.

#### Query Priority

When multiple filters are provided, the engine selects the most specific:

1. `correlationId` — Direct correlation lookup
2. `droneId` — Per-drone timeline (with optional time filter)
3. `eventType` — Event type filter (with optional time filter)
4. `from` + `to` — Time range on merged timeline
5. No filters — Return most recent records from merged timeline

#### Verified Trail

```csharp
var (records, chainValid) = queryEngine.GetVerifiedDroneTrail("drone-1");
// chainValid == false means tampering or data loss detected
```

### CustodyTrailSummary

| Field | Type | Description |
|-------|------|-------------|
| `TotalRecords` | `int` | Total records across all drones |
| `DroneCount` | `int` | Number of distinct drones |
| `DroneIds` | `string[]` | List of drone IDs |
| `MergedTimelineCount` | `int` | Records in the merged timeline |

## Environment Variables

### Drone Side

| Variable | Default | Description |
|----------|---------|-------------|
| `DRONE_CUSTODY_PATH` | `./custody/drone-custody.jsonl` | Local custody log file path |
| `DRONE_CUSTODY_SERVER` | _(none)_ | CustodyServer URL for streaming |

### Server Side

| Variable | Default | Description |
|----------|---------|-------------|
| `CUSTODY_STORAGE_PATH` | `./custody-data` | Server-side storage directory |
| `CUSTODY_LISTEN_URL` | `http://+:5010/` | Server listen URL |

## Event Types

| Event Type | Logged By | Examples |
|-----------|-----------|---------|
| `tool_call` | `LogToolCall()` | `run_command`, `read_file`, `write_file`, `list_tools` |
| `connection` | `LogConnection()` | `connected`, `disconnected`, `reconnected` |
| `security` | `LogSecurity()` | `auth_check`, `token_validated`, `auth_failed` |
| `cross_machine` | `LogCrossMachine()` | `remote_exec`, `remote_read`, `remote_write` |

## Testing

### Unit Tests (24 tests in `CustodyTests.cs`)

| Test Class | Tests | Covers |
|-----------|-------|--------|
| `CustodyRecordTests` | 6 | Hash computation, tamper detection, chain verification, JSON round-trip |
| `CustodyChainTests` | 7 | Sequence increment, hash chaining, chain validation, removal/reorder detection, state reset |
| `CorrelationTrackerTests` | 5 | ID generation, step counting, completion, active tracking |
| `CustodyAuditLoggerTests` | 6 | Chained records, ring buffer, sequence filtering, events, cross-machine correlation |

### E2E Test (Test 11 in `Drone.E2E/Program.cs`)

Full custody trail pipeline:
1. Create 5 records via `CustodyAuditLogger`
2. Verify hash chain integrity with `CustodyChain.VerifyChain()`
3. Verify individual record properties (sealed, sequenced, chained)
4. Verify correlation tracking
5. Retrieve from ring buffer
6. JSON round-trip chain integrity

## Security Considerations

1. **No secrets in records** — Arguments are sanitized before logging. Never include tokens, passwords, or API keys.
2. **Tamper evidence, not prevention** — The hash chain makes tampering detectable but doesn't prevent it. An attacker with write access to the log file can delete records, but the chain break will be visible.
3. **Server-side validation** — The CustodyServer validates chains on receipt, rejecting records that don't chain correctly.
4. **Local file permissions** — Custody log files should be stored in a directory with restricted write permissions.
5. **Ring buffer limits** — Only the last 1000 records are kept in memory. Older records must be read from disk.
