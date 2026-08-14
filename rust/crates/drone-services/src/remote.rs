//! Remote connector — remote desktop/screen sharing.

use drone_core::config::RemoteConfig;
use serde_json::Value as JsonValue;

#[allow(dead_code)]
pub struct RemoteConnector {
    config: RemoteConfig,
}

impl RemoteConnector {
    pub fn new(config: RemoteConfig) -> Self {
        Self { config }
    }

    pub fn is_connected(&self) -> bool {
        false // TODO
    }

    pub async fn connect(&self) -> anyhow::Result<()> {
        tracing::info!("Remote connector: connect not yet implemented");
        Ok(())
    }

    pub async fn request_screen(&self, _quality: u32, _max_width: u32) -> anyhow::Result<()> {
        Ok(())
    }

    pub async fn send_input(&self, _input_type: &str, _data: &JsonValue) -> anyhow::Result<()> {
        Ok(())
    }
}
