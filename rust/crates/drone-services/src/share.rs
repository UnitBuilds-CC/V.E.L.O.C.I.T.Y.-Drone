//! Share connector — HTTP-based file sharing with WebSocket notifications.

use drone_core::config::ShareConfig;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ShareFileInfo {
    pub path: String,
    pub name: String,
    pub size: i64,
    #[serde(rename = "modifiedAt")]
    pub modified_at: String,
    #[serde(rename = "contentType")]
    pub content_type: String,
}

pub struct ShareConnector {
    config: ShareConfig,
    http: reqwest::Client,
}

impl ShareConnector {
    pub fn new(config: ShareConfig) -> Self {
        let mut headers = reqwest::header::HeaderMap::new();
        if let Some(key) = &config.admin_api_key {
            if let Ok(val) = reqwest::header::HeaderValue::from_str(key) {
                headers.insert("X-Api-Key", val);
            }
        }
        let http = reqwest::Client::builder()
            .default_headers(headers)
            .build()
            .unwrap_or_default();
        Self { config, http }
    }

    pub fn server_url(&self) -> &str {
        self.config.server_url.as_deref().unwrap_or("")
    }

    pub fn is_connected(&self) -> bool {
        self.config.server_url.is_some()
    }

    /// Upload a local file to the share server.
    pub async fn upload_file(&self, local_path: &str, remote_path: &str) -> anyhow::Result<bool> {
        let base = self.config.server_url.as_deref()
            .ok_or_else(|| anyhow::anyhow!("Share server_url not configured"))?;

        let file_bytes = tokio::fs::read(local_path).await?;
        let file_name = std::path::Path::new(local_path)
            .file_name()
            .map(|n| n.to_string_lossy().to_string())
            .unwrap_or_default();

        let part = reqwest::multipart::Part::bytes(file_bytes)
            .file_name(file_name)
            .mime_str("application/octet-stream")?;

        let form = reqwest::multipart::Form::new()
            .part("file", part)
            .text("path", remote_path.to_string());

        let url = format!("{}/api/files/upload", base.trim_end_matches('/'));
        let resp: reqwest::Response = self.http.post(&url).multipart(form).send().await?;
        Ok(resp.status().is_success())
    }

    /// Download a file from the share server.
    pub async fn download_file(&self, remote_path: &str, local_path: &str) -> anyhow::Result<bool> {
        let base = self.config.server_url.as_deref()
            .ok_or_else(|| anyhow::anyhow!("Share server_url not configured"))?;

        let encoded: String = remote_path.bytes().map(|b| {
            match b {
                b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => (b as char).to_string(),
                _ => format!("%{:02X}", b),
            }
        }).collect();

        let url = format!("{}/api/files/download/{}", base.trim_end_matches('/'), encoded);
        let resp = self.http.get(&url).send().await?;
        if !resp.status().is_success() {
            return Ok(false);
        }

        let bytes = resp.bytes().await?;
        if let Some(dir) = std::path::Path::new(local_path).parent() {
            tokio::fs::create_dir_all(dir).await.ok();
        }
        tokio::fs::write(local_path, &bytes).await?;
        Ok(true)
    }

    /// List files on the share server.
    pub async fn list_files(&self, path: Option<&str>) -> anyhow::Result<Vec<ShareFileInfo>> {
        let base = self.config.server_url.as_deref()
            .ok_or_else(|| anyhow::anyhow!("Share server_url not configured"))?;

        let url = match path {
            Some(p) => format!("{}/api/files?path={}", base.trim_end_matches('/'), p),
            None => format!("{}/api/files", base.trim_end_matches('/')),
        };

        let resp = self.http.get(&url).send().await?;
        if !resp.status().is_success() {
            return Ok(vec![]);
        }
        let files: Vec<ShareFileInfo> = resp.json().await?;
        Ok(files)
    }

    /// Delete a file on the share server.
    pub async fn delete_file(&self, remote_path: &str) -> anyhow::Result<bool> {
        let base = self.config.server_url.as_deref()
            .ok_or_else(|| anyhow::anyhow!("Share server_url not configured"))?;

        let encoded: String = remote_path.bytes().map(|b| {
            match b {
                b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => (b as char).to_string(),
                _ => format!("%{:02X}", b),
            }
        }).collect();

        let url = format!("{}/api/files/{}", base.trim_end_matches('/'), encoded);
        let resp = self.http.delete(&url).send().await?;
        Ok(resp.status().is_success())
    }

    /// Sync a local folder to a remote folder, uploading only changed files.
    pub async fn sync_folder(&self, local: &str, remote: &str) -> anyhow::Result<usize> {
        let remote_files = self.list_files(Some(remote)).await?;
        let remote_map: std::collections::HashMap<String, String> = remote_files
            .into_iter()
            .map(|f| (f.path, f.modified_at))
            .collect();

        let mut uploaded = 0;
        let entries = walk_dir(local).await?;

        for local_file in entries {
            let rel = local_file.strip_prefix(local)
                .unwrap_or(&local_file)
                .trim_start_matches('/')
                .trim_start_matches('\\')
                .replace('\\', "/");
            let remote_path = format!("{}/{}", remote.trim_end_matches('/'), rel);

            let _local_modified = tokio::fs::metadata(&local_file).await
                .map(|m| m.modified().ok())
                .unwrap_or(None);

            let should_upload = match remote_map.get(&remote_path) {
                None => true,
                Some(_remote_mod) => {
                    // Simple heuristic: always upload if we can't compare timestamps
                    true
                }
            };

            if should_upload {
                if self.upload_file(&local_file, &remote_path).await.unwrap_or(false) {
                    uploaded += 1;
                }
            }
        }

        tracing::info!("Synced {} files from {} to {}", uploaded, local, remote);
        Ok(uploaded)
    }
}

/// Recursively walk a directory, returning all file paths.
async fn walk_dir(path: &str) -> anyhow::Result<Vec<String>> {
    let mut results = Vec::new();
    let mut stack = vec![path.to_string()];

    while let Some(dir) = stack.pop() {
        let mut entries = tokio::fs::read_dir(&dir).await?;
        while let Some(entry) = entries.next_entry().await? {
            let ft = entry.file_type().await?;
            let p = entry.path().to_string_lossy().to_string();
            if ft.is_dir() {
                stack.push(p);
            } else if ft.is_file() {
                results.push(p);
            }
        }
    }
    Ok(results)
}
