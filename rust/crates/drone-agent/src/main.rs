//! Velocity Drone — lightweight LLM agent runtime.
//!
//! A single binary that ships to any system and gives the server-side
//! eyes and hands. Becomes an extension of the LLM to new systems.

use clap::Parser;
use drone_core::config::DroneConfig;
use drone_mcp::McpServer;
use drone_autonomy::{AutonomyEngine, EventBus};
use std::path::PathBuf;

#[derive(Parser)]
#[command(name = "velocity-drone", version = "1.0.0", about = "Velocity Drone Agent")]
struct Cli {
    /// Path to configuration file
    #[arg(short, long, default_value = "drone.toml")]
    config: PathBuf,

    /// Run in headless mode (no screen/input)
    #[arg(long)]
    headless: bool,

    /// Override drone ID
    #[arg(long, env = "DRONE_ID")]
    drone_id: Option<String>,

    /// Override WebSocket uplink URL
    #[arg(long, env = "DRONE_WS_URL")]
    ws_url: Option<String>,

    /// Override MCP WebSocket listen URL
    #[arg(long, default_value = "0.0.0.0:9100")]
    mcp_url: String,
}

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    // Initialize tracing
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::from_default_env()
                .add_directive("velocity_drone=info".parse()?)
        )
        .init();

    let cli = Cli::parse();

    tracing::info!("=== Velocity Drone Agent v1.0.0 (Rust) ===");
    tracing::info!("Platform: {} {}", std::env::consts::OS, std::env::consts::ARCH);

    // Load config
    let mut config = DroneConfig::load(&cli.config).unwrap_or_default();
    config.apply_env_overrides();

    if cli.headless {
        config.mode = drone_core::config::DroneMode::Headless;
    }
    if let Some(id) = cli.drone_id {
        config.drone_id = id;
    }
    if let Some(url) = cli.ws_url {
        config.uplink.websocket_url = Some(url);
    }

    config.validate().map_err(|e| anyhow::anyhow!("{}", e))?;

    tracing::info!("Drone ID: {}", config.drone_id);
    tracing::info!("Mode: {:?}", config.mode);

    // Initialize custody chain
    let _custody_chain = drone_core::CustodyChain::new(&config.drone_id);
    tracing::info!("Custody trail initialized");

    // Create MCP server
    let mcp_server = McpServer::new();

    // Register built-in tools
    register_builtin_tools(&mcp_server).await;

    // Create event bus and autonomy engine
    let event_bus = EventBus::new();
    let autonomy = AutonomyEngine::new(config.autonomy.clone());
    autonomy.start(&event_bus).await;

    // Log startup
    let tools = mcp_server.get_tool_names().await;
    tracing::info!("Registered {} MCP tools. Drone ready.", tools.len());

    // Create cancellation channel
    let (cancel_tx, cancel_rx) = tokio::sync::watch::channel(false);

    // Start MCP WebSocket server
    let mcp_handle = tokio::spawn({
        let mcp_url = cli.mcp_url.clone();
        async move {
            if let Err(e) = mcp_server.run_websocket(&mcp_url, cancel_rx).await {
                tracing::error!("MCP WebSocket server error: {}", e);
            }
        }
    });

    tracing::info!("MCP WebSocket at {}", cli.mcp_url);

    // Wait for Ctrl+C
    tokio::signal::ctrl_c().await?;
    tracing::info!("Shutting down...");

    let _ = cancel_tx.send(true);
    let _ = mcp_handle.await;

    Ok(())
}

async fn register_builtin_tools(server: &McpServer) {
    use serde_json::Value as JsonValue;
    use std::sync::Arc;

    // run_command
    server.register_tool("run_command", Arc::new(|args: JsonValue| {
        Box::pin(async move {
            let command = args.get("command").and_then(|c| c.as_str()).unwrap_or("");
            let cmd_args = args.get("args").and_then(|a| a.as_str()).unwrap_or("");

            // Security: block dangerous commands
            let blocked = ["format", "del /s", "rm -rf /", "mkfs", "dd if=/dev/zero"];
            if blocked.iter().any(|b| command.to_lowercase().contains(b)) {
                return serde_json::json!({"error": "Command blocked by security policy"});
            }

            let full_cmd = if cmd_args.is_empty() {
                command.to_string()
            } else {
                format!("{} {}", command, cmd_args)
            };

            let output = if cfg!(target_os = "windows") {
                tokio::process::Command::new("cmd")
                    .args(["/C", &full_cmd])
                    .output().await
            } else {
                tokio::process::Command::new("sh")
                    .args(["-c", &full_cmd])
                    .output().await
            };

            match output {
                Ok(out) => {
                    let stdout = String::from_utf8_lossy(&out.stdout).to_string();
                    let stderr = String::from_utf8_lossy(&out.stderr).to_string();
                    let stdout_trunc = if stdout.len() > 100_000 {
                        format!("{}...", &stdout[..100_000])
                    } else {
                        stdout
                    };
                    serde_json::json!({
                        "exitCode": out.status.code().unwrap_or(-1),
                        "stdout": stdout_trunc,
                        "stderr": stderr
                    })
                }
                Err(e) => serde_json::json!({"error": format!("Failed to run command: {}", e)}),
            }
        })
    })).await;

    // read_file
    server.register_tool("read_file", Arc::new(|args: JsonValue| {
        Box::pin(async move {
            let path = args.get("path").and_then(|p| p.as_str()).unwrap_or("");
            match tokio::fs::read_to_string(path).await {
                Ok(content) => serde_json::json!({"content": content, "path": path}),
                Err(e) => serde_json::json!({"error": format!("File not found: {}", e)}),
            }
        })
    })).await;

    // write_file
    server.register_tool("write_file", Arc::new(|args: JsonValue| {
        Box::pin(async move {
            let path = args.get("path").and_then(|p| p.as_str()).unwrap_or("");
            let content = args.get("content").and_then(|c| c.as_str()).unwrap_or("");
            if let Some(dir) = std::path::Path::new(path).parent() {
                let _ = tokio::fs::create_dir_all(dir).await;
            }
            match tokio::fs::write(path, content).await {
                Ok(_) => serde_json::json!({"success": true, "path": path}),
                Err(e) => serde_json::json!({"error": format!("Write failed: {}", e)}),
            }
        })
    })).await;

    // list_dir
    server.register_tool("list_dir", Arc::new(|args: JsonValue| {
        Box::pin(async move {
            let path = args.get("path").and_then(|p| p.as_str()).unwrap_or(".");
            match tokio::fs::read_dir(path).await {
                Ok(mut entries) => {
                    let mut items = Vec::new();
                    while let Ok(Some(entry)) = entries.next_entry().await {
                        let name = entry.file_name().to_string_lossy().to_string();
                        let is_dir = entry.file_type().await.map(|t| t.is_dir()).unwrap_or(false);
                        items.push(serde_json::json!({"name": name, "isDir": is_dir}));
                    }
                    items.sort_by(|a, b| {
                        a.get("name").and_then(|n| n.as_str()).unwrap_or("").cmp(
                            b.get("name").and_then(|n| n.as_str()).unwrap_or("")
                        )
                    });
                    serde_json::json!({"path": path, "count": items.len(), "entries": items})
                }
                Err(e) => serde_json::json!({"error": format!("Directory not found: {}", e)}),
            }
        })
    })).await;

    // get_system_info
    server.register_tool("get_system_info", Arc::new(|_args: JsonValue| {
        Box::pin(async move {
            serde_json::json!({
                "os": std::env::consts::OS,
                "arch": std::env::consts::ARCH,
                "hostname": hostname::get().ok().and_then(|h| h.into_string().ok()).unwrap_or_default()
            })
        })
    })).await;

    // get_drone_status
    server.register_tool("get_drone_status", Arc::new(|_args: JsonValue| {
        Box::pin(async move {
            serde_json::json!({
                "agent": "velocity-drone",
                "version": "1.0.0",
                "platform": format!("{} {}", std::env::consts::OS, std::env::consts::ARCH),
                "capabilities": {
                    "processManagement": true,
                    "fileOperations": true,
                    "commandExecution": true
                }
            })
        })
    })).await;

    tracing::info!("Built-in tools registered");
}
