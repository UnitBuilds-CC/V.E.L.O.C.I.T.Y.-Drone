//! MCP Server — dual transport (NMCP shmem + WebSocket).

use std::collections::HashMap;
use std::sync::atomic::{AtomicI64, AtomicUsize, Ordering};
use std::sync::Arc;
use tokio::sync::RwLock;
use serde_json::Value as JsonValue;

/// Shared memory layout constants (matches V.E.L.O.C.I.T.Y.-MCP).
const SHMEM_TOTAL_SIZE: usize = 65536;
const REQ_STATE_OFFSET: usize = 0;
const REQ_LEN_OFFSET: usize = 1;
const REQ_PAYLOAD_OFFSET: usize = 5;
const REQ_PAYLOAD_SIZE: usize = 4096;
const RES_STATE_OFFSET: usize = 4100;
const RES_LEN_OFFSET: usize = 4101;
const RES_PAYLOAD_OFFSET: usize = 4105;
const RES_PAYLOAD_SIZE: usize = SHMEM_TOTAL_SIZE - RES_PAYLOAD_OFFSET;

// Atomic state machine
const STATE_IDLE: u8 = 0;
const STATE_REQ_READY: u8 = 1;
const STATE_PROCESSING: u8 = 2;
const STATE_RES_READY: u8 = 3;
#[allow(dead_code)]
const STATE_ERROR: u8 = 4;

/// Maximum WebSocket message size (10 MB).
#[allow(dead_code)]
const MAX_MESSAGE_SIZE: usize = 10 * 1024 * 1024;

/// Maximum concurrent WebSocket connections.
const MAX_CONNECTIONS: usize = 16;

/// Tool handler function type.
pub type ToolHandler = Arc<dyn Fn(JsonValue) -> std::pin::Pin<Box<dyn std::future::Future<Output = JsonValue> + Send>> + Send + Sync>;

/// MCP Server with dual transport.
pub struct McpServer {
    tools: Arc<RwLock<HashMap<String, ToolHandler>>>,
    auth_token: Arc<RwLock<Option<String>>>,
    total_requests: Arc<AtomicI64>,
    total_errors: Arc<AtomicI64>,
    total_rejected: Arc<AtomicI64>,
    connected_clients: Arc<AtomicUsize>,
    start_time_ms: i64,
}

impl McpServer {
    pub fn new() -> Self {
        Self {
            tools: Arc::new(RwLock::new(HashMap::new())),
            auth_token: Arc::new(RwLock::new(None)),
            total_requests: Arc::new(AtomicI64::new(0)),
            total_errors: Arc::new(AtomicI64::new(0)),
            total_rejected: Arc::new(AtomicI64::new(0)),
            connected_clients: Arc::new(AtomicUsize::new(0)),
            start_time_ms: chrono::Utc::now().timestamp_millis(),
        }
    }

    /// Register a tool handler.
    pub async fn register_tool(&self, name: impl Into<String>, handler: ToolHandler) {
        self.tools.write().await.insert(name.into(), handler);
    }

    /// Set the auth token for WebSocket connections.
    pub async fn set_auth_token(&self, token: Option<String>) {
        *self.auth_token.write().await = token;
    }

    /// Get the list of registered tool names.
    pub async fn get_tool_names(&self) -> Vec<String> {
        self.tools.read().await.keys().cloned().collect()
    }

    /// Directly invoke a tool by name.
    pub async fn invoke_tool(&self, name: &str, args: JsonValue) -> Option<JsonValue> {
        let tools = self.tools.read().await;
        if let Some(handler) = tools.get(name) {
            Some(handler(args).await)
        } else {
            None
        }
    }

    pub fn connected_client_count(&self) -> usize {
        self.connected_clients.load(Ordering::Relaxed)
    }

    pub fn total_requests(&self) -> i64 {
        self.total_requests.load(Ordering::Relaxed)
    }

    pub fn total_errors(&self) -> i64 {
        self.total_errors.load(Ordering::Relaxed)
    }

    /// Handle a JSON-RPC request and return a response.
    pub async fn handle_request(&self, request: &JsonValue, client_addr: &str) -> JsonValue {
        self.total_requests.fetch_add(1, Ordering::Relaxed);

        let id = request.get("id")
            .map(|v| v.clone())
            .unwrap_or(JsonValue::Null);

        let method = match request.get("method").and_then(|m| m.as_str()) {
            Some(m) => m,
            None => return serde_json::json!({
                "jsonrpc": "2.0", "id": id,
                "error": { "code": -32600, "message": "Invalid Request: missing method" }
            }),
        };

        match method {
            "initialize" => serde_json::json!({
                "jsonrpc": "2.0", "id": id,
                "result": {
                    "protocolVersion": "2024-11-05",
                    "capabilities": { "tools": {} },
                    "serverInfo": { "name": "velocity-drone", "version": "1.0.0" }
                }
            }),
            "tools/list" => {
                let tools = self.tools.read().await;
                let tool_list: Vec<JsonValue> = tools.keys().map(|name| {
                    serde_json::json!({
                        "name": name,
                        "description": get_tool_description(name),
                        "inputSchema": { "type": "object" }
                    })
                }).collect();
                serde_json::json!({
                    "jsonrpc": "2.0", "id": id,
                    "result": { "tools": tool_list }
                })
            },
            "tools/call" => self.handle_tool_call(request, &id, client_addr).await,
            "notifications/initialized" => JsonValue::Null,
            _ => serde_json::json!({
                "jsonrpc": "2.0", "id": id,
                "error": { "code": -32601, "message": format!("Method not found: {}", method) }
            }),
        }
    }

    async fn handle_tool_call(&self, request: &JsonValue, id: &JsonValue, _client_addr: &str) -> JsonValue {
        let params = match request.get("params") {
            Some(p) => p,
            None => return serde_json::json!({
                "jsonrpc": "2.0", "id": id,
                "error": { "code": -32602, "message": "Invalid params: missing params" }
            }),
        };

        let tool_name = match params.get("name").and_then(|n| n.as_str()) {
            Some(n) => n,
            None => return serde_json::json!({
                "jsonrpc": "2.0", "id": id,
                "error": { "code": -32602, "message": "Invalid params: missing tool name" }
            }),
        };

        let args = params.get("arguments").cloned().unwrap_or(JsonValue::Object(serde_json::Map::new()));

        let tools = self.tools.read().await;
        match tools.get(tool_name) {
            Some(handler) => {
                let result = handler(args).await;
                serde_json::json!({
                    "jsonrpc": "2.0", "id": id,
                    "result": { "content": [{ "type": "text", "text": result }] }
                })
            }
            None => serde_json::json!({
                "jsonrpc": "2.0", "id": id,
                "result": { "content": [{ "type": "text", "text": format!("Unknown tool: {}", tool_name) }], "isError": true }
            }),
        }
    }

    /// Run the NMCP shared memory server.
    #[cfg(target_os = "windows")]
    pub async fn run_shmem(&self, buffer_path: &str, buffer_size: usize, cancel: tokio::sync::watch::Receiver<bool>) -> anyhow::Result<()> {
        use std::fs::OpenOptions;
        use std::io::{Read, Seek, SeekFrom, Write};

        tracing::info!("MCP NMCP server starting at {} ({} bytes, atomic shmem)", buffer_path, buffer_size);

        // Memory-mapped file via Windows API would go here
        // For now, use a file-backed approach
        let mut file = OpenOptions::new()
            .read(true).write(true).create(true)
            .open(buffer_path)?;
        file.set_len(buffer_size as u64)?;

        let mut _buf = vec![0u8; buffer_size];

        loop {
            if *cancel.borrow() { break; }

            // Read current state
            file.seek(SeekFrom::Start(REQ_STATE_OFFSET as u64))?;
            let mut state_byte = [0u8; 1];
            file.read_exact(&mut state_byte)?;

            if state_byte[0] == STATE_REQ_READY {
                // Transition to PROCESSING
                file.seek(SeekFrom::Start(REQ_STATE_OFFSET as u64))?;
                file.write_all(&[STATE_PROCESSING])?;

                // Read request length
                file.seek(SeekFrom::Start(REQ_LEN_OFFSET as u64))?;
                let mut len_bytes = [0u8; 4];
                file.read_exact(&mut len_bytes)?;
                let req_len = u32::from_le_bytes(len_bytes) as usize;

                if req_len > 0 && req_len <= REQ_PAYLOAD_SIZE {
                    // Read payload
                    file.seek(SeekFrom::Start(REQ_PAYLOAD_OFFSET as u64))?;
                    let mut payload = vec![0u8; req_len];
                    file.read_exact(&mut payload)?;

                    // Parse JSON and handle
                    let json_str = String::from_utf8_lossy(&payload);
                    let request: JsonValue = serde_json::from_str(&json_str).unwrap_or(JsonValue::Null);
                    let response = self.handle_request(&request, "shmem").await;
                    let response_json = serde_json::to_string(&response).unwrap_or_default();
                    let response_bytes = response_json.as_bytes();

                    if response_bytes.len() <= RES_PAYLOAD_SIZE {
                        // Write response length
                        file.seek(SeekFrom::Start(RES_LEN_OFFSET as u64))?;
                        file.write_all(&(response_bytes.len() as u32).to_le_bytes())?;

                        // Write response payload
                        file.seek(SeekFrom::Start(RES_PAYLOAD_OFFSET as u64))?;
                        file.write_all(response_bytes)?;

                        // Signal RES_READY
                        file.seek(SeekFrom::Start(RES_STATE_OFFSET as u64))?;
                        file.write_all(&[STATE_RES_READY])?;
                    }
                }

                // Reset request channel to IDLE
                file.seek(SeekFrom::Start(REQ_STATE_OFFSET as u64))?;
                file.write_all(&[STATE_IDLE])?;
            }

            tokio::task::yield_now().await;
        }

        Ok(())
    }

    /// Run the WebSocket JSON-RPC server.
    pub async fn run_websocket(&self, url: &str, _cancel: tokio::sync::watch::Receiver<bool>) -> anyhow::Result<()> {
        use tokio::net::TcpListener;
        use tokio_tungstenite::accept_async;
        use futures_util::StreamExt;

        tracing::info!("MCP WebSocket server starting at {}", url);

        let listener = TcpListener::bind(url).await?;

        loop {
            tokio::select! {
                result = listener.accept() => {
                    match result {
                        Ok((stream, addr)) => {
                            if self.connected_client_count() >= MAX_CONNECTIONS {
                                self.total_rejected.fetch_add(1, Ordering::Relaxed);
                                tracing::warn!("Rejected connection from {}: max connections", addr);
                                continue;
                            }

                            let server = self.clone_ref();
                            tokio::spawn(async move {
                                match accept_async(stream).await {
                                    Ok(ws) => {
                                        server.connected_clients.fetch_add(1, Ordering::Relaxed);
                                        tracing::info!("WebSocket client connected from {}", addr);

                                        let (_, mut read) = ws.split();
                                        while let Some(msg) = read.next().await {
                                            match msg {
                                                Ok(tokio_tungstenite::tungstenite::Message::Text(text)) => {
                                                    if let Ok(request) = serde_json::from_str::<JsonValue>(&text) {
                                                        let _response = server.handle_request(&request, &addr.to_string()).await;
                                                    }
                                                }
                                                Ok(tokio_tungstenite::tungstenite::Message::Close(_)) => break,
                                                Err(_) => break,
                                                _ => {}
                                            }
                                        }

                                        server.connected_clients.fetch_sub(1, Ordering::Relaxed);
                                        tracing::info!("WebSocket client disconnected from {}", addr);
                                    }
                                    Err(e) => {
                                        tracing::warn!("WebSocket handshake failed from {}: {}", addr, e);
                                    }
                                }
                            });
                        }
                        Err(e) => {
                            tracing::error!("Accept error: {}", e);
                        }
                    }
                }
                _ = tokio::signal::ctrl_c() => {
                    break;
                }
            }
        }

        Ok(())
    }

    /// Clone the Arc references for spawning tasks.
    fn clone_ref(&self) -> McpServerRef {
        McpServerRef {
            tools: self.tools.clone(),
            auth_token: self.auth_token.clone(),
            total_requests: self.total_requests.clone(),
            total_errors: self.total_errors.clone(),
            total_rejected: self.total_rejected.clone(),
            connected_clients: self.connected_clients.clone(),
            start_time_ms: self.start_time_ms,
        }
    }
}

/// Lightweight cloneable reference to McpServer state.
#[allow(dead_code)]
struct McpServerRef {
    tools: Arc<RwLock<HashMap<String, ToolHandler>>>,
    auth_token: Arc<RwLock<Option<String>>>,
    total_requests: Arc<AtomicI64>,
    total_errors: Arc<AtomicI64>,
    total_rejected: Arc<AtomicI64>,
    connected_clients: Arc<AtomicUsize>,
    start_time_ms: i64,
}

impl McpServerRef {
    async fn handle_request(&self, request: &JsonValue, _client_addr: &str) -> JsonValue {
        self.total_requests.fetch_add(1, Ordering::Relaxed);

        let id = request.get("id").cloned().unwrap_or(JsonValue::Null);
        let method = match request.get("method").and_then(|m| m.as_str()) {
            Some(m) => m,
            None => return serde_json::json!({
                "jsonrpc": "2.0", "id": id,
                "error": { "code": -32600, "message": "Invalid Request" }
            }),
        };

        match method {
            "initialize" => serde_json::json!({
                "jsonrpc": "2.0", "id": id,
                "result": {
                    "protocolVersion": "2024-11-05",
                    "capabilities": { "tools": {} },
                    "serverInfo": { "name": "velocity-drone", "version": "1.0.0" }
                }
            }),
            "tools/list" => {
                let tools = self.tools.read().await;
                let tool_list: Vec<JsonValue> = tools.keys().map(|name| {
                    serde_json::json!({ "name": name, "description": get_tool_description(name), "inputSchema": { "type": "object" } })
                }).collect();
                serde_json::json!({ "jsonrpc": "2.0", "id": id, "result": { "tools": tool_list } })
            },
            "tools/call" => {
                let params = request.get("params");
                let tool_name = params.and_then(|p| p.get("name")).and_then(|n| n.as_str()).unwrap_or("");
                let args = params.and_then(|p| p.get("arguments")).cloned().unwrap_or(JsonValue::Object(serde_json::Map::new()));

                let tools = self.tools.read().await;
                match tools.get(tool_name) {
                    Some(handler) => {
                        let result = handler(args).await;
                        serde_json::json!({ "jsonrpc": "2.0", "id": id, "result": { "content": [{ "type": "text", "text": result }] } })
                    }
                    None => serde_json::json!({ "jsonrpc": "2.0", "id": id, "result": { "content": [{ "type": "text", "text": format!("Unknown tool: {}", tool_name) }], "isError": true } })
                }
            },
            _ => serde_json::json!({ "jsonrpc": "2.0", "id": id, "error": { "code": -32601, "message": format!("Method not found: {}", method) } }),
        }
    }
}

fn get_tool_description(name: &str) -> &'static str {
    match name {
        "run_command" => "Run a shell command and return output",
        "read_file" => "Read a file's contents",
        "write_file" => "Write content to a file",
        "list_dir" => "List files in a directory",
        "capture_screen" => "Capture the screen as base64 PNG",
        "type_text" => "Type text using keyboard simulation",
        "click" => "Click at specified coordinates",
        _ => "No description available",
    }
}
