//! Messenger connector — WebSocket-based messaging.

use drone_core::config::MessengerConfig;
use std::sync::Arc;

#[allow(dead_code)]
pub struct MessengerConnector {
    config: MessengerConfig,
    drone_id: String,
    connected: Arc<std::sync::atomic::AtomicBool>,
}

impl MessengerConnector {
    pub fn new(config: MessengerConfig, drone_id: String) -> Self {
        Self {
            config,
            drone_id,
            connected: Arc::new(std::sync::atomic::AtomicBool::new(false)),
        }
    }

    pub fn is_connected(&self) -> bool {
        self.connected.load(std::sync::atomic::Ordering::Relaxed)
    }

    pub async fn connect(&self) -> anyhow::Result<()> {
        // TODO: Implement WebSocket connection to Messenger server
        tracing::info!("Messenger connector: connect not yet implemented");
        Ok(())
    }

    pub async fn send_message(&self, to: &str, content: &str) -> anyhow::Result<()> {
        // TODO: Send message via WebSocket
        tracing::info!("Messenger: send to {} content={}", to, content);
        Ok(())
    }
}
