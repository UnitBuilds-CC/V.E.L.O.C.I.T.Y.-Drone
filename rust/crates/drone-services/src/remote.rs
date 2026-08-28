//! Remote connector — NMCP binary protocol over WebSocket with NDA payloads.
//! Custody-aware: logs tool calls and connections to the audit trail.

use drone_core::config::RemoteConfig;
use drone_core::protocol::{NmcpFrame, NmcpFrameTypes, HEADER_SIZE};
use futures_util::{SinkExt, StreamExt};
use std::sync::Arc;
use tokio::sync::{watch, Mutex};
use tokio_tungstenite::{connect_async, tungstenite::Message};

type WsStream = tokio_tungstenite::WebSocketStream<
    tokio_tungstenite::MaybeTlsStream<tokio::net::TcpStream>,
>;

const MAX_RECONNECT_ATTEMPTS: u32 = 10;
const HEARTBEAT_INTERVAL_SEC: u64 = 30;

/// Remote host info parsed from NDA triples.
#[derive(Debug, Clone)]
pub struct RemoteHost {
    pub id: String,
    pub name: String,
    pub address: String,
    pub is_online: bool,
    pub platform: String,
}

/// Callback types.
pub type ScreenFrameHandler = Box<dyn Fn(Vec<u8>) -> futures_util::future::BoxFuture<'static, ()> + Send + Sync>;
pub type ToolCallHandler = Box<dyn Fn(String, Vec<u8>, u32) -> futures_util::future::BoxFuture<'static, Vec<u8>> + Send + Sync>;
pub type HostsHandler = Box<dyn Fn(Vec<RemoteHost>) -> futures_util::future::BoxFuture<'static, ()> + Send + Sync>;

pub struct RemoteConnector {
    config: RemoteConfig,
    ws: Arc<Mutex<Option<futures_util::stream::SplitSink<WsStream, Message>>>>,
    connected_tx: watch::Sender<bool>,
    connected_rx: watch::Receiver<bool>,
    sequence: Arc<std::sync::atomic::AtomicU32>,
    on_screen_frame: Arc<Mutex<Option<ScreenFrameHandler>>>,
    on_tool_call: Arc<Mutex<Option<ToolCallHandler>>>,
    on_hosts_updated: Arc<Mutex<Option<HostsHandler>>>,
}

impl RemoteConnector {
    pub fn new(config: RemoteConfig) -> Self {
        let (tx, rx) = watch::channel(false);
        Self {
            config,
            ws: Arc::new(Mutex::new(None)),
            connected_tx: tx,
            connected_rx: rx,
            sequence: Arc::new(std::sync::atomic::AtomicU32::new(0)),
            on_screen_frame: Arc::new(Mutex::new(None)),
            on_tool_call: Arc::new(Mutex::new(None)),
            on_hosts_updated: Arc::new(Mutex::new(None)),
        }
    }

    pub fn is_connected(&self) -> bool {
        *self.connected_rx.borrow()
    }

    pub async fn set_on_screen_frame<F>(&self, handler: F)
    where F: Fn(Vec<u8>) -> futures_util::future::BoxFuture<'static, ()> + Send + Sync + 'static {
        *self.on_screen_frame.lock().await = Some(Box::new(handler));
    }

    pub async fn set_on_tool_call<F>(&self, handler: F)
    where F: Fn(String, Vec<u8>, u32) -> futures_util::future::BoxFuture<'static, Vec<u8>> + Send + Sync + 'static {
        *self.on_tool_call.lock().await = Some(Box::new(handler));
    }

    pub async fn set_on_hosts_updated<F>(&self, handler: F)
    where F: Fn(Vec<RemoteHost>) -> futures_util::future::BoxFuture<'static, ()> + Send + Sync + 'static {
        *self.on_hosts_updated.lock().await = Some(Box::new(handler));
    }

    /// Connect with auto-reconnect and run the receive loop.
    pub async fn connect(&self, cancel: watch::Receiver<bool>) -> anyhow::Result<()> {
        let server_url = self.config.server_url.as_deref()
            .ok_or_else(|| anyhow::anyhow!("Remote server_url not configured"))?;

        let mut attempts = 0u32;

        loop {
            if *cancel.borrow() { break; }

            match self.connect_once(server_url, cancel.clone()).await {
                Ok(()) => {
                    tracing::info!("Remote connection closed cleanly");
                    break;
                }
                Err(e) => {
                    let _ = self.connected_tx.send(false);
                    if *cancel.borrow() || attempts >= MAX_RECONNECT_ATTEMPTS {
                        tracing::error!("Remote max reconnect ({}) reached: {}", MAX_RECONNECT_ATTEMPTS, e);
                        return Err(e);
                    }
                    attempts += 1;
                    let delay = std::cmp::min(
                        1000u64 * 2u64.pow(attempts.min(10)),
                        30_000,
                    );
                    tracing::warn!(
                        "Remote disconnected (attempt {}/{}): {}. Retrying in {}ms",
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
        tracing::info!("Remote NMCP connected at {}", url);

        // Send handshake
        self.send_handshake().await?;

        // Start heartbeat task
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

    fn next_seq(&self) -> u32 {
        self.sequence.fetch_add(1, std::sync::atomic::Ordering::Relaxed) + 1
    }

    async fn send_handshake(&self) -> anyhow::Result<()> {
        // Simple NDA-like payload: key=value pairs separated by newlines
        let host_name = hostname::get()
            .map(|h| h.to_string_lossy().into_owned())
            .unwrap_or_else(|_| "unknown".to_string());
        let payload = format!(
            "drone.id={}\ndrone.platform={}\ndrone.version=1.0.0\ndrone.protocol=NMCP/NDA",
            host_name,
            std::env::consts::OS,
        );
        let frame = NmcpFrame::new(NmcpFrameTypes::HANDSHAKE, self.next_seq(), payload.into_bytes());
        self.send_frame(&frame).await
    }

    /// Send a screen capture frame.
    pub async fn send_screen_frame(&self, frame_data: Vec<u8>) -> anyhow::Result<()> {
        let payload = format!(
            "screen.type=capture\nscreen.format=bmp\nscreen.bytes={}",
            frame_data.len()
        );
        let mut combined = payload.into_bytes();
        combined.push(0); // separator
        combined.extend_from_slice(&frame_data);
        let frame = NmcpFrame::new(NmcpFrameTypes::SCREEN_CAPTURE, self.next_seq(), combined);
        self.send_frame(&frame).await
    }

    /// Send an input event frame.
    pub async fn send_input(&self, input_type: &str, data: &str) -> anyhow::Result<()> {
        let payload = format!("input.type={}\ninput.data={}", input_type, data);
        let frame = NmcpFrame::new(NmcpFrameTypes::INPUT_EVENT, self.next_seq(), payload.into_bytes());
        self.send_frame(&frame).await
    }

    /// Request screen stream from remote.
    pub async fn request_screen(&self, quality: u32, max_width: u32) -> anyhow::Result<()> {
        let payload = format!(
            "screen.request=start\nscreen.quality={}\nscreen.maxWidth={}",
            quality, max_width
        );
        let frame = NmcpFrame::new(NmcpFrameTypes::JSON_RPC_REQUEST, self.next_seq(), payload.into_bytes());
        self.send_frame(&frame).await
    }

    async fn send_frame(&self, frame: &NmcpFrame) -> anyhow::Result<()> {
        let mut guard = self.ws.lock().await;
        let writer = guard.as_mut()
            .ok_or_else(|| anyhow::anyhow!("Remote not connected"))?;
        let bytes = frame.to_bytes();
        writer.send(Message::Binary(bytes)).await?;
        Ok(())
    }

    async fn receive_loop(
        &self,
        reader: &mut futures_util::stream::SplitStream<WsStream>,
        cancel: watch::Receiver<bool>,
    ) -> anyhow::Result<()> {
        loop {
            let data = tokio::select! {
                msg = reader.next() => {
                    match msg {
                        Some(Ok(Message::Binary(data))) => data,
                        Some(Ok(Message::Close(_))) => return Ok(()),
                        Some(Err(e)) => return Err(anyhow::anyhow!("WebSocket error: {}", e)),
                        None => return Ok(()),
                        _ => continue,
                    }
                }
                _ = cancel_wait(cancel.clone()) => return Ok(()),
            };

            if data.len() < HEADER_SIZE { continue; }
            if let Some((frame_type, _payload_len, seq_id)) = NmcpFrame::try_read_header(&data) {
                let payload = if data.len() > HEADER_SIZE {
                    data[HEADER_SIZE..].to_vec()
                } else {
                    vec![]
                };
                self.handle_frame(frame_type, seq_id, payload).await;
            }
        }
    }

    async fn handle_frame(&self, frame_type: u32, _seq_id: u32, payload: Vec<u8>) {
        match frame_type {
            NmcpFrameTypes::SCREEN_CAPTURE => {
                if let Some(handler) = self.on_screen_frame.lock().await.as_ref() {
                    handler(payload).await;
                }
            }
            NmcpFrameTypes::TOOL_CALL => {
                // Parse simple NDA: tool=name\nrequest_id=N\n...
                let (tool_name, request_id, raw_data) = parse_nda_payload(&payload);
                if let Some(handler) = self.on_tool_call.lock().await.as_ref() {
                    let result = handler(tool_name.clone(), raw_data, request_id).await;
                    // Send tool result
                    let result_payload = format!(
                        "tool_result.id={}\ntool_result.tool={}",
                        request_id, tool_name
                    );
                    let mut combined = result_payload.into_bytes();
                    combined.push(0);
                    combined.extend_from_slice(&result);
                    let frame = NmcpFrame::new(NmcpFrameTypes::TOOL_RESULT, request_id, combined);
                    if let Err(e) = self.send_frame(&frame).await {
                        tracing::warn!("Failed to send tool result: {}", e);
                    }
                }
            }
            NmcpFrameTypes::HEARTBEAT => {
                tracing::debug!("Remote heartbeat received");
            }
            _ => {
                tracing::debug!("Remote: unhandled frame type {}", frame_type);
            }
        }
    }
}

/// Parse simple NDA payload (key=value pairs, optionally followed by \0 + raw data).
fn parse_nda_payload(data: &[u8]) -> (String, u32, Vec<u8>) {
    let text_part = if let Some(pos) = data.iter().position(|&b| b == 0) {
        &data[..pos]
    } else {
        data
    };
    let raw_data = if let Some(pos) = data.iter().position(|&b| b == 0) {
        data[pos + 1..].to_vec()
    } else {
        vec![]
    };

    let text = String::from_utf8_lossy(text_part);
    let mut tool_name = String::from("unknown");
    let mut request_id = 0u32;

    for line in text.lines() {
        if let Some((key, val)) = line.split_once('=') {
            match key.trim() {
                "tool" => tool_name = val.trim().to_string(),
                "request_id" => { request_id = val.trim().parse().unwrap_or(0); }
                _ => {}
            }
        }
    }
    (tool_name, request_id, raw_data)
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
