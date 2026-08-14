//! Embedded file server for Share functionality.

use std::path::PathBuf;

pub struct FileServer {
    storage_path: PathBuf,
    listen_url: String,
}

impl FileServer {
    pub fn new(storage_path: PathBuf, listen_url: String) -> Self {
        Self { storage_path, listen_url }
    }

    pub async fn start(&self, _cancel: tokio::sync::watch::Receiver<bool>) -> anyhow::Result<()> {
        tracing::info!("File server at {} (storage: {})", self.listen_url, self.storage_path.display());
        // TODO: Implement HTTP file server
        Ok(())
    }
}
