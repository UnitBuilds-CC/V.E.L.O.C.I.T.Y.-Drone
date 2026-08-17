# Architecture Overview

<cite>
**Referenced Files in This Document**
- [docs/architecture.md](file://docs/architecture.md)
- [Drone.Agent/Program.cs](file://Drone.Agent/Program.cs)
- [Drone.Core/VelocityConnection.cs](file://Drone.Core/VelocityConnection.cs)
- [Drone.MCP/McpServer.cs](file://Drone.MCP/McpServer.cs)
- [Drone.Core/Custody/CustodyChain.cs](file://Drone.Core/Custody/CustodyChain.cs)
</cite>

## Table of Contents
1. [System Overview](#system-overview)
2. [Module Map](#module-map)
3. [Project Responsibilities](#project-responsibilities)
4. [Dependency Graph](#dependency-graph)
5. [Data Flow Diagrams](#data-flow-diagrams)
6. [Threading Model](#threading-model)
7. [Configuration Hierarchy](#configuration-hierarchy)
8. [Transport Layers](#transport-layers)

## System Overview

Velocity Drone is a modular autonomous agent for remote system management. It runs as a background tray agent on Windows machines (or headless in Docker), exposing tools via MCP (Model Context Protocol) over the NMCP binary protocol.

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
```

## Module Map

| Module | Responsibility | Key Types |
|--------|---------------|-----------|
| **Drone.Agent** | Entry point, DI wiring, tray app, command handler | `Program`, `TrayApp` |
| **Drone.Core** | Shared types, NMCP protocol, custody primitives | `ILogger`, `DroneConfig`, `EventBus`, `CustodyRecord`, `CustodyChain`, `NmcpFrame` |
| **Drone.Services** | Network connectors, background services | `MessengerConnector`, `RemoteConnector`, `ShareConnector`, `CustodyReporter` |
| **Drone.MCP** | MCP server, tool registration, JSON-RPC | `McpServer`, `SystemToolRegistrar` |
| **Drone.System** | Platform abstractions | `IScreenCapture`, `IInputSimulator`, `IWindowManager`, `IProcessManager`, `PlatformFactory` |
| **Drone.Autonomy** | Rule engine, behavior rules | `AutonomyEngine`, `BehaviorRule`, `ActionHandler` |
| **Drone.Native** | Rust FFI, delta frames, WebP | `DeltaEngine`, `DeltaFrameSerializer`, `WebpCompressor` |
| **Drone.Custody** | Standalone custody server | `CustodyLogStore`, `CustodyServerHost`, `CustodyQueryEngine` |

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
- **`ILogger`** — Minimal logging abstraction
- **`DroneConfig`** — Configuration model with validation
- **`EventBus`** — Pub/sub event system for autonomous behavior
- **NMCP protocol** — Binary frame format (16-byte header, big-endian), frame type registry
- **`VelocityConnection`** — WebSocket/shared-memory connection with NMCP buffer management
- **Custody primitives** — `CustodyRecord`, `CustodyChain`, `CorrelationTracker`, `CustodyAuditLogger`

### Drone.Services (Connectors & Background Services)

Network connectors and background services:
- **`MessengerConnector`** — WebSocket connection to Velocity Messenger
- **`RemoteConnector`** — NMCP/NDA connection for remote tool execution
- **`ShareConnector`** — HTTP/WebSocket connection to Share server
- **`CustodyReporter`** — Background service batching custody records
- **`DeltaScreenPipeline`** — Delta frame screen capture pipeline
- **`EmbeddedFileServer`** — Lightweight HTTP file server

### Drone.MCP (MCP Server)

Model Context Protocol implementation:
- **Tool registration** — `SystemToolRegistrar` registers platform tools
- **JSON-RPC 2.0** — Request/response/notification handling
- **NMCP framing** — Binary frame wrapping for tool calls/results
- **WebSocket transport** — Client connections over WebSocket
- **Buffer management** — File-backed NMCP buffer for offline resilience

### Drone.System (Platform Abstractions)

Cross-platform system interfaces:
- **`IScreenCapture`** — Screen capture (Win32 GDI+, Linux scrot/import, macOS screencapture)
- **`IInputSimulator`** — Keyboard/mouse simulation (Win32 SendInput, Linux xdotool, macOS cliclick)
- **`IWindowManager`** — Window enumeration and manipulation
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
- **C# wrappers** — `DeltaEngine`, `DeltaFrameSerializer`, `WebpCompressor`

### Drone.Custody (Standalone Server)

Central custody trail server:
- **`CustodyLogStore`** — Append-only JSON-lines storage
- **`CustodyServerHost`** — WebSocket server with HTTP query API
- **`CustodyQueryEngine`** — Flexible query by drone, time, correlation, event type

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

## Data Flow Diagrams

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
| `VelocityConnection` | `SemaphoreSlim` for WS sends | Serializes heartbeat and data sends |

## Configuration Hierarchy

1. **Environment variables** (highest priority) — `DRONE_ID`, `DRONE_MODE`, `DRONE_WS_URL`, etc.
2. **appsettings.json** — `Drone.Agent/appsettings.json`
3. **Defaults** — Hardcoded in `DroneConfig`

## Transport Layers

### WebSocket Transport

- **Protocol:** RFC 6455
- **Framing:** NMCP binary frames (16-byte header)
- **Reconnection:** Exponential backoff with attempt limit
- **Heartbeat:** 30-second interval

### Shared Memory Transport

- **Protocol:** Atomic state machine over memory-mapped files
- **Layout:** Request channel (4KB) + Response channel (61KB)
- **Polling:** 100μs spin-wait before yielding
- **Use case:** Local IPC with zero-copy semantics

### NMCP Frame Format

```
┌─────────────────────────────────────────────────────────┐
│  Type (4B)  │  Sequence (4B)  │  Length (4B)  │ Reserved (4B)  │  Payload...  │
└─────────────────────────────────────────────────────────┘
```

See [NMCP Protocol](file://docs/nmcp-protocol.md) for full specification.

## Next Steps

- Read [Custody Trail](file://docs/custody-trail.md) for audit system details
- Read [Development Guide](file://docs/development.md) for contributing
- Read [Configuration](file://docs/configuration.md) for all settings
