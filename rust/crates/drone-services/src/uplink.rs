//! Uplink connector — bidirectional connection to the Velocity server.
//! Supports WebSocket transport with auto-reconnect and heartbeat.

use drone_core::config::UplinkConfig;
use drone_core::protocol::{NmcpFrame, NmcpFrameTypes};
use futures_util::{SinkExt, StreamExt};
use serde_json::Value as JsonValue;
use std::sync::Arc;
use tokio::sync::{watch, Mutex};
use tokio_tungstenite::{connect_async, tungstenite::Message};

type WsStream = tokio_tungstenite::WebSocketStream<
    tokio_tungstenite::MaybeTlsStream<tokio::net::TcpStream>,
>;

const HEARTBEAT_INTERVAL_SEC: u64 = 30;
const MAX_RECONNECT_ATTEMPTS: u32 = 10;

/// Request handler: receives JSON-RPC request, returns response JSON string.
pub type RequestHandler = Box<dyn Fn(JsonValue) -> futures_util::future::BoxFuture<'static, String> + Send + Sync>;
/// Notification handler: receives JSON-RPC notification.
pub type NotificationHandler = Box<dyn Fn(JsonValue) -> futures_util::future::BoxFuture<'static, ()> + Send + Sync>;

pub struct VelocityConnection {
    config: UplinkConfig,
    ws: Arc<Mutex<Option<futures_util::stream::SplitSink<WsStream, Message>>>>,
    connected_tx: watch::Sender<bool>,
    connected_rx: watch::Receiver<bool>,
    sequence: Arc<std::sync::atomic::AtomicU32>,
    on_request: Arc<Mutex<Option<RequestHandler>>>,
    on_notification: Arc<Mutex<Option<NotificationHandler>>>,
}

impl VelocityConnection {
    pub fn new(config: UplinkConfig) -> Self {
        let (tx, rx) = watch::channel(false);
        Self {
            config,
            ws: Arc::new(Mutex::new(None)),
            connected_tx: tx,
            connected_rx: rx,
            sequence: Arc::new(std::sync::atomic::AtomicU32::new(0)),
            on_request: Arc::new(Mutex::new(None)),
            on_notification: Arc::new(Mutex::new(None)),
        }
    }

    pub fn is_connected(&self) -> bool {
        *self.connected_rx.borrow()
    }

    pub async fn set_on_request<F>(&self, handler: F)
    where F: Fn(JsonValue) -> futures_util::future::BoxFuture<'static, String> + Send + Sync + 'static {
        *self.on_request.lock().await = Some(Box::new(handler));
    }

    pub async fn set_on_notification<F>(&self, handler: F)
    where F: Fn(JsonValue) -> futures_util::future::BoxFuture<'static, ()> + Send + Sync + 'static {
        *self.on_notification.lock().await = Some(Box::new(handler));
    }

    /// Connect with auto-reconnect.
    pub async fn connect(&self, cancel: watch::Receiver<bool>) -> anyhow::Result<()> {
        let server_url = self.config.websocket_url.as_deref()
            .ok_or_else(|| anyhow::anyhow!("Uplink websocket_url not configured"))?;

        let mut attempts = 0u32;
        loop {
            if *cancel.borrow() { break; }
            match self.connect_once(server_url, cancel.clone()).await {
                Ok(()) => {
                    tracing::info!("Uplink connection closed cleanly");
                    break;
                }
                Err(e) => {
                    let _ = self.connected_tx.send(false);
                    if !self.config.auto_reconnect || *cancel.borrow() || attempts >= MAX_RECONNECT_ATTEMPTS {
                        return Err(e);
                    }
                    attempts += 1;
                    let delay = std::cmp::min(1000u64 * attempts as u64, 30_000);
                    tracing::warn!(
                        "Uplink disconnected (attempt {}/{}): {}. Retrying in {}ms",
                        attempts, MAX_RECONNECT_ATTEMPTS, e, delay,
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
        let (ws_stream, _) = connect_async(url).await?;
        let (writer, mut reader) = ws_stream.split();

        *self.ws.lock().await = Some(writer);
        let _ = self.connected_tx.send(true);
        tracing::info!("Uplink WebSocket connected at {}", url);

        // Start heartbeat
        let hb_ws = self.ws.clone();
        let hb_seq = self.sequence.clone();
        let hb_cancel = cancel.clone();
        let heartbeat = tokio::spawn(async move {
            heartbeat_loop(hb_ws, hb_seq, hb_cancel).await;
        });

        // Receive loop
        let result = self.receive_loop(&mut reader, cancel).await;

        heartbeat.abort();
        *self.ws.lock().await = None;
        let _ = self.connected_tx.send(false);
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
                        Some(Ok(Message::Binary(data))) => {
                            // NMCP binary frame — skip header-only frames (heartbeat etc.)
                            if data.len() > drone_core::protocol::HEADER_SIZE {
                                let json_str = String::from_utf8_lossy(&data[drone_core::protocol::HEADER_SIZE..]);
                                if let Ok(val) = serde_json::from_str::<JsonValue>(&json_str) {
                                    self.dispatch_message(&val).await;
                                }
                            }
                            continue;
                        }
                        Some(Ok(Message::Close(_))) => return Ok(()),
                        Some(Err(e)) => return Err(anyhow::anyhow!("Uplink WebSocket error: {}", e)),
                        None => return Ok(()),
                        _ => continue,
                    }
                }
                _ = cancel_wait(cancel.clone()) => return Ok(()),
            };

            if let Ok(val) = serde_json::from_str::<JsonValue>(&msg) {
                self.dispatch_message(&val).await;
            }
        }
    }

    async fn dispatch_message(&self, val: &JsonValue) {
        // If it has a "method" field, it's a request; otherwise it's a notification
        if val.get("method").is_some() {
            if let Some(handler) = self.on_request.lock().await.as_ref() {
                let _response = handler(val.clone()).await;
                // Response is sent via send_response
            }
        } else {
            if let Some(handler) = self.on_notification.lock().await.as_ref() {
                handler(val.clone()).await;
            }
        }
    }

    /// Send a JSON-RPC response back to the server.
    pub async fn send_response(&self, json: &str) -> anyhow::Result<()> {
        let seq = self.sequence.fetch_add(1, std::sync::atomic::Ordering::Relaxed) + 1;
        let frame = NmcpFrame::new(NmcpFrameTypes::JSON_RPC_RESPONSE, seq, json.as_bytes().to_vec());
        self.send_frame_data(&frame.to_bytes()).await
    }

    /// Send a JSON-RPC notification to the server.
    pub async fn send_notification(&self, json: &str) -> anyhow::Result<()> {
        let seq = self.sequence.fetch_add(1, std::sync::atomic::Ordering::Relaxed) + 1;
        let frame = NmcpFrame::new(NmcpFrameTypes::JSON_RPC_NOTIFICATION, seq, json.as_bytes().to_vec());
        self.send_frame_data(&frame.to_bytes()).await
    }

    async fn send_frame_data(&self, data: &[u8]) -> anyhow::Result<()> {
        let mut guard = self.ws.lock().await;
        let writer = guard.as_mut()
            .ok_or_else(|| anyhow::anyhow!("Uplink not connected"))?;
        writer.send(Message::Binary(data.to_vec())).await?;
        Ok(())
    }
}

async fn heartbeat_loop(
    ws: Arc<Mutex<Option<futures_util::stream::SplitSink<WsStream, Message>>>>,
    seq: Arc<std::sync::atomic::AtomicU32>,
    cancel: watch::Receiver<bool>,
) {
    loop {
        tokio::select! {
            _ = tokio::time::sleep(std::time::Duration::from_secs(HEARTBEAT_INTERVAL_SEC)) => {}
            _ = cancel_wait(cancel.clone()) => return,
        }

        let mut guard = ws.lock().await;
        if let Some(writer) = guard.as_mut() {
            let s = seq.fetch_add(1, std::sync::atomic::Ordering::Relaxed) + 1;
            let frame = NmcpFrame::new(NmcpFrameTypes::HEARTBEAT, s, vec![]);
            let bytes = frame.to_bytes();
            if writer.send(Message::Binary(bytes)).await.is_err() {
                break;
            }
        } else {
            break;
        }
    }
}

async fn cancel_wait(mut rx: watch::Receiver<bool>) {
    while !*rx.borrow() {
        if rx.changed().await.is_err() { break; }
    }
}
