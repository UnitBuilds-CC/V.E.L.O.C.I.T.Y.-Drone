//! Event bus — pub/sub for drone events.

use serde::{Deserialize, Serialize};
use serde_json::Value as JsonValue;
use std::sync::Arc;
use tokio::sync::RwLock;

/// Drone event types.
pub struct DroneEventTypes;

impl DroneEventTypes {
    pub const MESSAGE_RECEIVED: &'static str = "message_received";
    pub const FILE_CHANGED: &'static str = "file_changed";
    pub const SYSTEM_METRICS: &'static str = "system_metrics";
    pub const SYSTEM_ALERT: &'static str = "system_alert";
    pub const PROCESS_STARTED: &'static str = "process_started";
    pub const PROCESS_STOPPED: &'static str = "process_stopped";
    pub const SCHEDULED_TASK: &'static str = "scheduled_task";
}

/// A drone event.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DroneEvent {
    pub event_type: String,
    pub data: JsonValue,
}

/// Event handler function type.
type EventHandler = Box<dyn Fn(DroneEvent) -> std::pin::Pin<Box<dyn std::future::Future<Output = ()> + Send>> + Send + Sync>;

/// Async event bus for pub/sub.
pub struct EventBus {
    handlers: Arc<RwLock<Vec<EventHandler>>>,
}

impl EventBus {
    pub fn new() -> Self {
        Self { handlers: Arc::new(RwLock::new(Vec::new())) }
    }

    /// Subscribe to all events.
    pub async fn subscribe<F, Fut>(&self, handler: F)
    where
        F: Fn(DroneEvent) -> Fut + Send + Sync + 'static,
        Fut: std::future::Future<Output = ()> + Send + 'static,
    {
        self.handlers.write().await.push(Box::new(move |evt| Box::pin(handler(evt))));
    }

    /// Publish an event to all subscribers.
    pub async fn publish(&self, event: DroneEvent) {
        let handlers = self.handlers.read().await;
        for handler in handlers.iter() {
            handler(event.clone()).await;
        }
    }
}
