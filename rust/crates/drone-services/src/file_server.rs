//! Embedded HTTP file server for Share functionality.
//! Implements upload, download, list, and delete operations.

use hyper::body::Incoming;
use hyper::{Request, Response, StatusCode};
use hyper_util::rt::TokioIo;
use http_body_util::Full;
use std::net::SocketAddr;
use std::path::{Path, PathBuf};
use tokio::net::TcpListener;
use tokio::sync::watch;

pub struct FileServer {
    storage_path: PathBuf,
    listen_addr: SocketAddr,
    api_key: Option<String>,
}

impl FileServer {
    pub fn new(storage_path: PathBuf, listen_addr: SocketAddr, api_key: Option<String>) -> Self {
        Self { storage_path, listen_addr, api_key }
    }

    /// Create from string config values (listen_url like "127.0.0.1:5003").
    pub fn from_config(storage_path: PathBuf, listen_url: &str, api_key: Option<String>) -> Self {
        let addr: SocketAddr = listen_url.parse().unwrap_or_else(|_| {
            SocketAddr::from(([127, 0, 0, 1], 5003))
        });
        Self { storage_path, listen_addr: addr, api_key }
    }

    pub async fn start(&self, mut cancel: watch::Receiver<bool>) -> anyhow::Result<()> {
        tokio::fs::create_dir_all(&self.storage_path).await?;

        let listener = TcpListener::bind(self.listen_addr).await?;
        tracing::info!(
            "File server listening on {}, storage: {}",
            self.listen_addr, self.storage_path.display()
        );

        let storage = self.storage_path.clone();
        let api_key = self.api_key.clone();

        loop {
            tokio::select! {
                accept = listener.accept() => {
                    match accept {
                        Ok((stream, _peer)) => {
                            let storage = storage.clone();
                            let api_key = api_key.clone();
                            tokio::spawn(async move {
                                let io = TokioIo::new(stream);
                                let service = hyper::service::service_fn(move |req| {
                                    let storage = storage.clone();
                                    let api_key = api_key.clone();
                                    async move {
                                        handle_request(req, &storage, api_key.as_deref()).await
                                    }
                                });
                                if let Err(e) = hyper_util::server::conn::auto::Builder::new(
                                    hyper_util::rt::TokioExecutor::new()
                                ).serve_connection(io, service).await {
                                    tracing::debug!("File server connection error: {}", e);
                                }
                            });
                        }
                        Err(e) => {
                            tracing::warn!("File server accept error: {}", e);
                        }
                    }
                }
                _ = cancel_wait(&mut cancel) => {
                    tracing::info!("File server shutting down");
                    break;
                }
            }
        }
        Ok(())
    }
}

type BoxBody = Full<hyper::body::Bytes>;

fn full_body(data: impl Into<hyper::body::Bytes>) -> BoxBody {
    Full::new(data.into())
}

fn json_response(status: StatusCode, body: &str) -> Response<BoxBody> {
    Response::builder()
        .status(status)
        .header("Content-Type", "application/json")
        .body(full_body(body.to_string()))
        .unwrap()
}

async fn handle_request(
    req: Request<Incoming>,
    storage: &Path,
    api_key: Option<&str>,
) -> Result<Response<BoxBody>, std::convert::Infallible> {
    // Check API key
    if let Some(key) = api_key {
        let provided = req.headers()
            .get("X-Api-Key")
            .and_then(|v| v.to_str().ok())
            .unwrap_or("");
        if provided != key {
            return Ok(json_response(StatusCode::UNAUTHORIZED, r#"{"error":"Unauthorized"}"#));
        }
    }

    let method = req.method().clone();
    let path = req.uri().path().to_string();

    let result = if method == hyper::Method::POST && path == "/api/files/upload" {
        handle_upload(req, storage).await
    } else if method == hyper::Method::GET && path.starts_with("/api/files/download/") {
        let file_path = percent_decode(&path["/api/files/download/".len()..]);
        handle_download(storage, &file_path).await
    } else if method == hyper::Method::GET && path == "/api/files" {
        let query_path = req.uri().query()
            .and_then(|q| q.split('&').find_map(|p| {
                let (k, v) = p.split_once('=')?;
                if k == "path" { Some(v.to_string()) } else { None }
            }));
        handle_list(storage, query_path.as_deref()).await
    } else if method == hyper::Method::DELETE && path.starts_with("/api/files/") {
        let file_path = percent_decode(&path["/api/files/".len()..]);
        handle_delete(storage, &file_path).await
    } else {
        Ok(json_response(StatusCode::NOT_FOUND, r#"{"error":"Not found"}"#))
    };

    Ok(result.unwrap_or_else(|e| {
        json_response(StatusCode::INTERNAL_SERVER_ERROR, &format!(r#"{{"error":"{}"}}"#, e))
    }))
}

async fn handle_upload(req: Request<Incoming>, storage: &Path) -> anyhow::Result<Response<BoxBody>> {
    use http_body_util::BodyExt;

    // Get content-type before consuming the body
    let content_type = req.headers()
        .get("Content-Type")
        .and_then(|v| v.to_str().ok())
        .unwrap_or("")
        .to_string();

    let body = req.into_body().collect().await
        .map_err(|e| anyhow::anyhow!("Failed to read body: {}", e))?
        .to_bytes();

    let boundary = content_type.split("boundary=")
        .nth(1)
        .map(|b| b.trim_matches('"').to_string())
        .ok_or_else(|| anyhow::anyhow!("No boundary in multipart"))?;

    let body_str = String::from_utf8_lossy(&body);
    let mut remote_path = None;
    let mut file_data = None;

    for part in body_str.split(&format!("--{}", boundary)) {
        if part.contains("name=\"path\"") {
            if let Some(data) = part.split("\r\n\r\n").nth(1) {
                remote_path = Some(data.trim().trim_end_matches('-').trim().to_string());
            }
        } else if part.contains("name=\"file\"") {
            if let Some(header_end) = part.find("\r\n\r\n") {
                let data_start = header_end + 4;
                if let Some(data_end) = part.rfind("\r\n--") {
                    if data_end > data_start {
                        file_data = Some(part[data_start..data_end].as_bytes().to_vec());
                    }
                }
            }
        }
    }

    let remote_path = remote_path.ok_or_else(|| anyhow::anyhow!("Missing path in upload"))?;
    let file_data = file_data.ok_or_else(|| anyhow::anyhow!("Missing file in upload"))?;

    let full_path = storage.join(remote_path.replace('/', std::path::MAIN_SEPARATOR_STR));
    if let Some(dir) = full_path.parent() {
        tokio::fs::create_dir_all(dir).await?;
    }
    tokio::fs::write(&full_path, &file_data).await?;

    tracing::info!("File uploaded: {} ({} bytes)", remote_path, file_data.len());
    Ok(json_response(StatusCode::OK, &format!(
        r#"{{"success":true,"path":"{}","size":{}}}"#,
        remote_path, file_data.len()
    )))
}

async fn handle_download(storage: &Path, file_path: &str) -> anyhow::Result<Response<BoxBody>> {
    let full_path = storage.join(file_path.replace('/', std::path::MAIN_SEPARATOR_STR));
    if !full_path.exists() {
        return Ok(json_response(StatusCode::NOT_FOUND, r#"{"error":"File not found"}"#));
    }

    let data = tokio::fs::read(&full_path).await?;
    let file_name = full_path.file_name()
        .map(|n| n.to_string_lossy().to_string())
        .unwrap_or_default();

    let resp = Response::builder()
        .status(StatusCode::OK)
        .header("Content-Type", "application/octet-stream")
        .header("Content-Length", data.len())
        .header("Content-Disposition", format!(r#"attachment; filename="{}""#, file_name))
        .body(full_body(data))
        .unwrap();

    tracing::info!("File downloaded: {}", file_path);
    Ok(resp)
}

async fn handle_list(storage: &Path, query_path: Option<&str>) -> anyhow::Result<Response<BoxBody>> {
    let search_path = match query_path {
        Some(p) if !p.is_empty() => storage.join(p.replace('/', std::path::MAIN_SEPARATOR_STR)),
        _ => storage.to_path_buf(),
    };

    if !search_path.exists() {
        return Ok(json_response(StatusCode::NOT_FOUND, r#"{"error":"Directory not found"}"#));
    }

    let mut files = Vec::new();
    list_files_recursive(&search_path, storage, &mut files).await?;

    let json = serde_json::to_string(&files)?;
    Ok(json_response(StatusCode::OK, &json))
}

async fn list_files_recursive(
    dir: &Path,
    base: &Path,
    files: &mut Vec<serde_json::Value>,
) -> anyhow::Result<()> {
    let mut entries = tokio::fs::read_dir(dir).await?;
    while let Some(entry) = entries.next_entry().await? {
        let ft = entry.file_type().await?;
        if ft.is_dir() {
            Box::pin(list_files_recursive(&entry.path(), base, files)).await?;
        } else if ft.is_file() {
            let full = entry.path();
            let rel = full.strip_prefix(base)
                .unwrap_or(&full)
                .to_string_lossy()
                .replace('\\', "/");
            let meta = entry.metadata().await?;
            let modified = meta.modified()
                .ok()
                .map(|t| {
                    let dt: chrono::DateTime<chrono::Utc> = t.into();
                    dt.to_rfc3339_opts(chrono::SecondsFormat::Millis, true)
                })
                .unwrap_or_default();
            files.push(serde_json::json!({
                "path": rel,
                "name": entry.file_name().to_string_lossy().to_string(),
                "size": meta.len(),
                "modifiedAt": modified,
                "contentType": "application/octet-stream",
            }));
        }
    }
    Ok(())
}

async fn handle_delete(storage: &Path, file_path: &str) -> anyhow::Result<Response<BoxBody>> {
    let full_path = storage.join(file_path.replace('/', std::path::MAIN_SEPARATOR_STR));
    if !full_path.exists() {
        return Ok(json_response(StatusCode::NOT_FOUND, r#"{"error":"File not found"}"#));
    }
    tokio::fs::remove_file(&full_path).await?;
    tracing::info!("File deleted: {}", file_path);
    Ok(json_response(StatusCode::OK, r#"{"success":true}"#))
}

/// Simple percent-decoding for URL paths.
fn percent_decode(s: &str) -> String {
    let mut result = Vec::new();
    let bytes = s.as_bytes();
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'%' && i + 2 < bytes.len() {
            if let Ok(val) = u8::from_str_radix(
                &String::from_utf8_lossy(&bytes[i+1..i+3]), 16
            ) {
                result.push(val);
                i += 3;
                continue;
            }
        } else if bytes[i] == b'+' {
            result.push(b' ');
            i += 1;
            continue;
        }
        result.push(bytes[i]);
        i += 1;
    }
    String::from_utf8_lossy(&result).to_string()
}

async fn cancel_wait(rx: &mut watch::Receiver<bool>) {
    while !*rx.borrow() {
        if rx.changed().await.is_err() { break; }
    }
}
