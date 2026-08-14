//! Velocity Drone — lightweight LLM agent runtime.
//!
//! A single binary that ships to any system and gives the server-side
//! eyes and hands. Becomes an extension of the LLM to new systems.

use clap::Parser;
use drone_core::config::DroneConfig;
use drone_mcp::McpServer;
use drone_autonomy::{AutonomyEngine, EventBus};
use drone_autonomy::event_bus::DroneEventTypes;
use drone_services::messenger::MessengerConnector;
use drone_services::share::ShareConnector;
use drone_services::remote::RemoteConnector;
use drone_services::file_server::FileServer;
use drone_services::custody_reporter::CustodyReporter;
use drone_services::uplink::VelocityConnection;
use std::path::PathBuf;
use std::sync::Arc;

#[cfg(windows)]
use drone_system::windows::{
    Win32ScreenCapture, Win32InputSimulator, Win32ProcessManager,
    Win32ClipboardManager, Win32WindowManager,
};

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
    if cli.headless { config.mode = drone_core::config::DroneMode::Headless; }
    if let Some(id) = cli.drone_id { config.drone_id = id; }
    if let Some(url) = cli.ws_url { config.uplink.websocket_url = Some(url); }
    config.validate().map_err(|e| anyhow::anyhow!("{}", e))?;

    tracing::info!("Drone ID: {}", config.drone_id);
    tracing::info!("Mode: {:?}", config.mode);

    // Initialize custody chain
    let _custody_chain = drone_core::CustodyChain::new(&config.drone_id);
    tracing::info!("Custody trail initialized");

    // ── Platform implementations ──────────────────────────────────────────
    #[cfg(windows)]
    let (screen, input, windows, process, clipboard) = {
        let is_headless = config.mode == drone_core::config::DroneMode::Headless;
        let screen: Option<Arc<dyn drone_system::ScreenCapture>> = if !is_headless {
            match std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| Win32ScreenCapture::new())) {
                Ok(s) => Some(Arc::new(s)),
                Err(_) => { tracing::warn!("Screen capture unavailable"); None }
            }
        } else { None };
        let input: Option<Arc<dyn drone_system::InputSimulator>> = if !is_headless {
            Some(Arc::new(Win32InputSimulator::new()))
        } else { None };
        let windows: Option<Arc<dyn drone_system::WindowManager>> = if !is_headless {
            Some(Arc::new(Win32WindowManager::new()))
        } else { None };
        let process: Arc<dyn drone_system::ProcessManager> = Arc::new(Win32ProcessManager::new());
        let clipboard: Arc<dyn drone_system::ClipboardManager> = Arc::new(Win32ClipboardManager::new());
        (screen, input, windows, process, clipboard)
    };
    #[cfg(not(windows))]
    let (screen, input, windows, process, clipboard) = {
        let screen: Option<Arc<dyn drone_system::ScreenCapture>> = None;
        let input: Option<Arc<dyn drone_system::InputSimulator>> = None;
        let windows: Option<Arc<dyn drone_system::WindowManager>> = None;
        let process: Option<Arc<dyn drone_system::ProcessManager>> = None;
        let clipboard: Option<Arc<dyn drone_system::ClipboardManager>> = None;
        (screen, input, windows, process, clipboard)
    };

    // ── Service connectors ────────────────────────────────────────────────
    let messenger = if config.messenger.server_url.is_some() {
        Some(Arc::new(MessengerConnector::new(config.messenger.clone(), config.drone_id.clone())))
    } else { None };
    let share = if config.share.server_url.is_some() {
        Some(Arc::new(ShareConnector::new(config.share.clone())))
    } else { None };
    let remote = if config.remote.server_url.is_some() {
        Some(Arc::new(RemoteConnector::new(config.remote.clone())))
    } else { None };

    // ── MCP server ────────────────────────────────────────────────────────
    let mcp_server = McpServer::new();

    // Register all system tools (matches C# SystemToolRegistrar.RegisterAll)
    register_system_tools(
        &mcp_server, &screen, &input, &windows, &Some(process), &Some(clipboard),
        &messenger, &share, &remote,
    ).await;

    // ── Uplink (VelocityConnection) ──────────────────────────────────────
    let uplink = if config.uplink.websocket_url.is_some() {
        Some(Arc::new(VelocityConnection::new(config.uplink.clone())))
    } else { None };

    // ── Event bus and autonomy engine ─────────────────────────────────────
    let event_bus = Arc::new(EventBus::new());
    let autonomy = AutonomyEngine::new(config.autonomy.clone());

    // Wire autonomy action callback: notify messenger + forward events via uplink
    {
        let msg = messenger.clone();
        let up = uplink.clone();
        autonomy.set_on_action_executed(move |rule_name, event_type, data| {
            let msg = msg.clone();
            let up = up.clone();
            let rule_name = rule_name.clone();
            let event_type = event_type.clone();
            Box::pin(async move {
                // Auto-reply via messenger on message events
                if event_type == DroneEventTypes::MESSAGE_RECEIVED {
                    if let Some(ref msg) = msg {
                        if msg.is_connected() {
                            if let Some(from) = data.get("from").and_then(|f| f.as_str()) {
                                if !from.is_empty() {
                                    let _ = msg.send_message(from, "[Auto-reply] Acknowledged by Velocity Drone").await;
                                }
                            }
                        }
                    }
                }
                tracing::info!("[Autonomy] Action executed: rule={}, event={}", rule_name, event_type);

                // Forward autonomy events to uplink as notifications
                if let Some(ref up) = up {
                    if up.is_connected() {
                        let notification = serde_json::json!({
                            "jsonrpc": "2.0",
                            "method": "notifications/droneEvent",
                            "params": {
                                "eventType": event_type,
                                "data": data,
                                "ruleName": rule_name,
                            }
                        });
                        let json_str = notification.to_string();
                        let _ = up.send_notification(&json_str).await;
                    }
                }
            })
        }).await;
    }
    autonomy.start(&event_bus).await;

    // ── Auth token ────────────────────────────────────────────────────────
    if let Ok(token) = std::env::var("DRONE_MCP_TOKEN") {
        if !token.is_empty() {
            mcp_server.set_auth_token(Some(token)).await;
            tracing::info!("MCP auth enabled");
        }
    } else {
        tracing::warn!("MCP WebSocket has NO authentication");
    }

    // ── Cancel channel ────────────────────────────────────────────────────
    let (cancel_tx, cancel_rx) = tokio::sync::watch::channel(false);

    // ── MCP transports (shmem + WebSocket) ────────────────────────────────
    #[cfg(windows)]
    let shmem_handle = tokio::spawn({
        let path = config.mcp.buffer_path.clone();
        let size = config.mcp.buffer_size;
        let rx = cancel_rx.clone();
        let server = mcp_server.clone();
        async move {
            if let Err(e) = server.run_shmem(&path, size, rx).await {
                tracing::error!("MCP shmem error: {}", e);
            }
        }
    });
    #[cfg(windows)]
    tracing::info!("MCP NMCP at {}", config.mcp.buffer_path);

    let ws_handle = tokio::spawn({
        let url = cli.mcp_url.clone();
        let rx = cancel_rx.clone();
        let server = mcp_server.clone();
        async move {
            if let Err(e) = server.run_websocket(&url, rx).await {
                tracing::error!("MCP WebSocket error: {}", e);
            }
        }
    });
    tracing::info!("MCP WebSocket at {}", cli.mcp_url);

    // ── File server ───────────────────────────────────────────────────────
    if config.share.enabled {
        let storage_path = std::env::var("DRONE_SHARE_PATH")
            .unwrap_or_else(|_| "C:\\Drone\\share".to_string());
        let listen = "0.0.0.0:5003";
        let api_key = config.share.admin_api_key.clone();
        let fs_cancel = cancel_rx.clone();
        tokio::spawn(async move {
            let server = FileServer::from_config(PathBuf::from(storage_path), listen, api_key);
            if let Err(e) = server.start(fs_cancel).await {
                tracing::error!("File server error: {}", e);
            }
        });
        tracing::info!("File server at {}", listen);
    }

    // ── Messenger connector ───────────────────────────────────────────────
    if let Some(ref msg) = messenger {
        // Set message handler (command processor)
        let msg_clone = msg.clone();
        let mcp = mcp_server.clone_ref_public();
        msg.set_on_message(move |from, content, _msg_id| {
            let msg = msg_clone.clone();
            let mcp = mcp.clone();
            Box::pin(async move {
                if from.is_empty() || !msg.is_connected() { return; }
                let cmd = content.trim().to_string();
                let response = handle_messenger_command(&cmd, &from, &mcp, &msg).await;
                let _ = msg.send_message(&from, &response).await;
            })
        }).await;

        // Set connection handler
        msg.set_on_connection(|connected| {
            Box::pin(async move {
                if connected {
                    tracing::info!("Connected to Messenger");
                } else {
                    tracing::warn!("Disconnected from Messenger");
                }
            })
        }).await;

        // Start messenger
        let msg_connect = msg.clone();
        let msg_cancel = cancel_rx.clone();
        tokio::spawn(async move {
            if let Err(e) = msg_connect.connect(msg_cancel).await {
                tracing::error!("Messenger error: {}", e);
            }
        });
    }

    // ── Remote connector ──────────────────────────────────────────────────
    if let Some(ref rem) = remote {
        let mcp_tool = mcp_server.clone_ref_public();
        rem.set_on_tool_call(move |tool_name, args_data, seq_id| {
            let mcp = mcp_tool.clone();
            Box::pin(async move {
                tracing::info!("[Remote] Tool call: {} seq={}", tool_name, seq_id);
                let args_json = if args_data.is_empty() {
                    serde_json::Value::Object(serde_json::Map::new())
                } else {
                    serde_json::from_slice(&args_data).unwrap_or(serde_json::Value::Object(serde_json::Map::new()))
                };
                match mcp.invoke_tool(&tool_name, args_json).await {
                    Some(result) => serde_json::to_vec(&result).unwrap_or_default(),
                    None => format!(r#"{{"error":"Unknown tool: {}"}}"#, tool_name).into_bytes(),
                }
            })
        }).await;

        let rem_connect = rem.clone();
        let rem_cancel = cancel_rx.clone();
        tokio::spawn(async move {
            if let Err(e) = rem_connect.connect(rem_cancel).await {
                tracing::error!("Remote error: {}", e);
            }
        });
    }

    // ── Custody reporter ──────────────────────────────────────────────────
    let (custody_reporter, mut custody_rx) = CustodyReporter::new();
    tokio::spawn(async move {
        while let Some(record) = custody_rx.recv().await {
            tracing::debug!("[Custody] seq={} action={}", record.sequence, record.action);
        }
    });
    let _ = custody_reporter; // used for future reporting

    // ── Uplink handlers + connection ──────────────────────────────────────
    if let Some(ref uplink) = uplink {
        // Forward incoming JSON-RPC requests to the MCP server
        let mcp_req = mcp_server.clone();
        let up_req = uplink.clone();
        uplink.set_on_request(move |request: serde_json::Value| {
            let mcp = mcp_req.clone();
            let up = up_req.clone();
            Box::pin(async move {
                let response = mcp.handle_request(&request, "uplink").await;
                let json_str = response.to_string();
                let _ = up.send_response(&json_str).await;
                json_str
            })
        }).await;

        // Log incoming notifications from the server
        let up_not = uplink.clone();
        uplink.set_on_notification(move |notification: serde_json::Value| {
            let _up = up_not.clone();
            Box::pin(async move {
                tracing::info!("[Uplink] Notification: {}", notification);
            })
        }).await;

        // Spawn the uplink connection task
        let uplink_connect = uplink.clone();
        let uplink_cancel = cancel_rx.clone();
        tokio::spawn(async move {
            if let Err(e) = uplink_connect.connect(uplink_cancel).await {
                tracing::error!("Uplink connection error: {}", e);
            }
        });
        tracing::info!("Uplink configured: {}", config.uplink.websocket_url.as_deref().unwrap_or(""));
    }

    // ── Ready ─────────────────────────────────────────────────────────────
    let tools = mcp_server.get_tool_names().await;
    tracing::info!("Registered {} MCP tools. Drone ready.", tools.len());

    // Wait for Ctrl+C
    tokio::signal::ctrl_c().await?;
    tracing::info!("Shutting down...");
    let _ = cancel_tx.send(true);
    let _ = ws_handle.await;
    #[cfg(windows)]
    let _ = shmem_handle.await;

    Ok(())
}

// ── Messenger command processor ───────────────────────────────────────────────

async fn handle_messenger_command(
    cmd: &str, from: &str,
    mcp: &drone_mcp::server::McpServerRef,
    messenger: &MessengerConnector,
) -> String {
    let cmd = cmd.trim();
    tracing::info!("[Messenger] Command from {}: {}", from, cmd.split(' ').next().unwrap_or(""));

    if cmd.eq_ignore_ascii_case("status") {
        let result = mcp.invoke_tool("get_drone_status", serde_json::json!({})).await;
        format!("Drone Status: {}", result.map(|r| r.to_string()).unwrap_or_else(|| "unknown".into()))
    } else if let Some(rest) = cmd.strip_prefix("upload ") {
        let parts: Vec<&str> = rest.splitn(2, ' ').collect();
        if parts.len() == 2 {
            let args = serde_json::json!({"localPath": parts[0], "remotePath": parts[1]});
            let result = mcp.invoke_tool("upload_file", args).await;
            format!("Upload result: {}", result.map(|r| r.to_string()).unwrap_or_default())
        } else { "Usage: upload <localPath> <remotePath>".into() }
    } else if let Some(rest) = cmd.strip_prefix("download ") {
        let parts: Vec<&str> = rest.splitn(2, ' ').collect();
        if parts.len() == 2 {
            let args = serde_json::json!({"remotePath": parts[0], "localPath": parts[1]});
            let result = mcp.invoke_tool("download_file", args).await;
            format!("Download result: {}", result.map(|r| r.to_string()).unwrap_or_default())
        } else { "Usage: download <remotePath> <localPath>".into() }
    } else if let Some(rest) = cmd.strip_prefix("list") {
        let path = if rest.len() > 1 { Some(rest[1..].trim()) } else { None };
        let args = match path {
            Some(p) if !p.is_empty() => serde_json::json!({"path": p}),
            _ => serde_json::json!({}),
        };
        let result = mcp.invoke_tool("list_files", args).await;
        format!("Files: {}", result.map(|r| r.to_string()).unwrap_or_default())
    } else if cmd.eq_ignore_ascii_case("update") {
        self_update(messenger, from).await;
        return "Starting self-update...".into();
    } else if let Some(rest) = cmd.strip_prefix("run ") {
        let args = serde_json::json!({"command": rest});
        let result = mcp.invoke_tool("run_command", args).await;
        format!("Command result: {}", result.map(|r| r.to_string()).unwrap_or_default())
    } else if let Some(rest) = cmd.strip_prefix("type ") {
        let args = serde_json::json!({"text": rest});
        let result = mcp.invoke_tool("type_text", args).await;
        format!("Type result: {}", result.map(|r| r.to_string()).unwrap_or_default())
    } else if let Some(rest) = cmd.strip_prefix("key ") {
        let args = serde_json::json!({"key": rest});
        let result = mcp.invoke_tool("press_key", args).await;
        format!("Key result: {}", result.map(|r| r.to_string()).unwrap_or_default())
    } else if let Some(rest) = cmd.strip_prefix("click ") {
        let parts: Vec<&str> = rest.split_whitespace().collect();
        if parts.len() >= 2 {
            if let (Ok(x), Ok(y)) = (parts[0].parse::<i32>(), parts[1].parse::<i32>()) {
                let button = if parts.len() >= 3 { parts[2] } else { "left" };
                let args = serde_json::json!({"x": x, "y": y, "button": button});
                let result = mcp.invoke_tool("click", args).await;
                format!("Click result: {}", result.map(|r| r.to_string()).unwrap_or_default())
            } else { "Usage: click <x> <y> [left|right]".into() }
        } else { "Usage: click <x> <y> [left|right]".into() }
    } else if cmd.eq_ignore_ascii_case("screenshot") {
        let result = mcp.invoke_tool("capture_screen", serde_json::json!({})).await;
        format!("Screenshot: {}", result.map(|r| r.to_string()).unwrap_or_default())
    } else {
        "[Drone-v2-MARKER] Commands: status, upload, download, list, run, type, key, click, screenshot, update".into()
    }
}

async fn self_update(messenger: &MessengerConnector, from: &str) {
    let script = "@echo off\r\necho Waiting for drone to stop...\r\n:wait\r\ntasklist /fi \"imagename eq velocity-drone.exe\" 2>NUL | find /I /N \"velocity-drone.exe\" >NUL\r\nif %errorlevel%==0 (timeout /t 1 /nobreak >NUL & goto wait)\r\ntimeout /t 2 /nobreak >NUL\r\ncopy /Y \"C:\\Drone\\share\\velocity-drone-new.exe\" \"C:\\Drone\\velocity-drone.exe\" >NUL\r\ndel \"C:\\Drone\\share\\velocity-drone-new.exe\" >NUL\r\ncd /d C:\\Drone\r\nstart \"\" run-drone.bat\r\necho Update complete!\r\ntimeout /t 3 /nobreak >NUL";
    let script_path = "C:\\Drone\\update-drone.bat";
    let _ = tokio::fs::write(script_path, script).await;
    let _ = messenger.send_message(from, "Starting self-update...").await;
    tokio::spawn(async {
        tokio::time::sleep(std::time::Duration::from_secs(1)).await;
        std::process::exit(0);
    });
}

// ── System tool registration ──────────────────────────────────────────────────

#[allow(clippy::too_many_arguments)]
async fn register_system_tools(
    server: &McpServer,
    screen: &Option<Arc<dyn drone_system::ScreenCapture>>,
    input: &Option<Arc<dyn drone_system::InputSimulator>>,
    windows: &Option<Arc<dyn drone_system::WindowManager>>,
    process: &Option<Arc<dyn drone_system::ProcessManager>>,
    clipboard: &Option<Arc<dyn drone_system::ClipboardManager>>,
    messenger: &Option<Arc<MessengerConnector>>,
    share: &Option<Arc<ShareConnector>>,
    remote: &Option<Arc<RemoteConnector>>,
) {
    use serde_json::Value as JsonValue;

    // ── Screen tools ──────────────────────────────────────────────────────
    if let Some(sc) = screen {
        let sc_capture = sc.clone();
        server.register_tool("capture_screen", Arc::new(move |_args: JsonValue| {
            let sc = sc_capture.clone();
            Box::pin(async move {
                match sc.capture_screen().await {
                    Ok(data) => {
                        let b64 = base64::Engine::encode(&base64::engine::general_purpose::STANDARD, &data);
                        serde_json::json!({"image": b64, "format": "png"})
                    }
                    Err(e) => serde_json::json!({"error": format!("Capture failed: {}", e)}),
                }
            })
        })).await;

        let sc_win = sc.clone();
        let win_ref = windows.clone();
        server.register_tool("capture_window", Arc::new(move |args: JsonValue| {
            let sc = sc_win.clone();
            let win_ref = win_ref.clone();
            Box::pin(async move {
                let title = args.get("title").and_then(|t| t.as_str()).unwrap_or("");
                if let Some(ref wm) = win_ref {
                    if !title.is_empty() {
                        if let Ok(wins) = wm.list_windows().await {
                            if let Some(w) = wins.iter().find(|w| w.title.to_lowercase().contains(&title.to_lowercase())) {
                                if let Ok(data) = sc.capture_window(w.handle).await {
                                    let b64 = base64::Engine::encode(&base64::engine::general_purpose::STANDARD, &data);
                                    return serde_json::json!({"image": b64, "format": "png", "title": w.title});
                                }
                            }
                        }
                    }
                }
                match sc.capture_screen().await {
                    Ok(data) => {
                        let b64 = base64::Engine::encode(&base64::engine::general_purpose::STANDARD, &data);
                        serde_json::json!({"image": b64, "format": "png"})
                    }
                    Err(e) => serde_json::json!({"error": format!("Capture failed: {}", e)}),
                }
            })
        })).await;

        let sc_pixel = sc.clone();
        server.register_tool("get_pixel_color", Arc::new(move |args: JsonValue| {
            let sc = sc_pixel.clone();
            Box::pin(async move {
                let x = args.get("x").and_then(|v| v.as_i64()).unwrap_or(0) as i32;
                let y = args.get("y").and_then(|v| v.as_i64()).unwrap_or(0) as i32;
                match sc.pixel_color(x, y).await {
                    Ok((r, g, b)) => serde_json::json!({"r": r, "g": g, "b": b}),
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;

        server.register_tool("find_image_on_screen", Arc::new(|_args: JsonValue| {
            Box::pin(async move {
                serde_json::json!({
                    "found": false, "x": 0, "y": 0, "confidence": 0.0,
                    "message": "Template matching requires native backend. Use capture_screen + AI vision instead."
                })
            })
        })).await;

        let sc_size = sc.clone();
        server.register_tool("get_screen_size", Arc::new(move |_args: JsonValue| {
            let sc = sc_size.clone();
            Box::pin(async move {
                match sc.screen_size().await {
                    Ok((w, h)) => serde_json::json!({"width": w, "height": h}),
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;
    }

    // ── Input tools ───────────────────────────────────────────────────────
    if let Some(inp) = input {
        let inp_type = inp.clone();
        server.register_tool("type_text", Arc::new(move |args: JsonValue| {
            let inp = inp_type.clone();
            Box::pin(async move {
                let text = args.get("text").and_then(|t| t.as_str()).unwrap_or("");
                match inp.type_text(text).await {
                    Ok(()) => serde_json::json!({"success": true}),
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;

        let inp_key = inp.clone();
        server.register_tool("press_key", Arc::new(move |args: JsonValue| {
            let inp = inp_key.clone();
            Box::pin(async move {
                let key = args.get("key").and_then(|k| k.as_str()).unwrap_or("");
                match inp.press_key(key).await {
                    Ok(()) => serde_json::json!({"success": true}),
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;

        let inp_move = inp.clone();
        server.register_tool("move_mouse", Arc::new(move |args: JsonValue| {
            let inp = inp_move.clone();
            Box::pin(async move {
                let x = args.get("x").and_then(|v| v.as_i64()).unwrap_or(0) as i32;
                let y = args.get("y").and_then(|v| v.as_i64()).unwrap_or(0) as i32;
                match inp.move_mouse(x, y).await {
                    Ok(()) => serde_json::json!({"success": true}),
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;

        let inp_click = inp.clone();
        server.register_tool("click", Arc::new(move |args: JsonValue| {
            let inp = inp_click.clone();
            Box::pin(async move {
                let x = args.get("x").and_then(|v| v.as_i64()).unwrap_or(0) as i32;
                let y = args.get("y").and_then(|v| v.as_i64()).unwrap_or(0) as i32;
                let button = match args.get("button").and_then(|b| b.as_str()) {
                    Some("right") => drone_system::MouseButton::Right,
                    Some("middle") => drone_system::MouseButton::Middle,
                    _ => drone_system::MouseButton::Left,
                };
                match inp.click(x, y, button).await {
                    Ok(()) => serde_json::json!({"success": true}),
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;

        let inp_drag = inp.clone();
        server.register_tool("drag", Arc::new(move |args: JsonValue| {
            let inp = inp_drag.clone();
            Box::pin(async move {
                let fx = args.get("fromX").and_then(|v| v.as_i64()).unwrap_or(0) as i32;
                let fy = args.get("fromY").and_then(|v| v.as_i64()).unwrap_or(0) as i32;
                let tx = args.get("toX").and_then(|v| v.as_i64()).unwrap_or(0) as i32;
                let ty = args.get("toY").and_then(|v| v.as_i64()).unwrap_or(0) as i32;
                match inp.drag(fx, fy, tx, ty).await {
                    Ok(()) => serde_json::json!({"success": true}),
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;

        let inp_scroll = inp.clone();
        server.register_tool("scroll", Arc::new(move |args: JsonValue| {
            let inp = inp_scroll.clone();
            Box::pin(async move {
                let dx = args.get("deltaX").and_then(|v| v.as_i64()).unwrap_or(0) as i32;
                let dy = args.get("deltaY").and_then(|v| v.as_i64()).unwrap_or(0) as i32;
                match inp.scroll(dx, dy).await {
                    Ok(()) => serde_json::json!({"success": true}),
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;
    }

    // ── System tools (always available) ───────────────────────────────────
    if let Some(proc_mgr) = process {
        let pm = proc_mgr.clone();
        server.register_tool("run_command", Arc::new(move |args: JsonValue| {
            let pm = pm.clone();
            Box::pin(async move {
                let command = args.get("command").and_then(|c| c.as_str()).unwrap_or("");
                let blocked = ["format", "del /s", "rm -rf /", "mkfs", "dd if=/dev/zero"];
                if blocked.iter().any(|b| command.to_lowercase().contains(b)) {
                    return serde_json::json!({"error": "Command blocked by security policy"});
                }
                let cmd_args = args.get("args").and_then(|a| a.as_str()).unwrap_or("");
                let working_dir = args.get("workingDir").and_then(|w| w.as_str());
                match tokio::time::timeout(
                    std::time::Duration::from_secs(60),
                    pm.run_command(command, cmd_args, working_dir),
                ).await {
                    Ok(Ok(r)) => {
                        let stdout = if r.stdout.len() > 100_000 {
                            format!("{}...", &r.stdout[..100_000])
                        } else { r.stdout };
                        serde_json::json!({
                            "exitCode": r.exit_code, "stdout": stdout,
                            "stderr": r.stderr, "durationMs": r.duration.as_millis(),
                        })
                    }
                    Ok(Err(e)) => serde_json::json!({"error": format!("Command failed: {}", e)}),
                    Err(_) => serde_json::json!({"error": "Command timed out after 60s", "command": command, "exitCode": -1}),
                }
            })
        })).await;

        let pm2 = proc_mgr.clone();
        server.register_tool("list_processes", Arc::new(move |_args: JsonValue| {
            let pm = pm2.clone();
            Box::pin(async move {
                match pm.list_processes().await {
                    Ok(procs) => {
                        let count = procs.len();
                        let mut sorted = procs;
                        sorted.sort_by(|a, b| b.memory.cmp(&a.memory));
                        let top50: Vec<JsonValue> = sorted.iter().take(50).map(|p| {
                            serde_json::json!({
                                "pid": p.pid, "name": p.name,
                                "memoryMB": p.memory / 1024 / 1024,
                                "cpuUsage": p.cpu_usage, "status": p.status,
                            })
                        }).collect();
                        serde_json::json!({"count": count, "top50": top50})
                    }
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;

        let pm3 = proc_mgr.clone();
        server.register_tool("kill_process", Arc::new(move |args: JsonValue| {
            let pm = pm3.clone();
            Box::pin(async move {
                let pid = args.get("processId").and_then(|p| p.as_u64()).unwrap_or(0) as u32;
                match pm.kill_process(pid).await {
                    Ok(ok) => serde_json::json!({"success": ok, "processId": pid}),
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;

        let pm4 = proc_mgr.clone();
        server.register_tool("get_system_info", Arc::new(move |_args: JsonValue| {
            let pm = pm4.clone();
            Box::pin(async move {
                match pm.system_info().await {
                    Ok(info) => serde_json::json!({
                        "hostname": info.hostname, "os": info.os,
                        "osVersion": info.os_version, "arch": info.arch,
                        "cpuCount": info.cpu_count,
                        "totalMemory": info.total_memory, "usedMemory": info.used_memory,
                    }),
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;
    }

    // File tools
    server.register_tool("read_file", Arc::new(|args: JsonValue| {
        Box::pin(async move {
            let path = args.get("path").and_then(|p| p.as_str()).unwrap_or("");
            match tokio::fs::read_to_string(path).await {
                Ok(content) => serde_json::json!({"content": content, "path": path}),
                Err(e) => serde_json::json!({"error": format!("File not found: {}", e)}),
            }
        })
    })).await;

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

    server.register_tool("find_file", Arc::new(|args: JsonValue| {
        Box::pin(async move {
            let path = args.get("path").and_then(|p| p.as_str()).unwrap_or(".");
            let pattern = args.get("pattern").and_then(|p| p.as_str()).unwrap_or("*");
            match find_files_recursive(path, pattern, 0, 5).await {
                Ok(files) => {
                    let truncated: Vec<JsonValue> = files.into_iter().take(100).collect();
                    serde_json::json!({"count": truncated.len(), "files": truncated})
                }
                Err(e) => serde_json::json!({"error": format!("{}", e)}),
            }
        })
    })).await;

    // Clipboard tools
    if let Some(clip) = clipboard {
        let cl = clip.clone();
        server.register_tool("clipboard_get", Arc::new(move |_args: JsonValue| {
            let cl = cl.clone();
            Box::pin(async move {
                match cl.get_text().await {
                    Ok(Some(text)) => serde_json::json!({"text": text, "length": text.len()}),
                    Ok(None) => serde_json::json!({"text": "", "length": 0}),
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;

        let cl2 = clip.clone();
        server.register_tool("clipboard_set", Arc::new(move |args: JsonValue| {
            let cl = cl2.clone();
            Box::pin(async move {
                let text = args.get("text").and_then(|t| t.as_str()).unwrap_or("");
                match cl.set_text(text).await {
                    Ok(()) => serde_json::json!({"success": true}),
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;
    }

    // ── Drone status (always registered) ──────────────────────────────────
    let has_screen = screen.is_some();
    let has_input = input.is_some();
    let has_windows = windows.is_some();
    let has_clipboard = clipboard.is_some();
    let has_process = process.is_some();
    let msg_status = messenger.as_ref().map(|m| m.connected_watcher());
    let share_connected = share.is_some();
    let remote_connected = remote.is_some();
    let mcp_ref = server.clone_ref_public();

    server.register_tool("get_drone_status", Arc::new(move |_args: JsonValue| {
        let msg_w = msg_status.clone();
        let mcp = mcp_ref.clone();
        Box::pin(async move {
            let uptime_sec = (chrono::Utc::now().timestamp_millis() - mcp.start_time_ms()) / 1000;
            let messenger_connected = msg_w.as_ref().map(|w| *w.borrow()).unwrap_or(false);
            serde_json::json!({
                "agent": "velocity-drone",
                "version": "1.0.0",
                "uptimeSec": uptime_sec,
                "platform": format!("{} {}", std::env::consts::OS, std::env::consts::ARCH),
                "mode": if has_screen { "full" } else { "headless" },
                "connections": {
                    "messenger": messenger_connected,
                    "share": share_connected,
                    "remote": remote_connected,
                    "mcpWebSocketClients": mcp.connected_client_count(),
                },
                "capabilities": {
                    "screenCapture": has_screen,
                    "inputSimulation": has_input,
                    "windowManagement": has_windows,
                    "processManagement": has_process,
                    "clipboard": has_clipboard,
                    "fileOperations": true,
                    "commandExecution": true,
                },
                "metrics": {
                    "totalRequests": mcp.total_requests(),
                    "totalErrors": mcp.total_errors(),
                }
            })
        })
    })).await;

    // Legacy alias
    server.register_tool("get_status", Arc::new(|_args: JsonValue| {
        Box::pin(async move {
            serde_json::json!({"status": "ok", "version": "1.0.0"})
        })
    })).await;

    // ── Messenger tools ───────────────────────────────────────────────────
    if let Some(msg) = messenger {
        let m = msg.clone();
        server.register_tool("send_message", Arc::new(move |args: JsonValue| {
            let m = m.clone();
            Box::pin(async move {
                let to = args.get("to").and_then(|t| t.as_str()).unwrap_or("");
                let content = args.get("content").and_then(|c| c.as_str()).unwrap_or("");
                match m.send_message(to, content).await {
                    Ok(()) => serde_json::json!({"success": true}),
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;

        let m2 = msg.clone();
        server.register_tool("send_group_message", Arc::new(move |args: JsonValue| {
            let m = m2.clone();
            Box::pin(async move {
                let group = args.get("groupId").and_then(|g| g.as_str()).unwrap_or("");
                let content = args.get("content").and_then(|c| c.as_str()).unwrap_or("");
                match m.send_group_message(group, content).await {
                    Ok(()) => serde_json::json!({"success": true}),
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;

        let m3 = msg.clone();
        server.register_tool("upload_media", Arc::new(move |args: JsonValue| {
            let m = m3.clone();
            Box::pin(async move {
                let path = args.get("filePath").and_then(|p| p.as_str()).unwrap_or("");
                let media_type = args.get("mediaType").and_then(|t| t.as_str()).unwrap_or("file");
                match m.upload_media(path, media_type).await {
                    Ok(name) => serde_json::json!({"success": true, "fileName": name}),
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;

        let m4 = msg.clone();
        server.register_tool("download_media", Arc::new(move |args: JsonValue| {
            let m = m4.clone();
            Box::pin(async move {
                let url = args.get("url").and_then(|u| u.as_str()).unwrap_or("");
                let local = args.get("localPath").and_then(|p| p.as_str()).unwrap_or("");
                match m.download_media(url, local).await {
                    Ok(()) => serde_json::json!({"success": true, "localPath": local}),
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;
    }

    // ── Share tools ───────────────────────────────────────────────────────
    if let Some(sh) = share {
        let s = sh.clone();
        server.register_tool("upload_file", Arc::new(move |args: JsonValue| {
            let s = s.clone();
            Box::pin(async move {
                let local = args.get("localPath").and_then(|p| p.as_str()).unwrap_or("");
                let remote = args.get("remotePath").and_then(|p| p.as_str()).unwrap_or("");
                match s.upload_file(local, remote).await {
                    Ok(ok) => serde_json::json!({"success": ok}),
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;

        let s2 = sh.clone();
        server.register_tool("download_file", Arc::new(move |args: JsonValue| {
            let s = s2.clone();
            Box::pin(async move {
                let remote = args.get("remotePath").and_then(|p| p.as_str()).unwrap_or("");
                let local = args.get("localPath").and_then(|p| p.as_str()).unwrap_or("");
                match s.download_file(remote, local).await {
                    Ok(ok) => serde_json::json!({"success": ok}),
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;

        let s3 = sh.clone();
        server.register_tool("list_files", Arc::new(move |args: JsonValue| {
            let s = s3.clone();
            Box::pin(async move {
                let path = args.get("path").and_then(|p| p.as_str());
                match s.list_files(path).await {
                    Ok(files) => serde_json::json!({"count": files.len(), "files": files}),
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;

        let s4 = sh.clone();
        server.register_tool("sync_folder", Arc::new(move |args: JsonValue| {
            let s = s4.clone();
            Box::pin(async move {
                let local = args.get("localFolder").and_then(|p| p.as_str()).unwrap_or("");
                let remote = args.get("remoteFolder").and_then(|p| p.as_str()).unwrap_or("");
                match s.sync_folder(local, remote).await {
                    Ok(n) => serde_json::json!({"success": true, "uploaded": n}),
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;

        let s5 = sh.clone();
        server.register_tool("delete_file", Arc::new(move |args: JsonValue| {
            let s = s5.clone();
            Box::pin(async move {
                let path = args.get("path").and_then(|p| p.as_str()).unwrap_or("");
                match s.delete_file(path).await {
                    Ok(ok) => serde_json::json!({"success": ok}),
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;
    }

    // ── Remote tools ──────────────────────────────────────────────────────
    if let Some(rem) = remote {
        let r = rem.clone();
        server.register_tool("get_screen_stream", Arc::new(move |args: JsonValue| {
            let r = r.clone();
            Box::pin(async move {
                let quality = args.get("quality").and_then(|q| q.as_u64()).unwrap_or(80) as u32;
                let max_width = args.get("maxWidth").and_then(|w| w.as_u64()).unwrap_or(1920) as u32;
                match r.request_screen(quality, max_width).await {
                    Ok(()) => serde_json::json!({"success": true, "message": "Screen stream requested"}),
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;

        let r2 = rem.clone();
        server.register_tool("send_input", Arc::new(move |args: JsonValue| {
            let r = r2.clone();
            Box::pin(async move {
                let input_type = args.get("inputType").and_then(|t| t.as_str()).unwrap_or("");
                let data = args.get("data").map(|d| d.to_string()).unwrap_or_else(|| "{}".into());
                match r.send_input(input_type, &data).await {
                    Ok(()) => serde_json::json!({"success": true}),
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;
    }

    // ── Window management tools ───────────────────────────────────────────
    if let Some(wm) = windows {
        let w = wm.clone();
        server.register_tool("list_windows", Arc::new(move |_args: JsonValue| {
            let w = w.clone();
            Box::pin(async move {
                match w.list_windows().await {
                    Ok(wins) => {
                        let list: Vec<JsonValue> = wins.iter().map(|wi| {
                            serde_json::json!({
                                "title": wi.title, "handle": wi.handle,
                                "pid": wi.pid, "x": wi.x, "y": wi.y,
                                "width": wi.width, "height": wi.height,
                            })
                        }).collect();
                        serde_json::json!({"count": wins.len(), "windows": list})
                    }
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;

        let w2 = wm.clone();
        server.register_tool("focus_window", Arc::new(move |args: JsonValue| {
            let w = w2.clone();
            Box::pin(async move {
                let title = args.get("title").and_then(|t| t.as_str()).unwrap_or("");
                match w.list_windows().await {
                    Ok(wins) => {
                        if let Some(wi) = wins.iter().find(|wi| wi.title.to_lowercase().contains(&title.to_lowercase())) {
                            let _ = w.focus_window(wi.handle).await;
                            return serde_json::json!({"success": true, "title": wi.title});
                        }
                        serde_json::json!({"success": false, "error": "Window not found"})
                    }
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;

        let w3 = wm.clone();
        server.register_tool("close_app", Arc::new(move |args: JsonValue| {
            let w = w3.clone();
            Box::pin(async move {
                let title = args.get("title").and_then(|t| t.as_str()).unwrap_or("");
                match w.list_windows().await {
                    Ok(wins) => {
                        if let Some(wi) = wins.iter().find(|wi| wi.title.to_lowercase().contains(&title.to_lowercase())) {
                            let _ = w.close_window(wi.handle).await;
                            return serde_json::json!({"success": true});
                        }
                        serde_json::json!({"success": false, "error": "Window not found"})
                    }
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;
    }

    // launch_app (always available if process manager exists)
    if let Some(proc_mgr) = process {
        let pm = proc_mgr.clone();
        server.register_tool("launch_app", Arc::new(move |args: JsonValue| {
            let pm = pm.clone();
            Box::pin(async move {
                let app = args.get("app").and_then(|a| a.as_str()).unwrap_or("");
                let blocked = ["format", "del /s", "rm -rf /", "mkfs", "dd if=/dev/zero"];
                if blocked.iter().any(|b| app.to_lowercase().contains(b)) {
                    return serde_json::json!({"error": "Application blocked by security policy"});
                }
                let cmd_args = args.get("args").and_then(|a| a.as_str()).unwrap_or("");
                match pm.run_command(app, cmd_args, None).await {
                    Ok(r) => serde_json::json!({"launched": r.exit_code == 0, "app": app}),
                    Err(e) => serde_json::json!({"error": format!("{}", e)}),
                }
            })
        })).await;
    }

    let tools = server.get_tool_names().await;
    tracing::info!("All MCP tools registered ({} tools)", tools.len());
}

/// Recursively find files matching a glob-like pattern (simple prefix/suffix match).
fn find_files_recursive<'a>(
    path: &'a str, pattern: &'a str, depth: u32, max_depth: u32,
) -> std::pin::Pin<Box<dyn std::future::Future<Output = anyhow::Result<Vec<serde_json::Value>>> + Send + 'a>> {
    Box::pin(async move {
        if depth > max_depth { return Ok(vec![]); }
        let mut results = Vec::new();
        let mut entries = tokio::fs::read_dir(path).await?;
        while let Ok(Some(entry)) = entries.next_entry().await {
            let ft = entry.file_type().await?;
            if ft.is_dir() {
                let sub = find_files_recursive(&entry.path().to_string_lossy(), pattern, depth + 1, max_depth).await?;
                results.extend(sub);
            } else if ft.is_file() {
                let name = entry.file_name().to_string_lossy().to_string();
                let matches = pattern == "*" || name.contains(pattern.trim_matches('*'));
                if matches {
                    let meta = entry.metadata().await?;
                    results.push(serde_json::json!({"path": entry.path().to_string_lossy(), "size": meta.len()}));
                }
            }
            if results.len() >= 100 { break; }
        }
        Ok(results)
    })
}
