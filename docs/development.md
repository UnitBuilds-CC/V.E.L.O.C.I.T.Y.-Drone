# Development Guide

**Project:** Velocity Drone

---

## Prerequisites

- **.NET 10 Preview SDK** — [Download](https://dotnet.microsoft.com/download/dotnet/10.0)
- **Git** — For version control
- **Visual Studio 2022** or **VS Code** — Recommended IDEs
- **Rust toolchain** (optional) — Only needed if modifying `Drone.Native`

## Getting Started

```bash
# Clone the repository
git clone https://github.com/UnitBuilds-CC/V.E.L.O.C.I.T.Y.-Drone.git
cd V.E.L.O.C.I.T.Y.-Drone

# Restore dependencies
dotnet restore VelocityDrone.slnx

# Build all projects
dotnet build VelocityDrone.slnx -c Debug /p:SkipRust=true

# Run unit tests
dotnet test tests/Drone.Tests/Drone.Tests.csproj

# Run E2E tests
dotnet run --project tests/Drone.E2E/Drone.E2E.csproj

# Start the agent (Windows)
dotnet run --project Drone.Agent/Drone.Agent.csproj
```

## Solution Structure

The solution file `VelocityDrone.slnx` includes all projects:

```
VelocityDrone.slnx
├── Drone.Agent/              # Entry point (Windows tray app)
├── Drone.Core/               # Shared types, protocol, custody primitives
├── Drone.Services/           # Connectors, background services
├── Drone.MCP/                # MCP server, tool registration
├── Drone.System/             # Platform abstractions (screen, input, window)
├── Drone.Autonomy/           # Rule engine, behavior rules
├── Drone.Native/             # Rust FFI (delta frames, WebP)
├── Drone.Custody/            # Standalone custody server
├── tests/Drone.Tests/        # Unit tests (52 tests)
├── tests/Drone.E2E/          # E2E integration tests (10 tests)
└── DeltaBench/               # Delta frame benchmarks
```

## Building

### Full Solution

```bash
dotnet build VelocityDrone.slnx -c Release /p:SkipRust=true
```

The `/p:SkipRust=true` flag skips the Rust native build (required on CI where Rust toolchain isn't available).

### Individual Projects

```bash
# Build just the core library
dotnet build Drone.Core/Drone.Core.csproj

# Build the custody server
dotnet build Drone.Custody/Drone.Custody.csproj

# Build the agent
dotnet build Drone.Agent/Drone.Agent.csproj
```

### Skip Rust Native Build

If you don't have the Rust toolchain installed, use:
```bash
dotnet build VelocityDrone.slnx /p:SkipRust=true
```

This skips `Drone.Native` Rust compilation but keeps the C# wrapper classes.

## Testing

### Unit Tests

```bash
# Run all unit tests
dotnet test tests/Drone.Tests/Drone.Tests.csproj

# Run with verbose output
dotnet test tests/Drone.Tests/Drone.Tests.csproj --verbosity normal

# Run specific test class
dotnet test tests/Drone.Tests/Drone.Tests.csproj --filter "FullyQualifiedName~CustodyRecordTests"

# Run with coverage (requires coverlet)
dotnet test tests/Drone.Tests/Drone.Tests.csproj /p:CollectCoverage=true
```

**52 tests across 8 test classes:**

| Test Class | Tests | Module |
|-----------|-------|--------|
| `CustodyRecordTests` | 6 | Hash computation, tamper detection, chain verification, JSON round-trip |
| `CustodyChainTests` | 7 | Sequence increment, hash chaining, validation, removal/reorder, state reset |
| `CorrelationTrackerTests` | 5 | ID generation, step counting, completion, active tracking |
| `CustodyAuditLoggerTests` | 6 | Chained records, ring buffer, sequence filtering, events, cross-machine |
| `CoreTests` | 8 | NMCP frame protocol, config validation |
| `McpServerTests` | 6 | Tool registration, JSON-RPC, error handling |
| `BehaviorRuleTests` | 5 | Trigger matching, conditions, action params |
| `EventBusTests` | 4 | Pub/sub, filtering, error isolation |

### E2E Tests

```bash
dotnet run --project tests/Drone.E2E/Drone.E2E.csproj
```

**10 tests + 3 skipped (on Windows without admin):**

| Test | Description |
|------|-------------|
| Test 1 | MCP handshake |
| Test 2 | Tool call execution |
| Test 3 | WebSocket transport |
| Test 4 | Authentication flow |
| Test 5 | Screen capture pipeline |
| Test 6 | File operations |
| Test 7 | Remote connector |
| Test 8 | Messenger integration |
| Test 9 | Autonomy engine |
| Test 10 | Benchmark suite |
| Test 11 | Full custody trail pipeline |

Tests 8-10 may be skipped on Windows due to `HttpListener` requiring admin privileges for port binding.

## Project Conventions

### Namespace Convention

- `Drone.Core.*` — Core types and abstractions
- `Drone.Services.*` — Network connectors and services
- `Drone.MCP.*` — MCP protocol implementation
- `Drone.System.*` — Platform-specific implementations
- `Drone.Autonomy.*` — Autonomous behavior engine
- `Drone.Custody.*` — Custody server components

### File Organization

Each project follows this structure:
```
Drone.X/
├── Drone.X.csproj
├── SubFolder/
│   └── SomeComponent.cs
└── SomeOtherComponent.cs
```

### Coding Standards

- **C# 12+ features** — File-scoped namespaces, primary constructors, pattern matching
- **Async/await** — All I/O operations are async
- **Thread safety** — Use `lock` for mutable shared state, `ConcurrentDictionary` for lock-free scenarios
- **Error handling** — Custody operations never throw — failures are caught and logged
- **JSON** — Use `System.Text.Json` with `[JsonPropertyName]` attributes
- **Logging** — Use `ILogger` abstraction from `Drone.Core`

### Adding a New Tool

1. Implement the tool logic in `Drone.System` (platform-specific) or `Drone.Services`
2. Register it in `SystemToolRegistrar.RegisterTools()` in `Drone.MCP`
3. Add custody logging in `Drone.Agent/Program.cs` command handler
4. Add unit tests in `Drone.Tests`

### Adding a New Custody Event Type

1. Add a logging method to `CustodyAuditLogger` (e.g., `LogMyEvent()`)
2. Call it from the appropriate location in the codebase
3. Add unit tests in `CustodyAuditLoggerTests`

### Adding a New NMCP Frame Type

1. Add the constant to `NmcpFrameTypes` in `Drone.Core/Protocol/NmcpFrame.cs`
2. Document it in the [NMCP Protocol Spec](nmcp-protocol.md)
3. Implement handling in the appropriate connector/server

## Debugging

### Local Agent

```bash
# Run with debug logging
set DRONE_MCP_URL=http://+:9100
dotnet run --project Drone.Agent/Drone.Agent.csproj
```

### CustodyServer

```bash
# Run with verbose output
set CUSTODY_LISTEN_URL=http://localhost:5010/
dotnet run --project Drone.Custody/Drone.Custody.csproj
```

### Inspecting Custody Records

Custody records are stored as JSON-lines files:
```bash
# View latest records
tail -20 ./custody/drone-custody-2026-08-14.jsonl

# Verify chain integrity
dotnet run --project tests/Drone.E2E/Drone.E2E.csproj  # Test 11 verifies chain
```

### Query CustodyServer

```bash
# Health check
curl http://localhost:5010/health

# Query by drone
curl "http://localhost:5010/custody?drone=Drone"

# Query by time range
curl "http://localhost:5010/custody?from=2026-08-14T00:00:00Z&to=2026-08-14T23:59:59Z"

# Query by correlation ID
curl "http://localhost:5010/custody?correlation=corr-abc123"
```

## Git Workflow

- **Branch:** `main` is the default branch
- **Commits:** Conventional commits format (`feat:`, `fix:`, `docs:`, `refactor:`)
- **CI:** GitHub Actions builds on every push to `main`
- **CD:** Azure deployment triggers on push when relevant paths change

```bash
# Standard workflow
git checkout -b feature/my-feature
# ... make changes ...
dotnet build VelocityDrone.slnx /p:SkipRust=true
dotnet test tests/Drone.Tests/Drone.Tests.csproj
git add -A
git commit -m "feat: add my feature"
git push origin feature/my-feature
```

## Known Warnings

| Warning | Source | Action |
|---------|--------|--------|
| `CS0168` (unused variable `ex`) | `Drone.MCP/McpServer.cs:196` | Pre-existing, non-blocking |
| `NETSDK1057` (.NET preview) | All projects | Expected — using .NET 10 preview |
