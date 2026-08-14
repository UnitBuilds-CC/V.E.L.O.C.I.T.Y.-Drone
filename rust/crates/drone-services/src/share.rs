//! Share connector — file sharing service.

use drone_core::config::ShareConfig;

#[allow(dead_code)]
pub struct ShareConnector {
    config: ShareConfig,
}

impl ShareConnector {
    pub fn new(config: ShareConfig) -> Self {
        Self { config }
    }

    pub async fn upload_file(&self, local_path: &str, remote_path: &str) -> anyhow::Result<bool> {
        tracing::info!("Share: upload {} -> {}", local_path, remote_path);
        Ok(true)
    }

    pub async fn download_file(&self, remote_path: &str, local_path: &str) -> anyhow::Result<bool> {
        tracing::info!("Share: download {} -> {}", remote_path, local_path);
        Ok(true)
    }

    pub async fn list_files(&self, _path: Option<&str>) -> anyhow::Result<Vec<serde_json::Value>> {
        Ok(vec![])
    }

    pub async fn sync_folder(&self, local: &str, remote: &str) -> anyhow::Result<usize> {
        tracing::info!("Share: sync {} -> {}", local, remote);
        Ok(0)
    }

    pub async fn delete_file(&self, _path: &str) -> anyhow::Result<bool> {
        Ok(true)
    }
}
