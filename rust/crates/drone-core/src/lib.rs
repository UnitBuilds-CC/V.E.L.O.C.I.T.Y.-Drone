//! Drone Core — foundational types for the Velocity Drone agent.
//!
//! Provides:
//! - NMCP binary protocol frames (16-byte header, big-endian)
//! - Configuration (TOML-based, env var overrides)
//! - Custody chain (hash-chained audit trail with Merkle batch verification)
//! - Merkle tree (SHA-256, O(log N) proof verification)
//! - Audit logger (JSON-lines, daily rotation)

pub mod protocol;
pub mod config;
pub mod custody;
pub mod audit;

/// Re-export key types at crate root.
pub use protocol::{NmcpFrame, NmcpFrameTypes};
pub use config::DroneConfig;
pub use custody::{CustodyRecord, CustodyChain, MerkleTree};
pub use audit::AuditLogger;
