//! Autonomy engine — event-driven behavior execution.

use crate::behavior::BehaviorRule;
use crate::event_bus::{DroneEvent, EventBus};
use drone_core::config::AutonomyConfig;
use std::sync::Arc;
use tokio::sync::RwLock;

/// Callback when an action is executed: (rule_name, event_type, event_data).
pub type ActionCallback = Arc<dyn Fn(String, String, serde_json::Value) -> std::pin::Pin<Box<dyn std::future::Future<Output = ()> + Send>> + Send + Sync>;

/// The autonomy engine processes behavior rules in response to events.
pub struct AutonomyEngine {
    config: AutonomyConfig,
    rules: Arc<RwLock<Vec<BehaviorRule>>>,
    on_action: Arc<RwLock<Option<ActionCallback>>>,
}

impl AutonomyEngine {
    pub fn new(config: AutonomyConfig) -> Self {
        Self {
            config,
            rules: Arc::new(RwLock::new(Vec::new())),
            on_action: Arc::new(RwLock::new(None)),
        }
    }

    /// Set a callback invoked whenever a rule action fires.
    pub async fn set_on_action_executed<F>(&self, callback: F)
    where
        F: Fn(String, String, serde_json::Value) -> std::pin::Pin<Box<dyn std::future::Future<Output = ()> + Send>> + Send + Sync + 'static,
    {
        *self.on_action.write().await = Some(Arc::new(callback));
    }

    /// Load rules from the configured path.
    pub async fn load_rules(&self) {
        let path = &self.config.rules_path;
        match std::fs::read_to_string(path) {
            Ok(content) => {
                match serde_json::from_str::<Vec<BehaviorRule>>(&content) {
                    Ok(rules) => {
                        tracing::info!("Loaded {} behavior rules from {}", rules.len(), path);
                        *self.rules.write().await = rules;
                    }
                    Err(e) => {
                        tracing::error!("Failed to parse rules from {}: {}", path, e);
                        self.add_default_rule().await;
                    }
                }
            }
            Err(_) => {
                tracing::info!("No rules file at {}, using defaults", path);
                self.add_default_rule().await;
            }
        }
    }

    async fn add_default_rule(&self) {
        let default = BehaviorRule {
            name: "LogAllEvents".into(),
            trigger: "*".into(),
            action: "log".into(),
            enabled: true,
            action_params: None,
        };
        self.rules.write().await.push(default);
    }

    /// Start the autonomy engine with the given event bus.
    pub async fn start(&self, event_bus: &EventBus) {
        if !self.config.enabled {
            tracing::info!("Autonomy engine disabled");
            return;
        }

        self.load_rules().await;

        let rules = self.rules.clone();
        let on_action = self.on_action.clone();
        event_bus.subscribe(move |event: DroneEvent| {
            let rules = rules.clone();
            let on_action = on_action.clone();
            async move {
                let rules = rules.read().await;
                for rule in rules.iter().filter(|r| r.matches(&event)) {
                    tracing::info!("[Autonomy] Rule '{}' triggered by event '{}'", rule.name, event.event_type);
                    if let Some(callback) = on_action.read().await.as_ref() {
                        callback(rule.name.clone(), event.event_type.clone(), event.data.clone()).await;
                    }
                }
            }
        }).await;

        tracing::info!("Autonomy engine started");
    }
}
