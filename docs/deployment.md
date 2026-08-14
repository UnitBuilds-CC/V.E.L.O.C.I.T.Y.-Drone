# Deployment Guide

**Project:** Velocity Drone

---

## Deployment Options

| Option | Use Case | Platform |
|--------|----------|----------|
| **Docker (Headless)** | Cloud VMs, servers, CI/CD | Linux |
| **Windows Tray App** | Desktop machines, workstations | Windows 10/11 |
| **CustodyServer** | Central audit trail collection | Any (cross-platform) |

## Docker Deployment (Recommended for Servers)

### Build

```bash
docker build -t velocity-drone:latest .
```

The Dockerfile uses multi-stage build:
1. **Build stage** — .NET 10 preview SDK, compiles all projects with `/p:SkipRust=true`
2. **Runtime stage** — .NET 10 preview ASP.NET runtime, installs Linux input tools

### Run

```bash
docker run -d --name drone \
  -e DRONE_MODE=headless \
  -e DRONE_MCP_URL=http://0.0.0.0:9100 \
  -e DRONE_MCP_TOKEN=your-secret-token \
  -e DRONE_WS_URL=wss://your-host/ws/drone \
  -e DRONE_ALLOWED_PATHS=/data \
  -p 9100:9100 \
  -v /data:/data \
  velocity-drone:latest
```

### Docker Compose

```yaml
version: "3.8"
services:
  drone:
    image: velocity-drone:latest
    environment:
      - DRONE_MODE=headless
      - DRONE_MCP_URL=http://0.0.0.0:9100
      - DRONE_MCP_TOKEN=${MCP_TOKEN}
      - DRONE_WS_URL=${WS_URL}
      - DRONE_ALLOWED_PATHS=/data
    ports:
      - "9100:9100"
    volumes:
      - drone-data:/data
    restart: unless-stopped

  custody:
    image: velocity-drone:latest
    entrypoint: ["dotnet", "Drone.Custody.dll"]
    environment:
      - CUSTODY_STORAGE_PATH=/custody-data
      - CUSTODY_LISTEN_URL=http://+:5010/
    ports:
      - "5010:5010"
    volumes:
      - custody-data:/custody-data
    restart: unless-stopped

volumes:
  drone-data:
  custody-data:
```

### Health Check

The Docker image includes a health check:
```
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3
  CMD curl -f http://localhost:9100/health || exit 1
```

### Volumes

| Path | Purpose |
|------|---------|
| `/data` | Shared files storage (upload/download) |
| `/data/audit/` | Audit log files |

## Windows Tray App Deployment

### Prerequisites

- Windows 10/11
- .NET 10 preview runtime (or publish as self-contained)

### Publish

```bash
dotnet publish Drone.Agent/Drone.Agent.csproj -c Release -o ./publish
```

For self-contained (no .NET runtime required on target):
```bash
dotnet publish Drone.Agent/Drone.Agent.csproj -c Release -r win-x64 --self-contained -o ./publish
```

### Run

Double-click `velocity-drone.exe` or run from command line:
```cmd
velocity-drone.exe
```

The agent appears in the system tray. Right-click for status and options.

### Auto-Start

To start the drone on Windows login:
1. Create a shortcut to `velocity-drone.exe`
2. Place it in `shell:startup` (press Win+R, type `shell:startup`, press Enter)

### Environment Variables

Set via System Properties → Environment Variables, or in a wrapper script:
```cmd
set DRONE_ID=workstation-1
set DRONE_WS_URL=wss://server:9000/ws/drone
set DRONE_MCP_TOKEN=secret
set DRONE_CUSTODY_PATH=C:\drone-data\custody.jsonl
velocity-drone.exe
```

## CustodyServer Deployment

### Standalone

```bash
dotnet run --project Drone.Custody/Drone.Custody.csproj
```

### With Custom Configuration

```bash
CUSTODY_STORAGE_PATH=/var/custody-data \
CUSTODY_LISTEN_URL=http://0.0.0.0:5010/ \
dotnet run --project Drone.Custody/Drone.Custody.csproj
```

### Docker

```bash
docker build -t velocity-custody:latest -f Dockerfile.custody .
docker run -d --name custody \
  -e CUSTODY_STORAGE_PATH=/custody-data \
  -e CUSTODY_LISTEN_URL=http://+:5010/ \
  -p 5010:5010 \
  -v /var/custody-data:/custody-data \
  velocity-custody:latest
```

### Systemd Service (Linux)

```ini
[Unit]
Description=Velocity Custody Server
After=network.target

[Service]
Type=simple
User=velocity
WorkingDirectory=/opt/velocity-custody
ExecStart=/usr/bin/dotnet Drone.Custody.dll
Environment=CUSTODY_STORAGE_PATH=/var/lib/custody
Environment=CUSTODY_LISTEN_URL=http://+:5010/
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
```

### Azure Deployment

The `deploy.yml` workflow handles Azure deployment:
1. Builds Docker image
2. Pushes to Azure Container Registry
3. SSH into VM and pulls the new image

Required GitHub secrets:
| Secret | Description |
|--------|-------------|
| `AZURE_CREDENTIALS` | Azure service principal credentials |
| `ACR_SERVER` | Azure Container Registry server URL |
| `ACR_USERNAME` | ACR username |
| `ACR_PASSWORD` | ACR password |
| `VM_HOST` | Target VM hostname/IP |
| `VM_USER` | SSH username |
| `VM_SSH_KEY` | SSH private key |

## Network Requirements

### Drone Agent

| Direction | Port | Protocol | Purpose |
|-----------|------|----------|---------|
| Outbound | 443/9000 | WebSocket | Uplink connection |
| Outbound | 443 | HTTPS | Messenger connection |
| Inbound | 9100 | WebSocket | MCP server (tool access) |
| Inbound | 5003 | HTTP | Share file server |
| Outbound | 5010 | WebSocket | CustodyServer streaming |

### CustodyServer

| Direction | Port | Protocol | Purpose |
|-----------|------|----------|---------|
| Inbound | 5010 | HTTP/WebSocket | Query API + drone streaming |

## Security Checklist

- [ ] Set `DRONE_MCP_TOKEN` to a strong random value
- [ ] Set `DRONE_ALLOWED_PATHS` to restrict file access
- [ ] Use TLS for WebSocket connections (`wss://`)
- [ ] Store custody logs in a directory with restricted permissions
- [ ] Rotate `ConnectionSecret` and `AdminApiKey` regularly
- [ ] Monitor custody trail for chain breaks (indicates tampering)
- [ ] Keep .NET runtime updated
