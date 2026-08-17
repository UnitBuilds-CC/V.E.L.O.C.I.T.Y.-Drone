# Velocity Drone — Multi-stage build for Linux (headless cloud VM or LAN)
# Usage:
#   docker build -t velocity-drone .
#   docker run -d --name drone \
#     -e DRONE_MODE=headless \
#     -e DRONE_MCP_URL=http://0.0.0.0:9100 \
#     -e DRONE_MCP_TOKEN=your-secret-token \
#     -e DRONE_WS_URL=wss://your-host/ws/drone \
#     -e DRONE_ALLOWED_PATHS=/data \
#     -p 9100:9100 \
#     velocity-drone

FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

# Copy project files for restore
COPY Drone.Core/*.csproj Drone.Core/
COPY Drone.System/*.csproj Drone.System/
COPY Drone.Services/*.csproj Drone.Services/
COPY Drone.MCP/*.csproj Drone.MCP/
COPY Drone.Autonomy/*.csproj Drone.Autonomy/
COPY Drone.Agent/*.csproj Drone.Agent/
COPY Drone.Custody/*.csproj Drone.Custody/
COPY Drone.Native/*.csproj Drone.Native/
COPY tests/Drone.Tests/*.csproj tests/Drone.Tests/
COPY tests/Drone.E2E/*.csproj tests/Drone.E2E/

# Restore (build projects individually — no solution file)
RUN dotnet restore Drone.Agent/Drone.Agent.csproj

# Copy source
COPY . .

# Build and publish
RUN dotnet publish Drone.Agent/Drone.Agent.csproj -c Release -o /app/publish --no-restore /p:SkipRust=true

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS runtime
WORKDIR /app

# Install system dependencies for Linux (screen/input tools when running in full mode)
RUN apt-get update && apt-get install -y --no-install-recommends \
    xdotool xclip wmctrl xrandr x11-utils imagemagick \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Default environment — headless mode for cloud VMs
# Override these at runtime with -e flags
ENV DRONE_MODE=headless
ENV DRONE_ID=Drone
ENV DRONE_MCP_URL=http://0.0.0.0:9100
ENV DRONE_MCP_TOKEN=""
ENV DRONE_MCP_TLS=0
ENV DRONE_AUDIT_LOG=/data/audit/drone-audit.jsonl
ENV DRONE_WS_URL=""
ENV DRONE_ALLOWED_PATHS=/data
ENV DRONE_SHUTDOWN_TIMEOUT=15
ENV DRONE_CUSTODY_PATH=/data/custody/drone-custody.jsonl
ENV Drone__Uplink__Transport=auto
ENV Drone__Uplink__BufferSize=4194304

# MCP WebSocket port
EXPOSE 9100

# Health check for cloud orchestrators — uses JSON health endpoint
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -sf http://localhost:9100/health | grep -q '"status":"healthy"' || exit 1

# Graceful shutdown signal
STOPSIGNAL SIGTERM

ENTRYPOINT ["dotnet", "velocity-drone.dll"]
