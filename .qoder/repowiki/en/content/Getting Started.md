# Getting Started

<cite>
**Referenced Files in This Document**
- [README.md](file://README.md)
- [VelocityDrone.slnx](file://VelocityDrone.slnx)
- [Drone.Agent/Program.cs](file://Drone.Agent/Program.cs)
- [Drone.Agent/appsettings.json](file://Drone.Agent/appsettings.json)
- [docs/architecture.md](file://docs/architecture.md)
- [docs/configuration.md](file://docs/configuration.md)
</cite>

## Table of Contents
1. [Introduction](#introduction)
2. [Prerequisites](#prerequisites)
3. [Project Structure](#project-structure)
4. [Building the Project](#building-the-project)
5. [Running the Agent](#running-the-agent)
6. [Running the Custody Server](#running-the-custody-server)
7. [Testing](#testing)
8. [Configuration](#configuration)
9. [Deployment Options](#deployment-options)

## Introduction

Velocity Drone is an autonomous agent for remote system management. It runs as a background tray agent on Windows machines (or headless via Docker), exposing tools via MCP (Model Context Protocol) over the NMCP binary protocol. The agent is controlled remotely via Messenger commands, WebSocket uplink, or direct NMCP tool calls.

Key capabilities:
- **Screen capture and input simulation** — Remote control of mouse/keyboard
- **File operations** — Upload, download, list files via Share connector
- **Command execution** — Run shell commands remotely
- **Custody trail** — Tamper-evident, hash-chained audit trail for all actions
- **Autonomous behavior** — Rule engine with event-driven triggers

## Prerequisites

- **.NET 10 preview SDK** — Required for building all projects
- **Windows 10/11** — Primary platform (WinForms tray app)
- **Docker** — Optional, for headless deployment
- **Rust toolchain** — Optional, for native FFI (use `/p:SkipRust=true` to skip)

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
├── tests/
│   ├── Drone.Tests/        # 52 xUnit tests
│   └── Drone.E2E/          # E2E integration tests
└── docs/                   # Documentation
```

## Building the Project

```bash
# Build all projects (skip Rust native)
dotnet build VelocityDrone.slnx /p:SkipRust=true

# Build specific module
dotnet build Drone.Core/Drone.Core.csproj
dotnet build Drone.Agent/Drone.Agent.csproj

# Release build for deployment
dotnet publish Drone.Agent/Drone.Agent.csproj -c Release -o ./publish
```

## Running the Agent

```bash
# Run interactively
dotnet run --project Drone.Agent/Drone.Agent.csproj

# Run with custom config
set DRONE_ID=my-drone
set DRONE_MODE=full
set DRONE_WS_URL=ws://server:9000
dotnet run --project Drone.Agent/Drone.Agent.csproj
```

The agent starts a system tray icon (Windows) and initializes:
1. Custody trail (hash-chained audit logging)
2. Platform services (screen, input, window, process)
3. MCP server (WebSocket + NMCP buffer)
4. Connectors (Messenger, Remote, Share, Uplink)
5. Autonomy engine (rule evaluation)

## Running the Custody Server

```bash
# Run standalone custody server
dotnet run --project Drone.Custody/Drone.Custody.csproj

# Configure storage
set CUSTODY_STORAGE_PATH=C:\CustodyLogs
set CUSTODY_LISTEN_URL=http://+:5050
dotnet run --project Drone.Custody/Drone.Custody.csproj
```

The custody server provides:
- **WebSocket ingestion** — Receives custody reports from drones
- **HTTP query API** — `GET /custody?drone=X&from=T1&to=T2`
- **Real-time broadcast** — WebSocket stream of new records

## Testing

```bash
# Unit tests (52 tests)
dotnet test tests/Drone.Tests/Drone.Tests.csproj

# E2E integration tests (10 tests)
dotnet run --project tests/Drone.E2E/Drone.E2E.csproj
```

### Test Coverage

| Test Class | Tests | Covers |
|-----------|-------|--------|
| `CustodyRecordTests` | 6 | Hash computation, tamper detection, chain verification |
| `CustodyChainTests` | 7 | Sequence increment, hash chaining, validation |
| `CorrelationTrackerTests` | 5 | ID generation, step counting, completion |
| `CustodyAuditLoggerTests` | 6 | Chained records, ring buffer, filtering |
| `CoreTests` | 8 | NMCP frame protocol, config validation |
| `McpServerTests` | 6 | Tool registration, JSON-RPC protocol |
| `BehaviorRuleTests` | 5 | Trigger matching, conditions, action params |
| `EventBusTests` | 4 | Pub/sub, filtering, error isolation |
| `E2E Tests` | 10 | MCP handshake, WebSocket transport, auth |

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

See [Configuration Reference](file://docs/configuration.md) for every setting.

## Deployment Options

### Docker (Headless)

```bash
docker build -t velocity-drone:latest .
docker run -d -e DRONE_MODE=headless -e DRONE_MCP_TOKEN=secret -p 9100:9100 velocity-drone
```

### Windows Tray App

```bash
dotnet publish Drone.Agent/Drone.Agent.csproj -c Release -o ./publish
# Run velocity-drone.exe from publish folder
```

### CustodyServer (Central)

```bash
dotnet publish Drone.Custody/Drone.Custody.csproj -c Release -o ./publish-custody
# Run on central server
```

See [Deployment Guide](file://docs/deployment.md) for Docker Compose, systemd, and Azure deployment.

## Quick Commands Reference

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
| `benchmark` | Run performance benchmarks |
| `update` | Self-update from shared directory |

## Next Steps

- Read [Architecture](file://docs/architecture.md) for module details
- Read [Custody Trail](file://docs/custody-trail.md) for audit system design
- Read [Development Guide](file://docs/development.md) for contributing
