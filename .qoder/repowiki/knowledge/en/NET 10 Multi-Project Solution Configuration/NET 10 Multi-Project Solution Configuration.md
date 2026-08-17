---
kind: build_configuration
name: .NET 10 Multi-Project Solution Configuration
category: build_configuration
scope:
    - '**'
source_files:
    - VelocityDrone.slnx
    - Drone.Agent/Drone.Agent.csproj
    - Drone.Core/Drone.Core.csproj
    - Drone.Services/Drone.Services.csproj
    - Drone.MCP/Drone.MCP.csproj
    - Drone.System/Drone.System.csproj
    - Drone.Autonomy/Drone.Autonomy.csproj
    - Drone.Native/Drone.Native.csproj
    - Drone.Custody/Drone.Custody.csproj
---

The Velocity Drone project uses a .NET 10 preview multi-project solution (`VelocityDrone.slnx`) with 10 distinct projects organized by responsibility:

- **Entry Points (2):** `Drone.Agent` (Windows tray app) and `Drone.Custody` (standalone server) — both produce executables
- **Core Libraries (6):** `Drone.Core` (zero-dependency foundation), `Drone.Services` (connectors), `Drone.MCP` (tool server), `Drone.System` (platform abstractions), `Drone.Autonomy` (rule engine), `Drone.Native` (Rust FFI)
- **Test Projects (2):** `Drone.Tests` (52 xUnit tests) and `Drone.E2E` (10 integration tests)
- **Benchmarking (1):** `DeltaBench` for delta frame performance testing

**Build Configuration:**
- Uses `/p:SkipRust=true` to bypass Rust native compilation when not needed
- Target framework: .NET 10 preview (`net10.0`)
- Output types: `Exe` for agents/servers, `Library` for shared code
- Nullable reference types enabled across all projects
- Implicit usings enabled

**Dependency Flow:**
- `Drone.Core` has zero project dependencies (foundation layer)
- All other libraries depend on `Drone.Core`
- `Drone.Agent` depends on all libraries (composition root)
- Test projects reference the libraries they test

**Platform Targets:**
- Windows: Primary (WinForms tray app)
- Linux/macOS: Headless via Docker (no tray UI)
- Cross-platform abstractions in `Drone.System` with runtime detection
