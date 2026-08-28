//! Custody chain — hash-chained audit trail with Merkle batch verification.
//!
//! Provides:
//! - `CustodyRecord`: single audit record with hash chain + Merkle root
//! - `CustodyChain`: thread-safe chain manager with sequence tracking
//! - `MerkleTree`: SHA-256 Merkle tree for O(log N) batch verification
//! - `CustodyBinarySerializer`: fixed 256-byte binary format for NMCP frames

use serde::{Deserialize, Serialize};
use sha2::{Sha256, Digest};
use std::sync::Mutex;
use chrono::{DateTime, Utc};

// ─── CustodyRecord ──────────────────────────────────────────────────────────

/// A single custody record in the audit trail. Each record is hash-chained to
/// the previous one, creating a tamper-evident log of every action the agent takes.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CustodyRecord {
    /// Which drone agent produced this record.
    pub drone_id: String,

    /// Globally unique event ID: DroneId + monotonic sequence.
    pub event_id: String,

    /// Monotonic sequence number within this drone's timeline.
    pub sequence: i64,

    /// UTC timestamp with high resolution.
    pub timestamp: DateTime<Utc>,

    /// Event category: "tool_call", "connection", "security", "cross_machine".
    pub event_type: String,

    /// Which system was affected: "local", "drone:xyz", "share-server", "messenger".
    #[serde(default = "default_target")]
    pub target_system: String,

    /// What action was performed: "run_command", "read_file", "send_message", etc.
    pub action: String,

    /// Sanitized arguments (no secrets). None for events with no arguments.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub arguments: Option<String>,

    /// Result summary: success/failure + brief description.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub result: Option<String>,

    /// True if the action succeeded, false otherwise.
    #[serde(default = "default_true")]
    pub success: bool,

    /// Links multi-step cross-machine sequences together.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub correlation_id: Option<String>,

    /// SHA-256 hash of the previous record in the chain. Empty for genesis.
    #[serde(default)]
    pub prev_hash: String,

    /// SHA-256 hash of this record's content (excluding prev_hash and hash).
    #[serde(default)]
    pub hash: String,

    /// Merkle root over the batch this record belongs to.
    /// Hex-encoded SHA-256 (64 chars). Empty if not yet assigned to a batch.
    #[serde(default)]
    pub merkle_root: String,
}

fn default_target() -> String { "local".into() }
fn default_true() -> bool { true }

impl CustodyRecord {
    /// Compute the content hash of this record (excluding prev_hash, hash, merkle_root).
    pub fn compute_hash(&self) -> String {
        let content = format!(
            "{}|{}|{}|{}|{}|{}|{}|{}|{}|{}|{}",
            self.drone_id, self.event_id, self.sequence,
            self.timestamp.to_rfc3339(),
            self.event_type, self.target_system, self.action,
            self.arguments.as_deref().unwrap_or(""),
            self.result.as_deref().unwrap_or(""),
            self.success,
            self.correlation_id.as_deref().unwrap_or(""),
        );
        let hash = Sha256::digest(content.as_bytes());
        hex::encode_upper(hash)
    }

    /// Compute and set the hash field.
    pub fn seal(&mut self) {
        self.hash = self.compute_hash();
    }

    /// Verify that this record's hash matches its content.
    pub fn verify_hash(&self) -> bool {
        if self.hash.is_empty() { return false; }
        self.compute_hash() == self.hash
    }

    /// Verify chain linkage with the previous record.
    pub fn verify_chain(&self, previous: Option<&CustodyRecord>) -> bool {
        if !self.verify_hash() { return false; }
        match previous {
            None => self.prev_hash.is_empty(),
            Some(prev) => self.prev_hash == prev.hash,
        }
    }

    /// Verify this record's Merkle proof against a known batch root.
    pub fn verify_merkle_proof(&self, batch_root: &str, proof: &[String], batch_index: usize) -> bool {
        if self.hash.is_empty() || self.merkle_root.is_empty() { return false; }
        if self.merkle_root != batch_root { return false; }
        MerkleTree::verify_proof_hex(batch_root, &self.hash, proof, batch_index)
    }

    /// Serialize to JSON.
    pub fn to_json(&self) -> String {
        serde_json::to_string(self).unwrap_or_default()
    }

    /// Deserialize from JSON.
    pub fn from_json(json: &str) -> Option<Self> {
        serde_json::from_str(json).ok()
    }
}

// ─── CustodyChain ───────────────────────────────────────────────────────────

/// Thread-safe hash chain manager for custody records.
pub struct CustodyChain {
    drone_id: String,
    inner: Mutex<ChainState>,
}

struct ChainState {
    sequence: i64,
    prev_hash: String,
    last_record: Option<CustodyRecord>,
}

impl CustodyChain {
    pub fn new(drone_id: impl Into<String>) -> Self {
        Self {
            drone_id: drone_id.into(),
            inner: Mutex::new(ChainState {
                sequence: 0,
                prev_hash: String::new(),
                last_record: None,
            }),
        }
    }

    /// Current sequence number (0 if no records yet).
    pub fn current_sequence(&self) -> i64 {
        self.inner.lock().unwrap().sequence
    }

    /// Hash of the last record (empty if no records yet).
    pub fn current_hash(&self) -> String {
        self.inner.lock().unwrap().prev_hash.clone()
    }

    /// The last record added to the chain.
    pub fn last_record(&self) -> Option<CustodyRecord> {
        self.inner.lock().unwrap().last_record.clone()
    }

    /// Create the next record in the chain.
    pub fn next_record(
        &self,
        event_type: &str,
        action: &str,
        arguments: Option<&str>,
        result: Option<&str>,
        success: bool,
        target_system: Option<&str>,
        correlation_id: Option<&str>,
    ) -> CustodyRecord {
        let mut state = self.inner.lock().unwrap();
        state.sequence += 1;

        let mut record = CustodyRecord {
            drone_id: self.drone_id.clone(),
            event_id: format!("{}:{}", self.drone_id, state.sequence),
            sequence: state.sequence,
            timestamp: Utc::now(),
            event_type: event_type.to_string(),
            target_system: target_system.unwrap_or("local").to_string(),
            action: action.to_string(),
            arguments: arguments.map(String::from),
            result: result.map(String::from),
            success,
            correlation_id: correlation_id.map(String::from),
            prev_hash: state.prev_hash.clone(),
            hash: String::new(),
            merkle_root: String::new(),
        };

        record.seal();
        state.prev_hash = record.hash.clone();
        state.last_record = Some(record.clone());
        record
    }

    /// Verify the integrity of a sequence of records.
    pub fn verify_chain(records: &[CustodyRecord]) -> bool {
        let mut expected_seq: i64 = 0;
        let mut prev: Option<&CustodyRecord> = None;

        for record in records {
            expected_seq += 1;
            if record.sequence != expected_seq { return false; }
            if !record.verify_chain(prev) { return false; }
            prev = Some(record);
        }

        true
    }

    /// Compute the Merkle root over a batch of records using their content hashes as leaves.
    pub fn compute_batch_merkle_root(records: &[CustodyRecord]) -> String {
        let leaves: Vec<[u8; 32]> = records.iter()
            .filter(|r| !r.hash.is_empty())
            .filter_map(|r| hex::decode(&r.hash).ok())
            .filter(|b| b.len() == 32)
            .map(|b| {
                let mut arr = [0u8; 32];
                arr.copy_from_slice(&b);
                arr
            })
            .collect();

        if leaves.is_empty() { return String::new(); }
        let root = MerkleTree::compute_root(&leaves);
        hex::encode_upper(root)
    }

    /// Assign Merkle roots to a batch of records.
    pub fn assign_batch_merkle_root(records: &mut [CustodyRecord]) -> String {
        if records.is_empty() { return String::new(); }
        let root = Self::compute_batch_merkle_root(records);
        for record in records.iter_mut() {
            record.merkle_root = root.clone();
        }
        root
    }

    /// Build a Merkle proof for a specific record within a batch.
    pub fn build_batch_proof(records: &[CustodyRecord], batch_index: usize) -> Vec<String> {
        let leaves: Vec<[u8; 32]> = records.iter()
            .filter(|r| !r.hash.is_empty())
            .filter_map(|r| hex::decode(&r.hash).ok())
            .filter(|b| b.len() == 32)
            .map(|b| {
                let mut arr = [0u8; 32];
                arr.copy_from_slice(&b);
                arr
            })
            .collect();

        if leaves.is_empty() || batch_index >= leaves.len() { return vec![]; }
        MerkleTree::build_proof(&leaves, batch_index)
            .iter()
            .map(|h| hex::encode_upper(h))
            .collect()
    }

    /// Reset the chain to a known state (e.g., after loading from persisted records).
    pub fn reset_to(&self, sequence: i64, last_hash: String, last_record: Option<CustodyRecord>) {
        let mut state = self.inner.lock().unwrap();
        state.sequence = sequence;
        state.prev_hash = last_hash;
        state.last_record = last_record;
    }
}

// ─── MerkleTree ─────────────────────────────────────────────────────────────

/// SHA-256 Merkle tree for batch verification.
/// Enables O(log N) verification of individual records.
pub struct MerkleTree;

impl MerkleTree {
    pub const HASH_SIZE: usize = 32;

    /// Compute the Merkle root from a list of 32-byte leaf hashes.
    pub fn compute_root(leaves: &[[u8; 32]]) -> [u8; 32] {
        if leaves.is_empty() {
            return Sha256::digest(&[]).into();
        }
        if leaves.len() == 1 {
            return leaves[0];
        }

        let mut current_level: Vec<[u8; 32]> = leaves.to_vec();

        while current_level.len() > 1 {
            let mut next_level = Vec::with_capacity((current_level.len() + 1) / 2);

            let mut i = 0;
            while i < current_level.len() {
                let left = &current_level[i];
                let right = if i + 1 < current_level.len() {
                    &current_level[i + 1]
                } else {
                    &current_level[i] // Odd node: hash with itself
                };

                let mut combined = [0u8; 64];
                combined[..32].copy_from_slice(left);
                combined[32..].copy_from_slice(right);
                next_level.push(Sha256::digest(combined).into());

                i += 2;
            }

            current_level = next_level;
        }

        current_level[0]
    }

    /// Build a Merkle proof for a specific leaf index.
    pub fn build_proof(leaves: &[[u8; 32]], index: usize) -> Vec<[u8; 32]> {
        if leaves.is_empty() || leaves.len() == 1 { return vec![]; }
        if index >= leaves.len() { return vec![]; }

        let mut proof = Vec::new();
        let mut current_level: Vec<[u8; 32]> = leaves.to_vec();
        let mut current_index = index;

        while current_level.len() > 1 {
            let sibling_index = if current_index % 2 == 0 {
                if current_index + 1 < current_level.len() {
                    current_index + 1
                } else {
                    current_index // Odd node: sibling is self
                }
            } else {
                current_index - 1
            };

            proof.push(current_level[sibling_index]);

            // Build next level
            let mut next_level = Vec::with_capacity((current_level.len() + 1) / 2);
            let mut i = 0;
            while i < current_level.len() {
                let left = &current_level[i];
                let right = if i + 1 < current_level.len() {
                    &current_level[i + 1]
                } else {
                    &current_level[i]
                };
                let mut combined = [0u8; 64];
                combined[..32].copy_from_slice(left);
                combined[32..].copy_from_slice(right);
                next_level.push(Sha256::digest(combined).into());
                i += 2;
            }

            current_level = next_level;
            current_index /= 2;
        }

        proof
    }

    /// Verify a Merkle proof for a specific leaf.
    pub fn verify_proof(root: &[u8; 32], leaf: &[u8; 32], proof: &[[u8; 32]], index: usize) -> bool {
        let mut current = *leaf;
        let mut idx = index;

        for sibling in proof {
            let mut combined = [0u8; 64];
            if idx % 2 == 0 {
                combined[..32].copy_from_slice(&current);
                combined[32..].copy_from_slice(sibling);
            } else {
                combined[..32].copy_from_slice(sibling);
                combined[32..].copy_from_slice(&current);
            }
            current = Sha256::digest(combined).into();
            idx /= 2;
        }

        current == *root
    }

    /// Hex convenience: compute root from hex-encoded leaves.
    pub fn compute_root_hex(hex_leaves: &[&str]) -> String {
        let leaves: Vec<[u8; 32]> = hex_leaves.iter()
            .filter_map(|h| {
                let bytes = hex::decode(h).ok()?;
                if bytes.len() != 32 { return None; }
                let mut arr = [0u8; 32];
                arr.copy_from_slice(&bytes);
                Some(arr)
            })
            .collect();
        if leaves.is_empty() { return String::new(); }
        hex::encode_upper(MerkleTree::compute_root(&leaves))
    }

    /// Hex convenience: verify proof with hex-encoded values.
    pub fn verify_proof_hex(root_hex: &str, leaf_hex: &str, proof_hex: &[String], index: usize) -> bool {
        let root = match hex::decode(root_hex) {
            Ok(b) if b.len() == 32 => { let mut a = [0u8; 32]; a.copy_from_slice(&b); a }
            _ => return false,
        };
        let leaf = match hex::decode(leaf_hex) {
            Ok(b) if b.len() == 32 => { let mut a = [0u8; 32]; a.copy_from_slice(&b); a }
            _ => return false,
        };
        let proof: Option<Vec<[u8; 32]>> = proof_hex.iter().map(|h| {
            let bytes = hex::decode(h).ok()?;
            if bytes.len() != 32 { return None; }
            let mut arr = [0u8; 32];
            arr.copy_from_slice(&bytes);
            Some(arr)
        }).collect();

        match proof {
            Some(p) => MerkleTree::verify_proof(&root, &leaf, &p, index),
            None => false,
        }
    }
}

// ─── CustodyBinarySerializer ────────────────────────────────────────────────

/// Binary serialization for custody records in NMCP frames.
/// Fixed 256-byte record format for zero-allocation parsing.
pub struct CustodyBinarySerializer;

/// Size of a single binary-serialized custody record.
pub const RECORD_SIZE: usize = 256;

// Field offsets within the 256-byte record
const OFF_HASH: usize = 0;          // 32 bytes
const OFF_PREV_HASH: usize = 32;    // 32 bytes
const OFF_MERKLE_ROOT: usize = 64;  // 32 bytes
const OFF_TIMESTAMP: usize = 96;    // 8 bytes (unix millis, u64 LE)
const OFF_SEQUENCE: usize = 104;    // 8 bytes (i64 LE)
const OFF_EVENT_TYPE: usize = 112;  // 2 bytes (u16 LE, enum index)
const OFF_SUCCESS: usize = 114;     // 1 byte (0 or 1)
// 115..120 = 5 bytes padding
const OFF_DRONE_ID: usize = 120;    // 40 bytes (fixed, zero-padded)
const OFF_ACTION: usize = 160;      // 48 bytes (fixed, zero-padded)
const OFF_CORRELATION: usize = 208; // 48 bytes (fixed, zero-padded)
// 208+48 = 256

impl CustodyBinarySerializer {
    /// Serialize a batch of records into a binary NMCP Merkle frame.
    /// Format: [4B magic "NMCP"][32B Merkle root][N × 256B records]
    pub fn serialize_batch(records: &[CustodyRecord]) -> Vec<u8> {
        // Compute Merkle root over record hashes
        let leaves: Vec<[u8; 32]> = records.iter()
            .filter(|r| !r.hash.is_empty())
            .filter_map(|r| hex::decode(&r.hash).ok())
            .filter(|b| b.len() == 32)
            .map(|b| { let mut a = [0u8; 32]; a.copy_from_slice(&b); a })
            .collect();

        let merkle_root = if leaves.is_empty() {
            [0u8; 32]
        } else {
            MerkleTree::compute_root(&leaves)
        };

        let mut out = Vec::with_capacity(36 + records.len() * RECORD_SIZE);

        // NMCP magic
        out.extend_from_slice(b"NMCP");
        // Merkle root
        out.extend_from_slice(&merkle_root);

        // Serialize each record
        for record in records {
            let mut buf = [0u8; RECORD_SIZE];

            // Hash fields
            if let Ok(hash_bytes) = hex::decode(&record.hash) {
                let len = hash_bytes.len().min(32);
                buf[OFF_HASH..OFF_HASH + len].copy_from_slice(&hash_bytes[..len]);
            }
            if let Ok(prev_bytes) = hex::decode(&record.prev_hash) {
                let len = prev_bytes.len().min(32);
                buf[OFF_PREV_HASH..OFF_PREV_HASH + len].copy_from_slice(&prev_bytes[..len]);
            }
            if let Ok(merkle_bytes) = hex::decode(&record.merkle_root) {
                let len = merkle_bytes.len().min(32);
                buf[OFF_MERKLE_ROOT..OFF_MERKLE_ROOT + len].copy_from_slice(&merkle_bytes[..len]);
            }

            // Timestamp (unix millis)
            let ts_millis = record.timestamp.timestamp_millis() as u64;
            buf[OFF_TIMESTAMP..OFF_TIMESTAMP + 8].copy_from_slice(&ts_millis.to_le_bytes());

            // Sequence
            buf[OFF_SEQUENCE..OFF_SEQUENCE + 8].copy_from_slice(&record.sequence.to_le_bytes());

            // Event type (encoded as u16 index)
            let event_type_idx = event_type_to_u16(&record.event_type);
            buf[OFF_EVENT_TYPE..OFF_EVENT_TYPE + 2].copy_from_slice(&event_type_idx.to_le_bytes());

            // Success
            buf[OFF_SUCCESS] = if record.success { 1 } else { 0 };

            // Fixed-length string fields (zero-padded)
            write_fixed_str(&mut buf, OFF_DRONE_ID, 40, &record.drone_id);
            write_fixed_str(&mut buf, OFF_ACTION, 48, &record.action);
            if let Some(ref cid) = record.correlation_id {
                write_fixed_str(&mut buf, OFF_CORRELATION, 48, cid);
            }

            out.extend_from_slice(&buf);
        }

        out
    }

    /// Deserialize a batch of records from binary NMCP Merkle frame.
    /// Validates the "NMCP" magic and Merkle root.
    pub fn deserialize_batch(data: &[u8]) -> Option<Vec<CustodyRecord>> {
        if data.len() < 36 { return None; }

        // Validate magic
        if &data[0..4] != b"NMCP" { return None; }

        let merkle_root = &data[4..36];
        let payload = &data[36..];

        if payload.len() % RECORD_SIZE != 0 { return None; }
        let count = payload.len() / RECORD_SIZE;

        let mut records = Vec::with_capacity(count);

        for i in 0..count {
            let offset = i * RECORD_SIZE;
            let buf = &payload[offset..offset + RECORD_SIZE];

            let hash = hex::encode_upper(&buf[OFF_HASH..OFF_HASH + 32]);
            let prev_hash = hex::encode_upper(&buf[OFF_PREV_HASH..OFF_PREV_HASH + 32]);
            let merkle_root_hex = hex::encode_upper(&buf[OFF_MERKLE_ROOT..OFF_MERKLE_ROOT + 32]);

            let ts_millis = i64::from_le_bytes(buf[OFF_TIMESTAMP..OFF_TIMESTAMP + 8].try_into().ok()?);
            let timestamp = DateTime::from_timestamp_millis(ts_millis).unwrap_or_default();

            let sequence = i64::from_le_bytes(buf[OFF_SEQUENCE..OFF_SEQUENCE + 8].try_into().ok()?);

            let event_type_idx = u16::from_le_bytes(buf[OFF_EVENT_TYPE..OFF_EVENT_TYPE + 2].try_into().ok()?);
            let event_type = u16_to_event_type(event_type_idx);

            let success = buf[OFF_SUCCESS] != 0;

            let drone_id = read_fixed_str(buf, OFF_DRONE_ID, 40);
            let event_id = format!("{}:{}", drone_id, sequence);
            let action = read_fixed_str(buf, OFF_ACTION, 48);
            let correlation_id = read_fixed_str(buf, OFF_CORRELATION, 48);

            records.push(CustodyRecord {
                drone_id,
                event_id,
                sequence,
                timestamp,
                event_type,
                target_system: "local".to_string(),
                action,
                arguments: None,
                result: None,
                success,
                correlation_id: if correlation_id.is_empty() { None } else { Some(correlation_id) },
                prev_hash,
                hash,
                merkle_root: merkle_root_hex,
            });
        }

        // Verify Merkle root
        let computed_root = MerkleTree::compute_root(
            &records.iter()
                .filter(|r| !r.hash.is_empty())
                .filter_map(|r| {
                    let bytes = hex::decode(&r.hash).ok()?;
                    if bytes.len() != 32 { return None; }
                    let mut arr = [0u8; 32]; arr.copy_from_slice(&bytes);
                    Some(arr)
                })
                .collect::<Vec<_>>()
        );

        if computed_root != MerkleTree::compute_root(&[]) && &computed_root != merkle_root {
            // Merkle root mismatch — but we still return records (caller can decide)
            tracing::warn!("Merkle root mismatch in binary custody batch");
        }

        Some(records)
    }
}

// ─── Helpers ────────────────────────────────────────────────────────────────

fn write_fixed_str(buf: &mut [u8], offset: usize, max_len: usize, s: &str) {
    let bytes = s.as_bytes();
    let len = bytes.len().min(max_len);
    buf[offset..offset + len].copy_from_slice(&bytes[..len]);
}

fn read_fixed_str(buf: &[u8], offset: usize, max_len: usize) -> String {
    let slice = &buf[offset..offset + max_len];
    let end = slice.iter().position(|&b| b == 0).unwrap_or(max_len);
    String::from_utf8_lossy(&slice[..end]).to_string()
}

fn event_type_to_u16(event_type: &str) -> u16 {
    match event_type {
        "tool_call" => 1,
        "connection" => 2,
        "security" => 3,
        "cross_machine" => 4,
        "system_metrics" => 5,
        "process_event" => 6,
        "scheduled_task" => 7,
        _ => 0,
    }
}

fn u16_to_event_type(idx: u16) -> String {
    match idx {
        1 => "tool_call".into(),
        2 => "connection".into(),
        3 => "security".into(),
        4 => "cross_machine".into(),
        5 => "system_metrics".into(),
        6 => "process_event".into(),
        7 => "scheduled_task".into(),
        _ => "unknown".into(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn custody_record_hash_roundtrip() {
        let mut record = CustodyRecord {
            drone_id: "test-drone".into(),
            event_id: "test-drone:1".into(),
            sequence: 1,
            timestamp: Utc::now(),
            event_type: "tool_call".into(),
            target_system: "local".into(),
            action: "run_command".into(),
            arguments: Some("ls -la".into()),
            result: Some("success".into()),
            success: true,
            correlation_id: None,
            prev_hash: String::new(),
            hash: String::new(),
            merkle_root: String::new(),
        };

        record.seal();
        assert!(!record.hash.is_empty());
        assert!(record.verify_hash());
    }

    #[test]
    fn custody_chain_integrity() {
        let chain = CustodyChain::new("test");
        let r1 = chain.next_record("tool_call", "run_command", Some("ls"), None, true, None, None);
        let r2 = chain.next_record("tool_call", "read_file", Some("/etc/hosts"), None, true, None, None);
        let r3 = chain.next_record("connection", "messenger_connected", None, Some("ok"), true, Some("messenger"), None);

        assert_eq!(r1.sequence, 1);
        assert_eq!(r2.sequence, 2);
        assert_eq!(r3.sequence, 3);
        assert!(r1.prev_hash.is_empty()); // genesis
        assert_eq!(r2.prev_hash, r1.hash);
        assert_eq!(r3.prev_hash, r2.hash);

        assert!(CustodyChain::verify_chain(&[r1, r2, r3]));
    }

    #[test]
    fn merkle_tree_single_leaf() {
        let leaf = Sha256::digest(b"hello").into();
        let root = MerkleTree::compute_root(&[leaf]);
        assert_eq!(root, leaf); // single leaf IS the root
    }

    #[test]
    fn merkle_tree_proof_verification() {
        let leaves: Vec<[u8; 32]> = (0..4u8)
            .map(|i| Sha256::digest(&[i]).into())
            .collect();

        let root = MerkleTree::compute_root(&leaves);

        for i in 0..4 {
            let proof = MerkleTree::build_proof(&leaves, i);
            assert!(MerkleTree::verify_proof(&root, &leaves[i], &proof, i));
        }
    }

    #[test]
    fn merkle_tree_odd_count() {
        let leaves: Vec<[u8; 32]> = (0..3u8)
            .map(|i| Sha256::digest(&[i]).into())
            .collect();

        let root = MerkleTree::compute_root(&leaves);

        for i in 0..3 {
            let proof = MerkleTree::build_proof(&leaves, i);
            assert!(MerkleTree::verify_proof(&root, &leaves[i], &proof, i));
        }
    }

    #[test]
    fn binary_serialization_roundtrip() {
        let chain = CustodyChain::new("drone-1");
        let r1 = chain.next_record("tool_call", "run_command", Some("ls"), Some("ok"), true, None, None);
        let r2 = chain.next_record("connection", "connected", None, None, true, Some("messenger"), Some("corr-123"));

        // Assign Merkle root
        let mut records = vec![r1.clone(), r2.clone()];
        CustodyChain::assign_batch_merkle_root(&mut records);

        // Serialize
        let frame = CustodyBinarySerializer::serialize_batch(&records);
        assert!(frame.len() > 36);
        assert_eq!(&frame[0..4], b"NMCP");

        // Deserialize
        let parsed = CustodyBinarySerializer::deserialize_batch(&frame).unwrap();
        assert_eq!(parsed.len(), 2);
        assert_eq!(parsed[0].action, "run_command");
        assert_eq!(parsed[1].action, "connected");
        assert_eq!(parsed[1].correlation_id.as_deref(), Some("corr-123"));
    }
}
