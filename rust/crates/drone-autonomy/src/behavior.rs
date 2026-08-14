//! Behavior rules — configurable event-driven actions.

use serde::{Deserialize, Serialize};
use serde_json::Value as JsonValue;
use crate::event_bus::DroneEvent;

/// A behavior rule that matches events and triggers actions.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct BehaviorRule {
    pub name: String,
    pub trigger: String,
    pub action: String,
    #[serde(default)]
    pub enabled: bool,
    #[serde(default)]
    pub action_params: Option<JsonValue>,
}

impl BehaviorRule {
    /// Check if this rule matches the given event.
    pub fn matches(&self, event: &DroneEvent) -> bool {
        if !self.enabled { return false; }
        if self.trigger == "*" { return true; }
        self.trigger == event.event_type
    }
}
