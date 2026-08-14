//! Audit logger — thread-safe JSON-lines logger with daily rotation.

use chrono::Utc;
use serde::{Deserialize, Serialize};
use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

/// Thread-safe audit logger for security-sensitive operations.
/// Writes JSON-lines to a file with automatic daily rotation.
pub struct AuditLogger {
    base_path: String,
    max_file_size_bytes: u64,
    state: Mutex<AuditState>,
}

struct AuditState {
    writer: Option<std::fs::File>,
    current_file_path: PathBuf,
    current_date: chrono::NaiveDate,
}

#[derive(Debug, Serialize, Deserialize)]
struct AuditRecord {
    timestamp: DateTime<Utc>,
    event: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    client_address: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    tool_name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    duration_ms: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    success: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    details: Option<String>,
}

use chrono::DateTime;

impl AuditLogger {
    /// Create a new audit logger. Pass empty/None path to disable auditing.
    pub fn new(base_path: Option<&str>, max_file_size_mb: u64) -> Self {
        let path = base_path.unwrap_or("").to_string();
        let logger = Self {
            base_path: path.clone(),
            max_file_size_bytes: max_file_size_mb * 1024 * 1024,
            state: Mutex::new(AuditState {
                writer: None,
                current_file_path: PathBuf::new(),
                current_date: Utc::now().date_naive(),
            }),
        };

        if !path.is_empty() {
            let mut state = logger.state.lock().unwrap();
            logger.ensure_writer(&mut state);
        }

        logger
    }

    /// Whether audit logging is enabled.
    pub fn is_enabled(&self) -> bool {
        !self.base_path.is_empty()
    }

    /// Log a tool call with full context.
    pub fn log_tool_call(
        &self,
        client_address: &str,
        tool_name: &str,
        duration_ms: i64,
        is_error: bool,
        error_message: Option<&str>,
    ) {
        if !self.is_enabled() { return; }
        self.write_record(AuditRecord {
            timestamp: Utc::now(),
            event: "tool_call".into(),
            client_address: Some(client_address.into()),
            tool_name: Some(tool_name.into()),
            duration_ms: Some(duration_ms),
            success: Some(!is_error),
            error: error_message.map(Into::into),
            details: None,
        });
    }

    /// Log a connection event.
    pub fn log_connection(&self, client_address: &str, event_type: &str, details: Option<&str>) {
        if !self.is_enabled() { return; }
        self.write_record(AuditRecord {
            timestamp: Utc::now(),
            event: event_type.into(),
            client_address: Some(client_address.into()),
            tool_name: None,
            duration_ms: None,
            success: None,
            error: None,
            details: details.map(Into::into),
        });
    }

    /// Log a security event.
    pub fn log_security(&self, client_address: &str, event_type: &str, details: Option<&str>) {
        if !self.is_enabled() { return; }
        self.write_record(AuditRecord {
            timestamp: Utc::now(),
            event: format!("security_{}", event_type),
            client_address: Some(client_address.into()),
            tool_name: None,
            duration_ms: None,
            success: None,
            error: None,
            details: details.map(Into::into),
        });
    }

    fn write_record(&self, record: AuditRecord) {
        let mut state = match self.state.lock() {
            Ok(s) => s,
            Err(_) => return, // Poisoned lock — skip
        };

        self.ensure_writer(&mut state);

        if let Some(ref mut writer) = state.writer {
            if let Ok(json) = serde_json::to_string(&record) {
                let _ = writeln!(writer, "{}", json);
                let _ = writer.flush();
            }
        }
    }

    fn ensure_writer(&self, state: &mut AuditState) {
        if self.base_path.is_empty() { return; }

        let today = Utc::now().date_naive();
        let file_path = self.get_file_path(today);

        // Daily rotation: if date changed, close old writer
        if state.writer.is_some() && today != state.current_date {
            state.writer = None;
        }

        if state.writer.is_none() {
            if let Some(dir) = file_path.parent() {
                let _ = fs::create_dir_all(dir);
            }

            match OpenOptions::new().create(true).append(true).open(&file_path) {
                Ok(f) => {
                    state.current_file_path = file_path.clone();
                    state.current_date = today;
                    state.writer = Some(f);
                }
                Err(_) => return,
            }
        }

        // Size-based rotation
        if let Ok(metadata) = fs::metadata(&state.current_file_path) {
            if metadata.len() > self.max_file_size_bytes {
                state.writer = None;
                let rotated = format!("{}.{}", state.current_file_path.display(),
                    Utc::now().format("%H%M%S"));
                let _ = fs::rename(&state.current_file_path, &rotated);

                match OpenOptions::new().create(true).append(true).open(&state.current_file_path) {
                    Ok(f) => state.writer = Some(f),
                    Err(_) => {}
                }

                self.cleanup_old_rotations(state);
            }
        }
    }

    fn get_file_path(&self, date: chrono::NaiveDate) -> PathBuf {
        let base = Path::new(&self.base_path);
        let ext = base.extension().and_then(|e| e.to_str()).unwrap_or("");
        let stem = base.file_stem().and_then(|s| s.to_str()).unwrap_or("audit");
        let dir = base.parent().unwrap_or(Path::new("."));

        if ext.is_empty() {
            dir.join(format!("{}-{}", stem, date.format("%Y-%m-%d")))
        } else {
            dir.join(format!("{}-{}.{}", stem, date.format("%Y-%m-%d"), ext))
        }
    }

    fn cleanup_old_rotations(&self, state: &AuditState) {
        if let Some(dir) = state.current_file_path.parent() {
            if let Some(base_name) = state.current_file_path.file_name().and_then(|n| n.to_str()) {
                if let Ok(entries) = fs::read_dir(dir) {
                    let mut old_files: Vec<PathBuf> = entries
                        .filter_map(|e| e.ok())
                        .map(|e| e.path())
                        .filter(|p| {
                            p.file_name().and_then(|n| n.to_str())
                                .map(|n| n.starts_with(base_name) && *p != state.current_file_path)
                                .unwrap_or(false)
                        })
                        .collect();

                    old_files.sort_by(|a, b| b.cmp(a)); // newest first

                    for old in old_files.into_iter().skip(5) {
                        let _ = fs::remove_file(old);
                    }
                }
            }
        }
    }
}
