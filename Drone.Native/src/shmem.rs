//! Shared memory IPC with atomic state machine.
//! Ported from V.E.L.O.C.I.T.Y.-MCP's shmem.rs.
//! Provides FFI functions for reading/writing shared memory buffers
//! using the 5-state atomic protocol.
//!
//! # FFI Safety
//! All `extern "C"` functions use `catch_unwind` to prevent Rust panics
//! from unwinding into .NET. Null pointers are validated at entry.

use std::fs::OpenOptions;
use std::io::{Read, Write, Seek, SeekFrom};
use std::panic;

/// Shared memory layout (matches C# McpServer/VelocityConnection):
/// Request channel:  [0] = state byte, [1..4] = payload length (i32 LE), [5..4099] = payload (4096 bytes)
/// Response channel: [4100] = state byte, [4101..4104] = payload length (i32 LE), [4105..65535] = payload
pub const SHMEM_TOTAL_SIZE: usize = 65536;
pub const REQ_STATE_OFFSET: usize = 0;
pub const REQ_LEN_OFFSET: usize = 1;
pub const REQ_PAYLOAD_OFFSET: usize = 5;
pub const REQ_PAYLOAD_SIZE: usize = 4096;
pub const RES_STATE_OFFSET: usize = 4100;
pub const RES_LEN_OFFSET: usize = 4101;
pub const RES_PAYLOAD_OFFSET: usize = 4105;
pub const RES_PAYLOAD_SIZE: usize = SHMEM_TOTAL_SIZE - RES_PAYLOAD_OFFSET;

// State machine — 5-state protocol for lock-free shmem IPC
pub const STATE_IDLE: u8 = 0;
pub const STATE_REQ_READY: u8 = 1;
pub const STATE_PROCESSING: u8 = 2;
pub const STATE_RES_READY: u8 = 3;
pub const STATE_ERROR: u8 = 4;

/// Read a request from shared memory.
/// Returns 0 on success (request available), -1 if no request ready, -2 on error, -3 on null pointer.
/// On success, transitions the request channel to STATE_PROCESSING.
/// The caller must provide a buffer of at least `max_payload_len` bytes.
#[no_mangle]
pub extern "C" fn nmcp_shmem_read_request(
    buffer_path: *const u8,
    path_len: usize,
    out_state: *mut u8,
    out_payload: *mut u8,
    out_payload_len: *mut i32,
    max_payload_len: usize,
) -> i32 {
    if buffer_path.is_null() || out_state.is_null() || out_payload.is_null() || out_payload_len.is_null() {
        return -3;
    }
    let result = panic::catch_unwind(|| {
        unsafe {
            let path_bytes = std::slice::from_raw_parts(buffer_path, path_len);
            let path = match std::str::from_utf8(path_bytes) {
                Ok(s) => s,
                Err(_) => return -2,
            };

            let mut file = match OpenOptions::new().read(true).write(true).open(path) {
                Ok(f) => f,
                Err(_) => return -2,
            };

            // Read request state
            let mut state_buf = [0u8; 1];
            if file.seek(SeekFrom::Start(REQ_STATE_OFFSET as u64)).is_err() { return -2; }
            if file.read_exact(&mut state_buf).is_err() { return -2; }
            let state = state_buf[0];

            *out_state = state;

            if state != STATE_REQ_READY {
                return -1; // No request ready
            }

            // Read payload length
            let mut len_buf = [0u8; 4];
            if file.seek(SeekFrom::Start(REQ_LEN_OFFSET as u64)).is_err() { return -2; }
            if file.read_exact(&mut len_buf).is_err() { return -2; }
            let payload_len = i32::from_le_bytes(len_buf) as usize;

            if payload_len == 0 || payload_len > REQ_PAYLOAD_SIZE || payload_len > max_payload_len {
                // Signal error on the request channel
                let _ = file.seek(SeekFrom::Start(REQ_STATE_OFFSET as u64));
                let _ = file.write_all(&[STATE_ERROR]);
                return -2;
            }

            // Read payload
            let payload_out = std::slice::from_raw_parts_mut(out_payload, payload_len);
            if file.seek(SeekFrom::Start(REQ_PAYLOAD_OFFSET as u64)).is_err() { return -2; }
            if file.read_exact(payload_out).is_err() { return -2; }

            // Transition request channel to PROCESSING (we're handling it)
            if file.seek(SeekFrom::Start(REQ_STATE_OFFSET as u64)).is_err() { return -2; }
            if file.write_all(&[STATE_PROCESSING]).is_err() { return -2; }

            *out_payload_len = payload_len as i32;
            0
        }
    });
    result.unwrap_or(-2)
}

/// Write a response to shared memory and signal RES_READY.
/// Returns 0 on success, -1 on error, -3 on null pointer.
#[no_mangle]
pub extern "C" fn nmcp_shmem_write_response(
    buffer_path: *const u8,
    path_len: usize,
    payload: *const u8,
    payload_len: i32,
) -> i32 {
    if buffer_path.is_null() || payload.is_null() {
        return -3;
    }
    let result = panic::catch_unwind(|| {
        unsafe {
            let path_bytes = std::slice::from_raw_parts(buffer_path, path_len);
            let path = match std::str::from_utf8(path_bytes) {
                Ok(s) => s,
                Err(_) => return -1,
            };

            let mut file = match OpenOptions::new().write(true).open(path) {
                Ok(f) => f,
                Err(_) => return -1,
            };

            let len = payload_len as usize;
            if len > RES_PAYLOAD_SIZE {
                let _ = file.seek(SeekFrom::Start(RES_STATE_OFFSET as u64));
                let _ = file.write_all(&[STATE_ERROR]);
                return -1;
            }

            // Write payload length
            let len_bytes = payload_len.to_le_bytes();
            if file.seek(SeekFrom::Start(RES_LEN_OFFSET as u64)).is_err() {
                let _ = file.seek(SeekFrom::Start(RES_STATE_OFFSET as u64));
                let _ = file.write_all(&[STATE_ERROR]);
                return -1;
            }
            if file.write_all(&len_bytes).is_err() {
                let _ = file.seek(SeekFrom::Start(RES_STATE_OFFSET as u64));
                let _ = file.write_all(&[STATE_ERROR]);
                return -1;
            }

            // Write payload
            if len > 0 {
                let payload_data = std::slice::from_raw_parts(payload, len);
                if file.seek(SeekFrom::Start(RES_PAYLOAD_OFFSET as u64)).is_err() {
                    let _ = file.seek(SeekFrom::Start(RES_STATE_OFFSET as u64));
                    let _ = file.write_all(&[STATE_ERROR]);
                    return -1;
                }
                if file.write_all(payload_data).is_err() {
                    let _ = file.seek(SeekFrom::Start(RES_STATE_OFFSET as u64));
                    let _ = file.write_all(&[STATE_ERROR]);
                    return -1;
                }
            }

            // Signal RES_READY
            if file.seek(SeekFrom::Start(RES_STATE_OFFSET as u64)).is_err() { return -1; }
            if file.write_all(&[STATE_RES_READY]).is_err() { return -1; }

            0
        }
    });
    result.unwrap_or(-1)
}

/// Set the request state to IDLE (acknowledge that we've read the response).
/// Returns 0 on success, -1 on error, -3 on null pointer.
#[no_mangle]
pub extern "C" fn nmcp_shmem_reset_response(
    buffer_path: *const u8,
    path_len: usize,
) -> i32 {
    if buffer_path.is_null() {
        return -3;
    }
    let result = panic::catch_unwind(|| {
        unsafe {
            let path_bytes = std::slice::from_raw_parts(buffer_path, path_len);
            let path = match std::str::from_utf8(path_bytes) {
                Ok(s) => s,
                Err(_) => return -1,
            };

            let mut file = match OpenOptions::new().write(true).open(path) {
                Ok(f) => f,
                Err(_) => return -1,
            };

            if file.seek(SeekFrom::Start(RES_STATE_OFFSET as u64)).is_err() { return -1; }
            if file.write_all(&[STATE_IDLE]).is_err() { return -1; }

            0
        }
    });
    result.unwrap_or(-1)
}
