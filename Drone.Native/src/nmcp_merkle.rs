//! NMCP Merkle frame parsing and root computation.
//! Ported from V.E.L.O.C.I.T.Y.-MCP's nmcp_binary.rs.
//! Provides zero-allocation binary frame parsing with Merkle signature verification.

use sha2::{Sha256, Digest};
use std::slice;

/// NMCP Merkle frame magic bytes: b"NMCP"
pub const NMCP_MERKLE_MAGIC: [u8; 4] = [0x4E, 0x4D, 0x43, 0x50]; // "NMCP"

/// Size of the Merkle root (SHA-256 = 32 bytes)
pub const MERKLE_ROOT_SIZE: usize = 32;

/// Total header size: 4 (magic) + 32 (merkle root) = 36 bytes
pub const NMCP_MERKLE_HEADER_SIZE: usize = 4 + MERKLE_ROOT_SIZE;

/// NMCP Merkle frame: magic + merkle_root + payload.
/// The Merkle root is the SHA-256 root of a Merkle tree built over the payload's
/// constituent record hashes, enabling O(log N) verification.
/// This struct defines the FFI layout for C# interop.
#[repr(C)]
#[allow(dead_code)]
pub struct NmcpMerkleFrame {
    pub magic: [u8; 4],
    pub merkle_root: [u8; 32],
    pub payload_len: u32,
}

/// Parse an NMCP Merkle frame header from a byte buffer.
/// Returns 0 on success, -1 on invalid magic, -2 on buffer too small.
/// Writes the merkle root and payload length to the output pointers.
#[no_mangle]
pub extern "C" fn nmcp_merkle_parse_frame(
    data: *const u8,
    len: usize,
    out_merkle_root: *mut u8,
    out_payload_len: *mut u32,
) -> i32 {
    if len < NMCP_MERKLE_HEADER_SIZE {
        return -2;
    }
    unsafe {
        let bytes = slice::from_raw_parts(data, NMCP_MERKLE_HEADER_SIZE);

        // Check magic
        if bytes[0..4] != NMCP_MERKLE_MAGIC {
            return -1;
        }

        // Copy merkle root (32 bytes)
        let root_out = slice::from_raw_parts_mut(out_merkle_root, MERKLE_ROOT_SIZE);
        root_out.copy_from_slice(&bytes[4..4 + MERKLE_ROOT_SIZE]);

        // Payload length is implicit (total frame length - header size)
        // The caller knows the total frame length, so we return the header's parsed data
        *out_payload_len = (len - NMCP_MERKLE_HEADER_SIZE) as u32;

        0
    }
}

/// Write an NMCP Merkle frame header into a byte buffer.
/// Returns the number of bytes written (always 36).
#[no_mangle]
pub extern "C" fn nmcp_merkle_write_frame(
    buf: *mut u8,
    merkle_root: *const u8,
) -> i32 {
    unsafe {
        let out = slice::from_raw_parts_mut(buf, NMCP_MERKLE_HEADER_SIZE);
        let root = slice::from_raw_parts(merkle_root, MERKLE_ROOT_SIZE);

        // Write magic
        out[0..4].copy_from_slice(&NMCP_MERKLE_MAGIC);
        // Write merkle root
        out[4..4 + MERKLE_ROOT_SIZE].copy_from_slice(root);

        NMCP_MERKLE_HEADER_SIZE as i32
    }
}

/// Compute a SHA-256 Merkle root from an array of 32-byte leaf hashes.
/// Leaves are concatenated in pairs, hashed, and the tree is built bottom-up.
/// Odd nodes are duplicated (hashed with themselves).
/// Returns 0 on success, -1 on invalid input.
#[no_mangle]
pub extern "C" fn nmcp_merkle_compute_root(
    leaves: *const *const u8,
    leaf_count: usize,
    out_root: *mut u8,
) -> i32 {
    if leaf_count == 0 {
        // Empty tree: hash of empty data
        let hash = Sha256::digest([]);
        unsafe {
            let root_out = slice::from_raw_parts_mut(out_root, MERKLE_ROOT_SIZE);
            root_out.copy_from_slice(&hash);
        }
        return 0;
    }

    unsafe {
        let leaf_ptrs = slice::from_raw_parts(leaves, leaf_count);

        // Collect leaf hashes
        let mut current_level: Vec<[u8; 32]> = Vec::with_capacity(leaf_count);
        for i in 0..leaf_count {
            let leaf_data = slice::from_raw_parts(leaf_ptrs[i], 32);
            let mut hash = [0u8; 32];
            hash.copy_from_slice(leaf_data);
            current_level.push(hash);
        }

        // Build tree bottom-up
        while current_level.len() > 1 {
            let mut next_level = Vec::new();
            let mut i = 0;
            while i < current_level.len() {
                let left = &current_level[i];
                let right = if i + 1 < current_level.len() {
                    &current_level[i + 1]
                } else {
                    // Odd node: duplicate
                    &current_level[i]
                };

                let mut hasher = Sha256::new();
                hasher.update(left);
                hasher.update(right);
                let result = hasher.finalize();
                let mut hash = [0u8; 32];
                hash.copy_from_slice(&result);
                next_level.push(hash);

                i += 2;
            }
            current_level = next_level;
        }

        let root_out = slice::from_raw_parts_mut(out_root, MERKLE_ROOT_SIZE);
        root_out.copy_from_slice(&current_level[0]);
    }

    0
}

/// Verify a Merkle proof for a specific leaf.
/// The proof is an array of 32-byte sibling hashes (bottom to top).
/// `index` is the 0-based position of the leaf in the original tree.
/// Returns 1 if the proof is valid (computed root matches expected root), 0 otherwise.
#[no_mangle]
pub extern "C" fn nmcp_merkle_verify_proof(
    expected_root: *const u8,
    leaf: *const u8,
    proof: *const *const u8,
    proof_len: usize,
    index: usize,
) -> i32 {
    unsafe {
        let root_bytes = slice::from_raw_parts(expected_root, MERKLE_ROOT_SIZE);
        let leaf_bytes = slice::from_raw_parts(leaf, MERKLE_ROOT_SIZE);
        let proof_ptrs = slice::from_raw_parts(proof, proof_len);

        let mut current = [0u8; 32];
        current.copy_from_slice(leaf_bytes);
        let mut idx = index;

        for i in 0..proof_len {
            let sibling = slice::from_raw_parts(proof_ptrs[i], MERKLE_ROOT_SIZE);

            let mut hasher = Sha256::new();
            if idx % 2 == 0 {
                // Current is left child
                hasher.update(current);
                hasher.update(sibling);
            } else {
                // Current is right child
                hasher.update(sibling);
                hasher.update(current);
            }

            let result = hasher.finalize();
            current.copy_from_slice(&result);
            idx /= 2;
        }

        // Compare computed root with expected root
        if current == root_bytes {
            1
        } else {
            0
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_merkle_frame_roundtrip() {
        let root = [42u8; 32];
        let mut buf = [0u8; NMCP_MERKLE_HEADER_SIZE];
        let written = nmcp_merkle_write_frame(buf.as_mut_ptr(), root.as_ptr());
        assert_eq!(written, NMCP_MERKLE_HEADER_SIZE as i32);

        let mut out_root = [0u8; 32];
        let mut out_len = 0u32;
        let result = nmcp_merkle_parse_frame(
            buf.as_ptr(), NMCP_MERKLE_HEADER_SIZE + 100,
            out_root.as_mut_ptr(), &mut out_len,
        );
        assert_eq!(result, 0);
        assert_eq!(out_root, root);
        assert_eq!(out_len, 100);
    }

    #[test]
    fn test_merkle_invalid_magic() {
        let buf = [0u8; NMCP_MERKLE_HEADER_SIZE];
        let mut out_root = [0u8; 32];
        let mut out_len = 0u32;
        let result = nmcp_merkle_parse_frame(
            buf.as_ptr(), NMCP_MERKLE_HEADER_SIZE,
            out_root.as_mut_ptr(), &mut out_len,
        );
        assert_eq!(result, -1);
    }

    #[test]
    fn test_merkle_compute_single_leaf() {
        let leaf = [1u8; 32];
        let leaf_ptr = &leaf as *const u8;
        let mut root = [0u8; 32];
        let result = nmcp_merkle_compute_root(&leaf_ptr, 1, root.as_mut_ptr());
        assert_eq!(result, 0);
        // Single leaf: root == leaf
        assert_eq!(root, leaf);
    }

    #[test]
    fn test_merkle_compute_two_leaves() {
        let leaf_a = [1u8; 32];
        let leaf_b = [2u8; 32];
        let ptrs = [&leaf_a as *const u8, &leaf_b as *const u8];
        let mut root = [0u8; 32];
        let result = nmcp_merkle_compute_root(ptrs.as_ptr(), 2, root.as_mut_ptr());
        assert_eq!(result, 0);

        // Verify: root should be SHA-256(leaf_a || leaf_b)
        let mut hasher = Sha256::new();
        hasher.update(leaf_a);
        hasher.update(leaf_b);
        let expected = hasher.finalize();
        assert_eq!(&root[..], &expected[..]);
    }
}
