# Velocity Drone â€” Multi-stage build
# Supports both full mode (with screen/input) and headless mode (cloud VMs)

FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

# Copy solution and project files
COPY VelocityDrone.slnx .
COPY Drone.Core/*.csproj Drone.Core/
COPY Drone.System/*.csproj Drone.System/
COPY Drone.Services/*.csproj Drone.Services/
COPY Drone.MCP/*.csproj Drone.MCP/
COPY Drone.Autonomy/*.csproj Drone.Autonomy/
COPY Drone.Agent/*.csproj Drone.Agent/
COPY Drone.Native/*.csproj Drone.Native/
COPY tests/Drone.Tests/*.csproj tests/Drone.Tests/
COPY tests/Drone.E2E/*.csproj tests/Drone.E2E/

# Restore
RUN dotnet restore VelocityDrone.slnx

# Copy source
COPY . .

# Build and publish
RUN dotnet publish Drone.Agent/Drone.Agent.csproj -c Release -o /app/publish --no-restore /p:SkipRust=true

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS runtime
WORKDIR /app

# Install system dependencies for headless Linux
RUN apt-get update && apt-get install -y --no-install-recommends \
    xdotool xclip wmctrl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Default to headless mode (override with DRONE_MODE=full for desktop)
ENV DRONE_MODE=headless
ENV DRONE_Messenger__ServerUrl=""
ENV DRONE_Share__ServerUrl=""
ENV DRONE_Remote__ServerUrl=""

ENTRYPOINT ["dotnet", "velocity-drone.dll"]
