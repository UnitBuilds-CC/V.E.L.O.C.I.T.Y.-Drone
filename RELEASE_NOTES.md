# Velocity Drone v1.0.0 — Release Notes

**Release Date:** 2026-08-28
**Tag:** `v1.0.0`
**Commits:** 15 since initial release
**Files Changed:** 34+ files, 959+ insertions

---

## Overview

Velocity Drone v1.0 is a production-ready autonomous agent for remote system management. It runs as a background tray agent on Windows (or headless via Docker on any OS), exposing tools via MCP (Model Context Protocol) over the NMCP binary protocol. This release includes a comprehensive security hardening pass, Docker support, and full documentation.

---

## Highlights

- **Security hardened** — 3 dedicated audit passes, every finding closed
- **Docker-ready** — headless agent for cloud VMs, health checks, non-root container
- **181 tests passing** — unit, integration, concurrency stress, E2E
- **Zero warnings** — clean build on .NET 10 preview
- **Fully documented** — 9 docs covering architecture, configuration, deployment, hardening

---

## New Features

### Headless Agent (`Drone.Agent.Headless`)
- Cross-platform entry point with no WinForms dependency
- Runs on Linux/Docker without Windows Desktop runtime
- Same core functionality as the tray app: MCP server, relay, custody trail, autonomy engine

### Relay Server Architecture
- Unified relay server for drone-to-drone communication
- Messenger relay, file share, and remote bridge on a single port
- Per-drone rate limiting with token bucket algorithm
- Configurable concurrency limit (64 concurrent requests)

### Custody Trail
- Tamper-evident, hash-chained audit trail for every agent action
- Length-prefixed binary hash encoding (prevents field boundary ambiguity)
- Merkle batch verification for O(log N) integrity checks
- Constant-time hash comparison throughout (prevents timing side-channels)
- Local JSON-lines storage with offline resilience
- Real-time streaming to central CustodyServer

### TLS Enforcement
- MCP WebSocket server refuses to start without TLS unless explicitly opted out
- Set `DRONE_ALLOW_INSECURE_HTTP=1` for development/reverse proxy setups

### Self-Update with Integrity Verification
- Requires `.sha256` sidecar file alongside new binary
- SHA-256 checksum verified before applying update
- Rejects update if checksum file missing or mismatched

---

## Security

### Authentication & Authorization
- Constant-time API key comparison everywhere (`CryptographicOperations.FixedTimeEquals`)
- MCP bearer token authentication
- Secrets removed from URL query strings (header-only auth)
- Temporary MCP tokens written to stderr (not stdout/logs)

### Input Validation
- Shell command blocklist expanded: `| & ; \` $ ( ) > < ^ % ! \n \r` + null bytes + 8192 char limit
- Click coordinate bounds: -10000 to 10000
- File path validation: traversal protection + symlink/reparse point checks + null byte rejection
- Upload size limit: 100MB max with bounded read loop (prevents OOM DoS)
- WebSocket message size limits: 10-64MB depending on service

### Rate Limiting
| Component | Limit | Scope |
|-----------|-------|-------|
| MCP WebSocket | 20 req/s | Per client |
| EmbeddedFileServer | 30 req/s | Per IP |
| MessengerRelay | 30 msg/s (configurable) | Per drone |
| RelayServer | 64 concurrent | Global |

### WebSocket Safety
- Per-client `SemaphoreSlim` send locks on every WebSocket connection type
- Prevents concurrent `SendAsync` frame corruption
- Connection limits enforced (MCP: 16, Custody: 256, Relay: configurable)

### CORS & Security Headers
- No wildcard CORS on any endpoint
- `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Cache-Control: no-store` on all HTTP endpoints

### Supply Chain
- All GitHub Actions pinned to commit SHAs (not mutable tags)
- DLL integrity manifest: `Drone.Native/checksums.sha256`
- Dependabot configured for NuGet, GitHub Actions, and Cargo
- Deploy workflow requires build+test to pass first

### Error Handling
- Zero silent `catch {}` blocks in production code
- All exception handlers log the failure
- `AuditLogger` tracks failed write count
- Error responses sanitized (no stack traces or internal paths)

### Custody Trail Integrity
- Constant-time Merkle root verification in `CustodyBinarySerializer`, `CustodyChain`, `CustodyLogStore`
- Shared memory TOCTOU guards (re-verify state before committing transitions)
- Hash chain uses length-prefixed binary encoding (no delimiter ambiguity)

---

## Infrastructure

### Docker
- Multi-stage build: Rust native lib + .NET headless agent
- Non-root `drone` user
- Health check: `wget` against `/health/live`
- Graceful shutdown via SIGTERM with configurable timeout
- `DRONE_ALLOW_INSECURE_HTTP=1` for non-TLS operation

### CI/CD
- Build matrix: Ubuntu, Windows, macOS
- Unit tests + E2E tests in CI pipeline
- Docker build verification
- Deploy gated on build+test passing

### Configuration
- `global.json`: `rollForward: latestFeature` (prevents unexpected major upgrades)
- `appsettings.json`: no hardcoded secrets (empty placeholders)
- All sensitive values loaded from environment variables

---

## Testing

**181 tests passing, 0 failures, 0 warnings**

| Test Suite | Count | Coverage |
|-----------|-------|----------|
| CustodyRecordTests | 6 | Hash computation, tamper detection, chain verification |
| CustodyChainTests | 7 | Sequence, chaining, validation, removal/reorder |
| CorrelationTrackerTests | 5 | ID generation, step counting, completion |
| CustodyAuditLoggerTests | 6 | Chained records, ring buffer, filtering |
| CustodyLogStoreTests | 8 | Storage, validation, multi-drone, queries |
| CustodyQueryEngineTests | 5 | Query by drone/event, limit, verified trail |
| CoreTests | 8 | NMCP protocol, config validation |
| McpServerTests | 6 | Tool registration, JSON-RPC, auth |
| BehaviorRuleTests | 5 | Trigger matching, conditions |
| EventBusTests | 4 | Pub/sub, filtering, error isolation |
| CircuitBreakerTests | 12 | State transitions, thresholds, recovery |
| RelayServerTests | 20+ | File ops, rate limiting, auth, WebSocket E2E |
| SystemTests | 7 | Command execution, input validation, system info |
| NativeTests | 9 | FFI graceful degradation, Merkle validation |
| ConcurrencyTests | 5 | EventBus, CustodyChain, AuditLogger, LogStore stress |
| E2E Tests | 10 | MCP handshake, WebSocket transport, auth, custody |

---

## Documentation

| Document | Description |
|----------|-------------|
| [README](README.md) | Quick start, architecture, project map, configuration |
| [User Guide](docs/user-guide.md) | Getting started, commands, tools, workflows, integration |
| [Architecture](docs/architecture.md) | Module map, dependency graph, data flow, threading |
| [Configuration](docs/configuration.md) | Every setting, defaults, environment variables |
| [Deployment](docs/deployment.md) | Docker, Windows, CustodyServer, Azure, systemd |
| [Production Hardening](docs/production-hardening.md) | Security features, rate limiting, send locks, TLS |
| [Custody Trail](docs/custody-trail.md) | Full custody architecture, API reference |
| [NMCP Protocol](docs/nmcp-protocol.md) | Wire format, frame types, connection lifecycle |
| [Development](docs/development.md) | Building, testing, conventions, debugging |
| [Troubleshooting](docs/troubleshooting.md) | Common issues, diagnostics, FAQ |

---

## Projects

| Project | Description |
|---------|-------------|
| **Drone.Agent** | Windows tray app entry point |
| **Drone.Agent.Headless** | Cross-platform headless entry point (Docker/Linux) |
| **Drone.Core** | Shared types, NMCP protocol, custody primitives |
| **Drone.Services** | Connectors, relay server, background services |
| **Drone.MCP** | MCP server, tool registration, JSON-RPC 2.0 |
| **Drone.System** | Platform abstractions (screen, input, window, process) |
| **Drone.Autonomy** | Rule engine, event bus, behavior rules |
| **Drone.Native** | Rust FFI (delta frames, WebP, Merkle trees) |
| **Drone.Custody** | Standalone custody server |
| **Drone.Tests** | 181 xUnit tests |
| **Drone.E2E** | End-to-end integration tests |
| **DeltaBench** | Performance benchmarks |

---

## Breaking Changes

### CustodyRecord.ComputeHash()
Changed from pipe-delimited string encoding to length-prefixed binary encoding. Existing custody trails produced by pre-v1.0 agents will have different hash values. New trails are fully compatible.

### MCP WebSocket TLS
The MCP server now refuses to start with `http://` URLs unless `DRONE_ALLOW_INSECURE_HTTP=1` is set. Existing deployments using HTTP must either switch to HTTPS or set the environment variable.

### Self-Update
The update command now requires a `.sha256` sidecar file alongside the new binary. Updates without a checksum file are rejected.

### Relay Authentication
API keys are now accepted via `X-Api-Key` header only. Query string `?apikey=` parameter is no longer supported.

---

## Known Limitations

- MCP WebSocket binds to localhost inside Docker when running as non-root (HttpListener limitation on Linux). Health checks work correctly. External access requires running as root or using a reverse proxy.
- Command blocklist is bypass-resistant but not impossible to circumvent. An allowlist or sandboxed execution model would be stronger but changes the product behavior.

---

## Upgrade Guide

### From pre-v1.0

1. **Set `DRONE_ALLOW_INSECURE_HTTP=1`** if using HTTP MCP connections
2. **Update relay clients** to send API key via `X-Api-Key` header (not query string)
3. **Add `.sha256` files** alongside update binaries
4. **Verify DLL checksums** after clone: `cd Drone.Native && sha256sum -c checksums.sha256`
5. **Rebuild custody trails** if upgrading from pre-v1.0 (hash encoding changed)

---

## SHA-256 Checksums

Pre-built native DLL integrity:
```
475c906a248398cfd5c7acb87315fa54842a5a684d02b98a188cfb20676759c6  velocity_delta.dll
8cdb96279aa9f7d5a0f749b641b336be99335242432113a76f4d20200f7a4b4b  velocity_v2_ffi.dll
```

---

## Contributors

- UnitBuilds

---

## License

Proprietary — All rights reserved.
