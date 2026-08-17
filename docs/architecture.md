# Architecture

**Project:** Velocity Drone  
**Runtime:** .NET 10 preview, Windows (primary), Linux/macOS (headless via Docker)

---

## System Overview

Velocity Drone is a modular autonomous agent for remote system management. It runs as a background tray agent on any Windows machine (or headless in Docker), exposing tools via MCP (Model Context Protocol) over the NMCP binary protocol. It's controlled remotely via Messenger commands, WebSocket uplink, or direct NMCP tool calls.

## Module Map

```
┌─────────────────────────────────────────────────────────────────────┐
│                         Drone.Agent                                  │
│  Entry point · Tray app · DI wiring · RunDroneAsync                  │
│  ┌────────────┐ ┌────────────┐ ┌────────────┐ ┌──────────────────┐ │
│  │ Messenger  │ │  Remote    │ │  Uplink    │ │  Custody         │ │
│  │ Connector  │ │  Connector │ │ (Velocity) │ │  Reporter        │ │
│  └─────┬──────┘ └─────┬──────┘ └─────┬──────┘ └───────┬──────────┘ │
│        │              │              │                  │            │
│  ┌─────┴──────────────┴──────────────┴──────────────────┴────────┐  │
│  │                       McpServer                                │  │
│  │  Tool registration · JSON-RPC 2.0 · NMCP framing               │  │
│  └───────────────────────────┬────────────────────────────────────┘  │
│                              │                                       │
│  ┌────────────┐ ┌────────────┴───┐ ┌────────────┐ ┌──────────────┐ │
│  │  Autonomy  │ │SystemToolReg   │ │   Share    │ │ CustodyAudit │ │
│  │  Engine    │ │(screen, input, │ │  Connector │ │  Logger      │ │
│  │            │ │ clipboard...)  │ │            │ │              │ │
│  └────────────┘ └────────────────┘ └────────────┘ └──────────────┘ │
└─────────────────────────────────────────────────────────────────────┘
         │                │                │                │
         ▼                ▼                ▼                ▼
┌──────────────┐ ┌──────────────┐ ┌──────────────┐ ┌──────────────┐
│ Drone.Core   │ │Drone.Services│ │  Drone.MCP   │ │Drone.System  │
│ Shared types │ │ Connectors   │ │ MCP server   │ │ Platform     │
│ NMCP proto   │ │ + background │ │ tool reg     │ │ abstractions │
│ Custody core │ │   services   │ │ JSON-RPC     │ │ Win/Lin/Mac  │
└──────────────┘ └──────────────┘ └──────────────┘ └──────────────┘
         │
         ▼
┌──────────────┐ ┌──────────────┐
│Drone.Autonomy│ │ Drone.Native │
│ Rule engine  │ │ Rust FFI     │
│ Event bus    │ │ Delta frames │
│ Behavior     │ │ WebP comp    │
└──────────────┘ └──────────────┘

Standalone server:
┌──────────────┐
│Drone.Custody │  WebSocket ingestion · HTTP query API
│              │  Hash-chain validation · Stream broadcast
└──────────────┘
```

## Project Responsibilities

### Drone.Agent (Entry Point)

The application entry point. Handles:
- **DI wiring** — Creates and connects all components
- **Tray app** — System tray icon on Windows (WinForms)
- **Messenger command handler** — Parses incoming commands (`status`, `run`, `type`, `key`, `click`, `screenshot`, `list`, `upload`, `download`, `benchmark`, `update`)
- **MCP server host** — Starts the WebSocket MCP server
- **Custody initialization** — Creates `CustodyAuditLogger`, loads persisted records, starts `CustodyReporter`
- **Connection lifecycle** — Manages Messenger, Remote, Share, and Uplink connections

**Platform:** Windows-specific (WinForms for tray). Headless mode via Docker.

### Drone.Core (Shared Foundation)

Zero-dependency foundation layer. Contains:
- **`ILogger`** — Minimal logging abstraction (`LogInformation`, `LogWarning`, `LogError`, `LogDebug`)
- **`DroneConfig`** — Configuration model with validation
- **`EventBus`** — Pub/sub event system for autonomous behavior
- **NMCP protocol** — Binary frame format (16-byte header, big-endian), frame type registry
- **`VelocityConnection`** — WebSocket connection with NMCP buffer management
- **Custody primitives** — `CustodyRecord`, `CustodyChain`, `CorrelationTracker`, `CustodyAuditLogger`
- **`CircuitBreaker`** — Thread-safe circuit breaker (Closed/Open/HalfOpen) for connector resilience

### Drone.Services (Connectors & Background Services)

Network connectors and background services. All connectors integrate `CircuitBreaker` for cascading failure protection:
- **`MessengerConnector`** — WebSocket connection to Velocity Messenger for command reception (30s HttpClient timeout, circuit breaker)
- **`RemoteConnector`** — NMCP/NDA connection for remote tool execution with custody logging (circuit breaker)
- **`ShareConnector`** — HTTP/WebSocket connection to Share server for file operations (30s HttpClient timeout)
- **`CustodyReporter`** — Background service batching custody records for streaming
- **`DeltaScreenPipeline`** — Zero-alloc delta frame screen capture pipeline
- **`EmbeddedFileServer`** — Lightweight HTTP file server with path traversal protection

### Drone.MCP (MCP Server)

Model Context Protocol implementation:
- **Tool registration** — `SystemToolRegistrar` registers platform tools
- **JSON-RPC 2.0** — Request/response/notification handling
- **NMCP framing** — Binary frame wrapping for tool calls/results
- **WebSocket transport** — Client connections with bearer token auth, rate limiting, max message size enforcement
- **Health endpoint** — `/health` with connection status, custody chain, uptime metrics
- **Buffer management** — File-backed NMCP buffer for offline resilience

### Drone.System (Platform Abstractions)

Cross-platform system interfaces:
- **`IScreenCapture`** — Screen capture (Win32 GDI+, Linux scrot/import, macOS screencapture)
- **`IInputSimulator`** — Keyboard/mouse simulation (Win32 SendInput, Linux xdotool, macOS cliclick)
- **`IWindowManager`** — Window enumeration and manipulation (Win32 API, Linux wmctrl, macOS osascript)
- **`IProcessManager`** — Process management (cross-platform)
- **`PlatformFactory`** — Creates platform-specific implementations at runtime

### Drone.Autonomy (Rule Engine)

Autonomous behavior engine:
- **`BehaviorRule`** — Trigger conditions + action handlers
- **`ActionHandler`** — Executes actions when triggers fire
- **`AutonomyEngine`** — Evaluates rules on a schedule
- **`EventBus`** — Event-driven trigger system

### Drone.Native (Rust FFI)

Performance-critical native code:
- **Delta frame serialization** — Efficient screen delta encoding
- **WebP compression** — Image compression for screen captures
- **`DeltaEngine`**, **`DeltaFrameSerializer`**, **`WebpCompressor`** — C# wrappers around native DLLs

### Drone.Custody (Standalone Server)

Central custody trail server:
- **`CustodyLogStore`** — Append-only JSON-lines storage with per-drone + merged files
- **`CustodyServerHost`** — WebSocket server with HTTP query API
- **`CustodyQueryEngine`** — Flexible query by drone, time, correlation, event type
- **Standalone entry point** — Runs independently via `dotnet run`

## Data Flow

### Command Execution Flow

```
Messenger ──WS──▶ MessengerConnector ──▶ Command Handler
                                              │
                                    ┌─────────┼─────────┐
                                    ▼         ▼         ▼
                              McpServer  SystemTools  CustodyAuditLogger
                              (JSON-RPC) (screen,    (log the command)
                                 │        input,
                                 │        files...)
                                 ▼
                            Tool Result ──▶ MessengerConnector ──WS──▶ Messenger
```

### Custody Trail Flow

```
Agent Action ──▶ CustodyAuditLogger
                      │
            ┌─────────┼─────────────┐
            ▼         ▼             ▼
       CustodyChain  Local File   Ring Buffer
       (hash+seq)    (JSON-lines) (1000 records)
                                    │
                                    ▼
                              CustodyReporter
                              (batch 5s/50)
                                    │
                              NMCP Frame 40
                              (CustodyReport)
                                    │
                                    ▼
                              CustodyServer
                              (validate chain)
                                    │
                        ┌───────────┼───────────┐
                        ▼           ▼           ▼
                   CustodyLogStore  HTTP API   WebSocket
                   (per-drone +     /custody   broadcast
                    merged)         /health    to clients
```

### Remote Tool Execution Flow

```
Remote Client ──NMCP──▶ RemoteConnector
                              │
                    ┌─────────┼─────────┐
                    ▼         ▼         ▼
              CustodyAudit  McpServer  Tool Execution
              (log remote     (route    (screen, input,
               tool call)     to tool)   files, etc.)
                    │                      │
                    ▼                      ▼
              CustodyRecord          Tool Result
              (cross_machine)            │
                    │                    ▼
                    └──────────▶ RemoteConnector ──NMCP──▶ Remote Client
```

## Dependency Graph

```
Drone.Agent
├── Drone.Core
├── Drone.Services
│   ├── Drone.Core
│   └── Drone.System
├── Drone.MCP
│   ├── Drone.Core
│   └── Drone.System
├── Drone.Autonomy
│   └── Drone.Core
├── Drone.System
│   └── Drone.Core
└── Drone.Native
    └── Drone.Core

Drone.Custody (standalone)
└── Drone.Core

Drone.Tests
├── Drone.Core
├── Drone.MCP
├── Drone.Autonomy
└── Drone.Services

Drone.E2E
├── Drone.Core
├── Drone.Services
├── Drone.MCP
└── Drone.Custody
```

## Threading Model

| Component | Threading | Notes |
|-----------|-----------|-------|
| `CustodyChain` | `lock` on all mutations | Thread-safe sequence + hash tracking |
| `CustodyAuditLogger` | `lock` for file writes, `lock` for ring buffer | Separate locks for file I/O and buffer |
| `CorrelationTracker` | `ConcurrentDictionary` | Lock-free reads, atomic step increment |
| `CustodyReporter` | Background `Task.Run` | Flush loop with cancellation token |
| `CustodyServerHost` | `HttpListener` async loop | WebSocket handlers are async |
| `CustodyLogStore` | `lock` on all mutations | Single writer per drone file |
| `McpServer` | Async WebSocket handlers | One task per client connection |
| `EventBus` | `ConcurrentDictionary` of subscribers | Fire-and-forget event dispatch |

## Configuration Hierarchy

1. **Environment variables** (highest priority) — `DRONE_ID`, `DRONE_MODE`, `DRONE_WS_URL`, etc.
2. **appsettings.json** — `Drone.Agent/appsettings.json`
3. **Defaults** — Hardcoded in `DroneConfig`

See [Configuration Reference](configuration.md) for the full list.
