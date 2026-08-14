# Velocity Drone

Autonomous agent for remote system management. Runs as a background tray agent on any Windows machine (or headless via Docker), exposing tools via MCP (Model Context Protocol) over the NMCP binary protocol. Controlled remotely via Messenger commands, WebSocket uplink, or direct NMCP tool calls.

## Quick Start

```bash
# Build
dotnet build VelocityDrone.slnx /p:SkipRust=true

# Test (52 unit tests + 10 E2E)
dotnet test tests/Drone.Tests/Drone.Tests.csproj
dotnet run --project tests/Drone.E2E/Drone.E2E.csproj

# Run agent (Windows)
dotnet run --project Drone.Agent/Drone.Agent.csproj

# Run custody server
dotnet run --project Drone.Custody/Drone.Custody.csproj
```

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                      Drone.Agent                            │
│  Tray app + RunDroneAsync entry point                       │
│  ┌──────────┐ ┌──────────┐ ┌───────────┐ ┌──────────────┐  │
│  │ Messenger│ │  Remote  │ │  Uplink   │ │  Custody     │  │
│  │Connector │ │ Connector│ │(Velocity) │ │  Reporter    │  │
│  └────┬─────┘ └────┬─────┘ └─────┬─────┘ └──────┬───────┘  │
│       │            │             │               │          │
│  ┌────┴────────────┴─────────────┴───────────────┴───────┐  │
│  │                   McpServer                           │  │
│  │  Tool registration + JSON-RPC + NMCP framing          │  │
│  └───────────────────────┬───────────────────────────────┘  │
│                          │                                  │
│  ┌──────────┐ ┌──────────┴───┐ ┌──────────┐ ┌───────────┐  │
│  │ Autonomy │ │SystemToolReg │ │  Share   │ │ Custody   │  │
│  │ Engine   │ │(screen,input │ │ Connector│ │AuditLogger│  │
│  │          │ │ clipboard...)│ │          │ │           │  │
│  └──────────┘ └──────────────┘ └──────────┘ └───────────┘  │
└─────────────────────────────────────────────────────────────┘
```

See [Architecture](docs/architecture.md) for the full module map, dependency graph, data flow diagrams, and threading model.

## Projects

| Project | Description |
|---------|-------------|
| **Drone.Agent** | Entry point. Tray app, DI wiring, Messenger command handler, MCP server host. Windows-specific (WinForms). |
| **Drone.Core** | Shared types: `ILogger`, `DroneConfig`, `EventBus`, NMCP binary frame protocol, custody trail primitives. |
| **Drone.Services** | Connectors: `MessengerConnector`, `RemoteConnector`, `ShareConnector`, `CustodyReporter`, `DeltaScreenPipeline`. |
| **Drone.MCP** | MCP server: tool registration, JSON-RPC 2.0, WebSocket transport, NMCP buffer management. |
| **Drone.System** | Platform abstractions: `IScreenCapture`, `IInputSimulator`, `IWindowManager`, `IProcessManager`. Windows/Linux/macOS. |
| **Drone.Autonomy** | Rule engine: `BehaviorRule` triggers + `ActionHandler` responses. `EventBus`-driven autonomous behavior. |
| **Drone.Native** | Rust FFI bindings: delta frame serialization, WebP compression, native performance-critical paths. |
| **Drone.Custody** | Standalone custody server: WebSocket ingestion, hash-chain validation, HTTP query API, real-time stream broadcast. |
| **Drone.Tests** | xUnit tests: 52 tests covering core protocol, custody trail, MCP server, autonomy engine, event bus. |
| **Drone.E2E** | End-to-end integration tests: MCP handshake, WebSocket transport, auth, custody trail pipeline. |
| **DeltaBench** | Benchmarking project for delta frame serialization and compression performance. |

## Custody Trail

Every action the agent takes is recorded in a **tamper-evident, hash-chained audit trail**. Records are produced locally (offline-resilient) and streamed in real-time to a central CustodyServer.

```
Drone (local)                              CustodyServer (central)
┌──────────────────────┐                   ┌─────────────────────────┐
│ CustodyAuditLogger    │   NMCP frame 40   │ Per-drone timelines     │
│  ├─ SHA-256 hash chain│──────────────────▶│  ├─ chain validation    │
│  ├─ monotonic sequence│   CustodyReport   │  └─ sequence ordering   │
│  └─ correlation ID    │                   │                         │
│                       │                   │ Merged global timeline  │
│ CustodyReporter       │◀─────────────────│  ├─ by timestamp        │
│  └─ batch (5s / 50)   │   HTTP + WS      │  └─ query API           │
│                       │   query/stream    │                         │
│ Local JSON-lines file │                   │ WebSocket broadcast     │
│  └─ offline resilience│                   │  └─ real-time events    │
└──────────────────────┘                   └─────────────────────────┘
```

### Components

| Component | Module | Purpose |
|-----------|--------|---------|
| `CustodyRecord` | Core | Hash-chained record: SHA-256 content hash, sequence, prev-hash, correlation ID |
| `CustodyChain` | Core | Thread-safe chain manager: monotonic sequence, `VerifyChain()`, `ResetTo()` |
| `CorrelationTracker` | Core | Cross-machine correlation IDs (`corr-` prefix), step counting, active tracking |
| `CustodyAuditLogger` | Core | Produces records, writes local JSON-lines, ring buffer (1000), daily rotation |
| `CustodyReporter` | Services | Background batch+stream: flush every 5s or 50 records, retry on reconnect |
| `CustodyLogStore` | Custody | Server-side append-only storage, per-drone + merged files, in-memory indexes |
| `CustodyServerHost` | Custody | WebSocket server: receives reports, validates chains, HTTP query, stream broadcast |
| `CustodyQueryEngine` | Custody | Query by drone, time range, correlation ID, event type. Verified trail retrieval |

See [Custody Trail Reference](docs/custody-trail.md) for the complete architecture, API reference, field specifications, and security considerations.

### NMCP Frame Types

| Type | Code | Direction | Purpose |
|------|------|-----------|---------|
| `CustodyReport` | 40 | Drone → Server | Batch of hash-chained custody records |
| `CustodyQuery` | 41 | Drone → Server | Query request (drone, time, correlation, event) |
| `CustodyTimeline` | 42 | Server → Client | Query response with ordered records |
| `CustodyStream` | 43 | Server → Clients | Real-time broadcast of new records |

See [NMCP Protocol Specification](docs/nmcp-protocol.md) for the full wire format, frame type registry, and connection lifecycle.

### CustodyServer API

**HTTP Query:**
```
GET /custody?drone=X&from=T1&to=T2&correlation=Y&eventType=Z
GET /health
```

**WebSocket:** Connect to receive real-time custody events. Server validates hash chains on receipt and rejects broken chains.

## Configuration

Edit `Drone.Agent/appsettings.json` or set environment variables:

```json
{
  "Drone": {
    "DroneId": "my-drone",
    "Mode": "Full",
    "Uplink": { "WebSocketUrl": "ws://server:9000" },
    "Messenger": { "ServerUrl": "https://messenger.example.com" },
    "Remote": { "ServerUrl": "wss://remote.example.com/nmcp" },
    "Share": { "ServerUrl": "http://+:5003", "AdminApiKey": "..." },
    "Autonomy": { "Enabled": true },
    "Mcp": { "BufferPath": "nmcp_mcp.bin", "BufferSize": 1048576 }
  }
}
```

| Env Variable | Description |
|-------------|-------------|
| `DRONE_ID` | Override drone identity |
| `DRONE_MODE` | `full` (default) or `headless` |
| `DRONE_WS_URL` | Uplink WebSocket URL |
| `DRONE_MCP_URL` | MCP WebSocket listen URL (default `http://+:9100`) |
| `DRONE_MCP_TOKEN` | MCP auth token |
| `DRONE_CUSTODY_PATH` | Local custody log file path |
| `DRONE_CUSTODY_SERVER` | CustodyServer URL for streaming |
| `CUSTODY_STORAGE_PATH` | Server-side storage directory |
| `CUSTODY_LISTEN_URL` | CustodyServer listen URL |

See [Configuration Reference](docs/configuration.md) for every setting, defaults, and Docker environment.

## Messenger Commands

Send these as direct messages to the drone via the Messenger connector:

| Command | Description |
|---------|-------------|
| `status` | Get drone status (system info, connections, uptime) |
| `run <command>` | Execute a shell command |
| `type <text>` | Type text via simulated keystrokes |
| `key <key>` | Press a key (e.g., `key enter`, `key ctrl+c`) |
| `click <x> <y> [btn]` | Click at coordinates (left/right) |
| `screenshot` | Capture and return screen image |
| `list [path]` | List files in shared directory |
| `upload <local> <remote>` | Upload file to shared storage |
| `download <remote> <local>` | Download file from shared storage |
| `benchmark` | Run performance benchmarks (NDA, delta, WebP) |
| `update` | Self-update from shared directory |

## Building

Requires .NET 10 preview SDK.

```bash
# Build all projects
dotnet build VelocityDrone.slnx /p:SkipRust=true

# Build specific module
dotnet build Drone.Core/Drone.Core.csproj
dotnet build Drone.Custody/Drone.Custody.csproj
```

## Testing

```bash
# Unit tests (52 tests)
dotnet test tests/Drone.Tests/Drone.Tests.csproj

# E2E integration tests (10 tests + 3 skipped on Windows without admin)
dotnet run --project tests/Drone.E2E/Drone.E2E.csproj
```

### Test Coverage

| Test Class | Tests | Covers |
|-----------|-------|--------|
| `CustodyRecordTests` | 6 | Hash computation, tamper detection, chain verification, JSON round-trip |
| `CustodyChainTests` | 7 | Sequence increment, hash chaining, chain validation, removal/reorder detection, state reset |
| `CorrelationTrackerTests` | 5 | ID generation, step counting, completion, active tracking |
| `CustodyAuditLoggerTests` | 6 | Chained records, ring buffer, sequence filtering, events, cross-machine correlation |
| `CoreTests` | 8 | NMCP frame protocol, config validation |
| `McpServerTests` | 6 | Tool registration, JSON-RPC protocol, error handling |
| `BehaviorRuleTests` | 5 | Trigger matching, conditions, action params |
| `EventBusTests` | 4 | Pub/sub, filtering, error isolation |
| `E2E Tests` | 10 | MCP handshake, tool calls, WebSocket transport, auth, custody trail |

## Deployment

```bash
# Docker (headless, for cloud VMs)
docker build -t velocity-drone:latest .
docker run -d -e DRONE_MODE=headless -e DRONE_MCP_TOKEN=secret -p 9100:9100 velocity-drone

# Windows tray app
dotnet publish Drone.Agent/Drone.Agent.csproj -c Release -o ./publish

# CustodyServer
dotnet run --project Drone.Custody/Drone.Custody.csproj
```

See [Deployment Guide](docs/deployment.md) for Docker Compose, systemd, Azure deployment, and network requirements.

## Documentation

| Document | Description |
|----------|-------------|
| [Architecture](docs/architecture.md) | Module map, dependency graph, data flow, threading model |
| [Custody Trail](docs/custody-trail.md) | Full custody system architecture, API reference, security |
| [NMCP Protocol](docs/nmcp-protocol.md) | Wire format specification, frame types, connection lifecycle |
| [Configuration](docs/configuration.md) | Every setting, defaults, environment variables |
| [Deployment](docs/deployment.md) | Docker, Windows, CustodyServer, Azure, systemd |
| [Development](docs/development.md) | Building, testing, conventions, debugging, adding tools |

## Project Structure

```
Velocity-Drone/
├── Drone.Agent/            # Entry point, tray app, DI wiring
│   ├── Program.cs          # Main, RunDroneAsync
│   ├── UI/                 # TrayApp, system tray UI
│   ├── Benchmarks/         # DroneBenchmark suite
│   └── appsettings.json    # Configuration
├── Drone.Core/             # Shared types and protocol
│   ├── Custody/            # Hash-chained custody trail
│   │   ├── CustodyRecord.cs
│   │   ├── CustodyChain.cs
│   │   ├── CorrelationId.cs
│   │   └── CustodyAuditLogger.cs
│   ├── Protocol/           # NMCP binary framing, NDA triples
│   ├── Config/             # DroneConfig, validation
│   └── VelocityConnection.cs
├── Drone.Services/         # Connectors and background services
│   ├── Messenger/          # MessengerConnector
│   ├── Remote/             # RemoteConnector (NMCP/NDA)
│   ├── Share/              # ShareConnector, EmbeddedFileServer
│   └── Custody/            # CustodyReporter
├── Drone.MCP/              # MCP server, tool registration
├── Drone.System/           # Platform abstractions (screen, input, window, process)
│   ├── Windows/            # Win32 implementations
│   ├── Linux/              # Linux implementations
│   └── MacOS/              # macOS implementations
├── Drone.Autonomy/         # Rule engine, behavior rules, event bus
├── Drone.Native/           # Rust FFI (delta frames, WebP)
├── Drone.Custody/          # Standalone custody server
│   ├── CustodyLogStore.cs
│   ├── CustodyServerHost.cs
│   ├── CustodyQueryEngine.cs
│   └── Program.cs
├── tests/
│   ├── Drone.Tests/        # 52 xUnit tests
│   └── Drone.E2E/          # E2E integration tests
├── docs/                   # Documentation
│   ├── architecture.md
│   ├── custody-trail.md
│   ├── nmcp-protocol.md
│   ├── configuration.md
│   ├── deployment.md
│   └── development.md
└── DeltaBench/             # Delta frame benchmarks
```
