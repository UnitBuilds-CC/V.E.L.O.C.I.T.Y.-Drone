//! Messenger connector — WebSocket-based messaging with auto-reconnect.

use drone_core::config::MessengerConfig;
use futures_util::{SinkExt, StreamExt};
use serde_json::{json, Value};
use std::sync::Arc;
use tokio::sync::{watch, Mutex};
use tokio_tungstenite::{connect_async, tungstenite::Message};

type WsStream = tokio_tungstenite::WebSocketStream<
    tokio_tungstenite::MaybeTlsStream<tokio::net::TcpStream>,
>;

/// Message event: (from, content, message_id).
pub type MessageHandler = Box<dyn Fn(String, String, String) -> futures_util::future::BoxFuture<'static, ()> + Send + Sync>;
/// Media event: (from, url, media_type).
pub type MediaHandler = Box<dyn Fn(String, String, String) -> futures_util::future::BoxFuture<'static, ()> + Send + Sync>;
/// Connection state change.
pub type ConnectionHandler = Box<dyn Fn(bool) -> futures_util::future::BoxFuture<'static, ()> + Send + Sync>;

pub struct MessengerConnector {
    config: MessengerConfig,
    drone_id: String,
    ws: Arc<Mutex<Option<futures_util::stream::SplitSink<WsStream, Message>>>>,
    connected: watch::Sender<bool>,
    connected_rx: watch::Receiver<bool>,
    on_message: Arc<Mutex<Option<MessageHandler>>>,
    on_media: Arc<Mutex<Option<MediaHandler>>>,
    on_connection: Arc<Mutex<Option<ConnectionHandler>>>,
}

impl MessengerConnector {
    pub fn new(config: MessengerConfig, drone_id: String) -> Self {
        let (tx, rx) = watch::channel(false);
        Self {
            config,
            drone_id,
            ws: Arc::new(Mutex::new(None)),
            connected: tx,
            connected_rx: rx,
            on_message: Arc::new(Mutex::new(None)),
            on_media: Arc::new(Mutex::new(None)),
            on_connection: Arc::new(Mutex::new(None)),
        }
    }

    pub fn is_connected(&self) -> bool {
        *self.connected_rx.borrow()
    }

    pub fn connected_watcher(&self) -> watch::Receiver<bool> {
        self.connected_rx.clone()
    }

    pub async fn set_on_message<F>(&self, handler: F)
    where F: Fn(String, String, String) -> futures_util::future::BoxFuture<'static, ()> + Send + Sync + 'static {
        *self.on_message.lock().await = Some(Box::new(handler));
    }

    pub async fn set_on_media<F>(&self, handler: F)
    where F: Fn(String, String, String) -> futures_util::future::BoxFuture<'static, ()> + Send + Sync + 'static {
        *self.on_media.lock().await = Some(Box::new(handler));
    }

    pub async fn set_on_connection<F>(&self, handler: F)
    where F: Fn(bool) -> futures_util::future::BoxFuture<'static, ()> + Send + Sync + 'static {
        *self.on_connection.lock().await = Some(Box::new(handler));
    }

    /// Connect and run the receive loop. Auto-reconnects if configured.
    pub async fn connect(&self, cancel: watch::Receiver<bool>) -> anyhow::Result<()> {
        let server_url = self.config.server_url.as_deref()
            .ok_or_else(|| anyhow::anyhow!("Messenger server_url not configured"))?;

        let mut reconnect_attempts = 0u32;

        loop {
            if *cancel.borrow() { break; }

            match self.connect_once(server_url, cancel.clone()).await {
                Ok(()) => {
                    tracing::info!("Messenger connection closed cleanly");
                    break;
                }
                Err(e) => {
                    self.set_connected(false).await;
                    if !self.config.auto_reconnect || *cancel.borrow() {
                        return Err(e);
                    }
                    reconnect_attempts += 1;
                    let delay = std::cmp::min(
                        1000u64 * 2u64.pow(reconnect_attempts.min(10)),
                        30_000,
                    );
                    tracing::warn!(
                        "Messenger disconnected (attempt {}): {}. Reconnecting in {}ms",
                        reconnect_attempts, e, delay,
                    );
                    tokio::select! {
                        _ = tokio::time::sleep(std::time::Duration::from_millis(delay)) => {}
                        _ = cancel_wait(cancel.clone()) => { break; }
                    }
                }
            }
        }
        Ok(())
    }

    async fn connect_once(&self, url: &str, cancel: watch::Receiver<bool>) -> anyhow::Result<()> {
        let separator = if url.contains('?') { '&' } else { '?' };
        let mut connect_url = format!(
            "{}{}username={}&device_id=drone",
            url, separator,
            urlencoded(&self.drone_id),
        );
        if let Some(secret) = &self.config.connection_secret {
            connect_url.push_str(&format!("&secret={}", urlencoded(secret)));
        }

        let (ws_stream, _) = connect_async(&connect_url).await?;
        let (mut writer, mut reader) = ws_stream.split();

        // Send auth message
        let auth = json!({
            "type": "auth",
            "username": self.drone_id,
            "secret": self.config.connection_secret,
        });
        writer.send(Message::Text(auth.to_string())).await?;

        // Store writer for sending
        *self.ws.lock().await = Some(writer);
        self.set_connected(true).await;
        tracing::info!("Connected to Messenger at {}", url);

        // Receive loop
        let result = self.receive_loop(&mut reader, cancel).await;

        *self.ws.lock().await = None;
        result
    }

    async fn receive_loop(
        &self,
        reader: &mut futures_util::stream::SplitStream<WsStream>,
        cancel: watch::Receiver<bool>,
    ) -> anyhow::Result<()> {
        loop {
            let msg = tokio::select! {
                msg = reader.next() => {
                    match msg {
                        Some(Ok(Message::Text(text))) => text,
                        Some(Ok(Message::Close(_))) => return Ok(()),
                        Some(Err(e)) => return Err(anyhow::anyhow!("WebSocket error: {}", e)),
                        None => return Ok(()),
                        _ => continue, // Binary, Ping, Pong
                    }
                }
                _ = cancel_wait(cancel.clone()) => return Ok(()),
            };

            if let Ok(val) = serde_json::from_str::<Value>(&msg) {
                self.dispatch_message(&val).await;
            }
        }
    }

    async fn dispatch_message(&self, val: &Value) {
        let msg_type = val.get("type").and_then(|v| v.as_str()).unwrap_or("");
        match msg_type {
            "chat" => {
                if let Some(handler) = self.on_message.lock().await.as_ref() {
                    let from = val.get("from").and_then(|v| v.as_str()).unwrap_or("").to_string();
                    let content = val.get("content").and_then(|v| v.as_str()).unwrap_or("").to_string();
                    let id = val.get("id").and_then(|v| v.as_str()).unwrap_or("").to_string();
                    handler(from, content, id).await;
                }
            }
            "media" => {
                if let Some(handler) = self.on_media.lock().await.as_ref() {
                    let from = val.get("from").and_then(|v| v.as_str()).unwrap_or("").to_string();
                    let url = val.get("url").and_then(|v| v.as_str()).unwrap_or("").to_string();
                    let mt = val.get("mediaType").and_then(|v| v.as_str()).unwrap_or("").to_string();
                    handler(from, url, mt).await;
                }
            }
            _ => {
                tracing::debug!("Messenger: unhandled message type: {}", msg_type);
            }
        }
    }

    async fn set_connected(&self, state: bool) {
        let _ = self.connected.send(state);
        if let Some(handler) = self.on_connection.lock().await.as_ref() {
            handler(state).await;
        }
    }

    /// Send a chat message to a user.
    pub async fn send_message(&self, to: &str, content: &str) -> anyhow::Result<()> {
        let msg = json!({ "type": "chat", "to": to, "content": content });
        self.send_json(&msg).await
    }

    /// Send a group message.
    pub async fn send_group_message(&self, group_id: &str, content: &str) -> anyhow::Result<()> {
        let msg = json!({ "type": "group_message", "group": group_id, "content": content });
        self.send_json(&msg).await
    }

    /// Upload media (base64-encoded) via WebSocket.
    pub async fn upload_media(&self, file_path: &str, media_type: &str) -> anyhow::Result<String> {
        let data = tokio::fs::read(file_path).await?;
        let b64 = base64::Engine::encode(&base64::engine::general_purpose::STANDARD, &data);
        let file_name = std::path::Path::new(file_path)
            .file_name()
            .map(|n| n.to_string_lossy().to_string())
            .unwrap_or_default();

        let msg = json!({
            "type": "media_upload",
            "fileName": file_name,
            "mediaType": media_type,
            "data": b64,
        });
        self.send_json(&msg).await?;
        Ok(file_name)
    }

    /// Download media from URL to local path.
    pub async fn download_media(&self, url: &str, local_path: &str) -> anyhow::Result<()> {
        let resp = reqwest::get(url).await?;
        let bytes = resp.bytes().await?;
        if let Some(dir) = std::path::Path::new(local_path).parent() {
            tokio::fs::create_dir_all(dir).await.ok();
        }
        tokio::fs::write(local_path, &bytes).await?;
        tracing::info!("Downloaded media to {}", local_path);
        Ok(())
    }

    /// Send a call signal (WebRTC-style).
    pub async fn send_call_signal(&self, to: &str, signal_type: &str, payload: &Value) -> anyhow::Result<()> {
        let msg = json!({ "type": "call_signal", "to": to, "signalType": signal_type, "payload": payload });
        self.send_json(&msg).await
    }

    /// Send a control message.
    pub async fn send_control(&self, command: &str, payload: &str) -> anyhow::Result<()> {
        let msg = json!({ "type": "control", "command": command, "payload": payload });
        self.send_json(&msg).await
    }

    async fn send_json(&self, val: &Value) -> anyhow::Result<()> {
        let mut guard = self.ws.lock().await;
        let writer = guard.as_mut()
            .ok_or_else(|| anyhow::anyhow!("Not connected to Messenger"))?;
        writer.send(Message::Text(val.to_string())).await?;
        Ok(())
    }
}

/// Simple percent-encoding for URL query parameters.
fn urlencoded(s: &str) -> String {
    let mut out = String::new();
    for b in s.bytes() {
        match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => {
                out.push(b as char);
            }
            _ => {
                out.push_str(&format!("%{:02X}", b));
            }
        }
    }
    out
}

/// Await a cancel signal from a watch channel.
async fn cancel_wait(mut rx: watch::Receiver<bool>) {
    while !*rx.borrow() {
        if rx.changed().await.is_err() { break; }
    }
}
