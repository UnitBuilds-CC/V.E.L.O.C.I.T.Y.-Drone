# Development Guide

<cite>
**Referenced Files in This Document**
- [VelocityDrone.slnx](file://VelocityDrone.slnx)
- [Drone.Agent/Program.cs](file://Drone.Agent/Program.cs)
- [Drone.Core/Drone.Core.csproj](file://Drone.Core/Drone.Core.csproj)
- [tests/Drone.Tests/Drone.Tests.csproj](file://tests/Drone.Tests/Drone.Tests.csproj)
- [docs/development.md](file://docs/development.md)
</cite>

## Table of Contents
1. [Introduction](#introduction)
2. [Development Environment Setup](#development-environment-setup)
3. [Build System](#build-system)
4. [Code Style and Conventions](#code-style-and-conventions)
5. [Testing Guidelines](#testing-guidelines)
6. [Debugging Techniques](#debugging-techniques)
7. [Adding New MCP Tools](#adding-new-mcp-tools)
8. [Working with Custody Trail](#working-with-custody-trail)
9. [Cross-Platform Development](#cross-platform-development)
10. [Performance Considerations](#performance-considerations)

## Introduction

This guide explains how to set up a development environment, build and test Velocity Drone, follow code style and architecture conventions, debug and profile the application, and contribute new features or extensions.

## Development Environment Setup

### Required Tools

- **.NET 10 preview SDK** — Install from https://dotnet.microsoft.com/download/dotnet/10.0
- **Visual Studio 2022** or **VS Code** with C# Dev Kit
- **Git** — For version control
- **Rust toolchain** (optional) — Only needed if modifying Drone.Native

### IDE Configuration

**Visual Studio 2022:**
- Enable ".NET desktop development" workload
- Enable "ASP.NET and web development" workload (for Docker)

**VS Code:**
- Install C# Dev Kit extension
- Install C# extension
- Recommended settings:
```json
{
    "dotnet.defaultSolution": "VelocityDrone.slnx",
    "editor.formatOnSave": true,
    "csharp.suppressBuildAssetsNudge": true
}
```

## Build System

### Solution Structure

The solution (`VelocityDrone.slnx`) contains 10 projects:

| Project | Type | Description |
|---------|------|-------------|
| Drone.Agent | Exe | Entry point, tray app |
| Drone.Core | Library | Shared types, protocol |
| Drone.Services | Library | Connectors, background services |
| Drone.MCP | Library | MCP server, JSON-RPC |
| Drone.System | Library | Platform abstractions |
| Drone.Autonomy | Library | Rule engine |
| Drone.Native | Library | Rust FFI bindings |
| Drone.Custody | Exe | Standalone custody server |
| Drone.Tests | Test | xUnit tests |
| Drone.E2E | Test | Integration tests |
| DeltaBench | Exe | Benchmarks |

### Build Commands

```bash
# Full build (skip Rust if not needed)
dotnet build VelocityDrone.slnx /p:SkipRust=true

# Build single project
dotnet build Drone.Core/Drone.Core.csproj

# Release build
dotnet build VelocityDrone.slnx -c Release

# Clean and rebuild
dotnet clean; dotnet build VelocityDrone.slnx /p:SkipRust=true
```

### Build Output Directories

- `build-test/` — Test build output
- `build-v2/`, `build-v3/` — Versioned build outputs
- `publish/` — Published release builds
- `publish-new/` — New release candidate

## Code Style and Conventions

### General Guidelines

- **Naming:** PascalCase for types/methods, camelCase for parameters/locals, _underscore for private fields
- **Namespaces:** File-scoped namespaces (`namespace X;`)
- **Implicit usings:** Enabled in all projects
- **Nullable reference types:** Enabled — use `?` for nullable references

### Project-Specific Conventions

**Drone.Core:**
- Zero external dependencies (only System.* namespaces)
- All types should be public if used by other projects
- Use records for data transfer objects

**Drone.Services:**
- Connectors should implement reconnection logic
- Use `ILogger` from Core for logging
- Background services should accept `CancellationToken`

**Drone.MCP:**
- Tool methods should be async
- JSON-RPC errors should follow spec (code, message, data)
- Use `NmcpFrame` for binary protocol

### Example: Adding a New Service

```csharp
// Drone.Services/MyService/MyConnector.cs
using Drone.Core;

namespace Drone.Services.MyService;

public class MyConnector
{
    private readonly MyConfig _config;
    private readonly ILogger _logger;
    
    public MyConnector(MyConfig config, ILogger logger)
    {
        _config = config;
        _logger = logger;
    }
    
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Connecting to {Url}", _config.Url);
        // Connection logic with retry
    }
}
```

## Testing Guidelines

### Test Organization

```
tests/
├── Drone.Tests/           # Unit tests (52 tests)
│   ├── CustodyRecordTests.cs
│   ├── CustodyChainTests.cs
│   ├── CorrelationTrackerTests.cs
│   ├── CustodyAuditLoggerTests.cs
│   ├── CoreTests.cs
│   ├── McpServerTests.cs
│   ├── BehaviorRuleTests.cs
│   └── EventBusTests.cs
└── Drone.E2E/             # Integration tests (10 tests)
    └── Program.cs
```

### Writing Unit Tests

```csharp
using Xunit;
using Drone.Core.Custody;

namespace Drone.Tests;

public class MyFeatureTests
{
    [Fact]
    public void Test_HashComputation_IsDeterministic()
    {
        // Arrange
        var record = new CustodyRecord { /* ... */ };
        
        // Act
        var hash1 = record.ComputeHash();
        var hash2 = record.ComputeHash();
        
        // Assert
        Assert.Equal(hash1, hash2);
    }
    
    [Theory]
    [InlineData("event1", "drone-a")]
    [InlineData("event2", "drone-b")]
    public void Test_Record_CreatesValidChain(string eventType, string droneId)
    {
        // Test with multiple inputs
    }
}
```

### Running Tests

```bash
# Run all unit tests
dotnet test tests/Drone.Tests/Drone.Tests.csproj

# Run with coverage (requires coverlet)
dotnet test /p:CollectCoverage=true

# Run specific test class
dotnet test --filter "FullyQualifiedName~CustodyChainTests"

# Run E2E tests
dotnet run --project tests/Drone.E2E/Drone.E2E.csproj
```

### Test Coverage Goals

| Component | Target Coverage |
|-----------|-----------------|
| Drone.Core | >90% |
| Drone.MCP | >80% |
| Drone.Autonomy | >80% |
| Drone.Services | >70% |

## Debugging Techniques

### Logging

The agent uses a custom `DroneLogger` that wraps `Microsoft.Extensions.Logging`:

```csharp
logger.LogInformation("Processing request {Id}", requestId);
logger.LogWarning("Connection unstable, retrying...");
logger.LogError("Fatal error: {Error}", ex.Message);
logger.LogDebug("Detailed state: {@State}", stateObject);
```

### Tray App Logging

The tray app displays logs in the notification area. Right-click the tray icon to view the log window.

### Debugging Headless Mode

```bash
# Set debug environment
set DRONE_MODE=headless
set DOTNET_ENVIRONMENT=Development
dotnet run --project Drone.Agent/Drone.Agent.csproj
```

### Attaching Debugger

1. Start the agent
2. In Visual Studio: Debug > Attach to Process > select `velocity-drone.exe`
3. Set breakpoints in code

### Common Debug Scenarios

**MCP Tool not being called:**
- Check tool registration in `SystemToolRegistrar.RegisterAll()`
- Verify tool name matches exactly (case-sensitive)
- Check MCP server logs for JSON-RPC errors

**Custody chain broken:**
- Check `CustodyAuditLogger.LoadPersistedRecords()` succeeded
- Verify `DRONE_CUSTODY_PATH` is writable
- Check for concurrent writes to custody file

**WebSocket connection failing:**
- Verify URL format (`ws://` or `wss://`)
- Check firewall rules for port
- Enable debug logging: `set DRONE_LOG_LEVEL=Debug`

## Adding New MCP Tools

### Step 1: Define Tool in Registrar

```csharp
// Drone.MCP/Tools/SystemToolRegistrar.cs
public static void RegisterAll(McpServer server, /* dependencies */)
{
    // Existing tools...
    
    server.RegisterTool("my_new_tool", "Description of what the tool does", 
        new[] { 
            new ToolParameter("param1", "string", "Description", required: true),
            new ToolParameter("param2", "integer", "Description", required: false)
        },
        async (args) =>
        {
            var param1 = args.GetProperty("param1").GetString();
            var param2 = args.GetProperty("param2").GetInt32();
            
            // Tool logic here
            var result = await DoSomethingAsync(param1, param2);
            
            return JsonDocument.Parse(JsonSerializer.Serialize(new { success = true, result }));
        });
}
```

### Step 2: Add Tests

```csharp
[Fact]
public async Task McpServer_MyNewTool_ReturnsExpectedResult()
{
    var server = new McpServer(new NullLogger());
    SystemToolRegistrar.RegisterAll(server, /* mocks */);
    
    var args = JsonDocument.Parse(@"{""param1"":""test"",""param2"":42}").RootElement;
    var result = await server.InvokeToolAsync("my_new_tool", args);
    
    Assert.Contains("success", result.ToString());
}
```

### Step 3: Update Messenger Commands (Optional)

If the tool should be accessible via Messenger:

```csharp
// Drone.Agent/Program.cs - Messenger command handler
else if (cmd.StartsWith("mycommand ", StringComparison.OrdinalIgnoreCase))
{
    var param = cmd.Substring(10);
    var argsJson = JsonSerializer.Serialize(new { param1 = param });
    var result = await mcpServer.InvokeToolAsync("my_new_tool", 
        JsonDocument.Parse(argsJson).RootElement);
    response = $"Result: {result}";
}
```

## Working with Custody Trail

### Logging Actions

```csharp
// Log tool calls
custodyLogger.LogToolCall("tool_name", "param=value", targetSystem: "system");

// Log connections
custodyLogger.LogConnection("connected", "messenger", "server-url", success: true);

// Log custom events
custodyLogger.LogEvent("custom_event", new { data = "value" });
```

### Querying Custody Data

```csharp
// Query via HTTP (CustodyServer must be running)
GET /custody?drone=my-drone&from=2024-01-01T00:00:00Z&to=2024-01-02T00:00:00Z
GET /custody?correlation=corr-abc123
GET /custody?eventType=tool_call
```

### Verifying Chain Integrity

```csharp
var chain = new CustodyChain();
chain.AddRecord(record1);
chain.AddRecord(record2);

bool isValid = chain.VerifyChain();  // Returns false if tampered
```

## Cross-Platform Development

### Platform Detection

```csharp
// Drone.System/PlatformFactory.cs
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    return new WindowsScreenCapture();
else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    return new LinuxScreenCapture();
else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    return new MacOsScreenCapture();
```

### Testing Platform-Specific Code

- Use WSL2 for Linux testing on Windows
- Use Docker for headless Linux testing
- macOS testing requires actual Mac (or VM)

## Performance Considerations

### Memory Management

- Use `ArrayPool<T>` for large temporary buffers
- Dispose `IDisposable` objects promptly
- Use `IAsyncDisposable` for async cleanup

### Async Patterns

```csharp
// Good: Proper cancellation
public async Task ProcessAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        await DoWorkAsync(ct);
    }
}

// Bad: Fire-and-forget without error handling
_ = Task.Run(async () => await DoWorkAsync());  // Avoid
```

### WebSocket Performance

- Use `SemaphoreSlim` to serialize concurrent sends
- Set send timeouts to prevent slow-client hangs
- Buffer large messages before sending

## Troubleshooting Build Issues

### Common Errors

**"The .NET 10 SDK is not installed"**
- Download from https://dotnet.microsoft.com/download/dotnet/10.0
- Verify with `dotnet --version`

**"Rust FFI build failed"**
- Install Rust: `winget install Rustlang.Rustup`
- Or skip Rust: `dotnet build /p:SkipRust=true`

**"Circular dependency detected"**
- Check project references — Drone.Core should have no project references
- Services should depend on Core, not vice versa

## Next Steps

- Read [Architecture](file://docs/architecture.md) for system design
- Read [Custody Trail](file://docs/custody-trail.md) for audit system
- Read [NMCP Protocol](file://docs/nmcp-protocol.md) for wire format
