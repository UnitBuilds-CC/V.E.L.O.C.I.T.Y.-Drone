//! Tool registry — dynamic tool registration with JSON schema.

use serde::{Deserialize, Serialize};
use serde_json::Value as JsonValue;
use std::collections::HashMap;
use tokio::sync::RwLock;

/// Tool metadata.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ToolInfo {
    pub name: String,
    pub description: String,
    pub input_schema: JsonValue,
}

/// Dynamic tool registry.
pub struct ToolRegistry {
    tools: RwLock<HashMap<String, ToolEntry>>,
}

#[allow(dead_code)]
struct ToolEntry {
    info: ToolInfo,
    handler: Box<dyn Fn(JsonValue) -> std::pin::Pin<Box<dyn std::future::Future<Output = JsonValue> + Send>> + Send + Sync>,
}

impl ToolRegistry {
    pub fn new() -> Self {
        Self { tools: RwLock::new(HashMap::new()) }
    }

    pub async fn list_tools(&self) -> Vec<ToolInfo> {
        self.tools.read().await.values().map(|e| e.info.clone()).collect()
    }
}
