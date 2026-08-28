# ============================================================
# Velocity Drone — Multi-stage Docker Build
# Stage 1: Build Rust native libraries
# Stage 2: Build .NET application
# Stage 3: Minimal runtime image
# ============================================================

# --- Stage 1: Rust build ---
FROM rust:slim AS rust-builder
WORKDIR /build/rust
COPY Drone.Native/Cargo.toml Drone.Native/Cargo.lock* ./
COPY Drone.Native/src ./src
RUN cargo build --release && \
    cp target/release/libdrone_native.so /build/libdrone_native.so

# --- Stage 2: .NET build ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-builder
WORKDIR /build

# Copy solution and project files first (restore cache layer)
COPY VelocityDrone.slnx Directory.Build.props global.json ./
COPY Drone.Agent/Drone.Agent.csproj Drone.Agent/
COPY Drone.Core/Drone.Core.csproj Drone.Core/
COPY Drone.MCP/Drone.MCP.csproj Drone.MCP/
COPY Drone.Native/Drone.Native.csproj Drone.Native/
COPY Drone.Services/Drone.Services.csproj Drone.Services/
COPY Drone.System/Drone.System.csproj Drone.System/
COPY Drone.Autonomy/Drone.Autonomy.csproj Drone.Autonomy/
COPY Drone.Custody/Drone.Custody.csproj Drone.Custody/
COPY tests/Drone.Tests/Drone.Tests.csproj tests/Drone.Tests/
COPY tests/Drone.E2E/Drone.E2E.csproj tests/Drone.E2E/

# Restore packages (EnableWindowsTargeting allows cross-compile from Linux)
RUN dotnet restore VelocityDrone.slnx /p:EnableWindowsTargeting=true

# Copy all source code
COPY . .

# Build and publish (skip Rust build — using pre-built native lib from stage 1)
RUN dotnet publish Drone.Agent/Drone.Agent.csproj -c Release -o /app/publish \
    /p:SkipRust=true /p:PublishSingleFile=false /p:EnableWindowsTargeting=true

# --- Stage 3: Runtime ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Create non-root user for security
RUN groupadd -r drone && useradd -r -g drone -m drone

# Copy published app
COPY --from=dotnet-builder /app/publish .

# Copy native library from Rust builder
COPY --from=rust-builder /build/libdrone_native.so ./libdrone_native.so

# Set ownership
RUN chown -R drone:drone /app

# Expose MCP WebSocket port
EXPOSE 9100

# Health check
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget -q --spider http://localhost:9100/health/live || exit 1

# Run as non-root
USER drone

# Default to headless mode in Docker
ENV DRONE_MODE=headless
ENV DRONE_MCP_URL=http://0.0.0.0:9100
ENV DRONE_ALLOW_INSECURE_HTTP=1

ENTRYPOINT ["dotnet", "velocity-drone.dll"]
