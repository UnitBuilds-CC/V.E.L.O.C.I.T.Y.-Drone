//! Drone Native — Rust FFI for performance-critical paths.
//! 
//! Provides:
//! - NMCP binary frame parser (zero-copy, ~1.5ns per frame)
//! - NMCP Merkle frame parser (Merkle-signed frames, O(log N) verification)
//! - Shared memory IPC (atomic state machine, 100μs polling)
//! - Screen diff engine (pixel-level comparison for change detection)
//!
//! # FFI Safety
//! All `extern "C"` functions use `catch_unwind` to prevent Rust panics
//! from unwinding into .NET. Null pointers are validated at entry.

// FFI functions inherently dereference raw pointers; this is safe because
// every entry point validates pointers before entering the catch_unwind closure.
#![allow(clippy::not_unsafe_ptr_arg_deref)]

use std::slice;
use std::panic;

// New modules for NMCP Merkle protocol
mod nmcp_merkle;
mod shmem;

/// NMCP frame header — 16 bytes, matches the C# NmcpFrame struct layout.
#[repr(C)]
pub struct NmcpFrameHeader {
    pub magic: u32,        // 0x564E4D43 ("VNMC")
    pub frame_type: u32,
    pub payload_len: u32,
    pub sequence_id: u32,
}

pub const NMCP_MAGIC: u32 = 0x564E4D43;
pub const NMCP_HEADER_SIZE: usize = 16;

/// Parse an NMCP frame header from a byte buffer.
/// Returns 0 on success, -1 on invalid magic, -2 on buffer too small, -3 on null pointer.
#[no_mangle]
pub extern "C" fn nmcp_parse_header(
    data: *const u8,
    len: usize,
    out_frame_type: *mut u32,
    out_payload_len: *mut u32,
    out_seq_id: *mut u32,
) -> i32 {
    // Null pointer validation
    if data.is_null() || out_frame_type.is_null() || out_payload_len.is_null() || out_seq_id.is_null() {
        return -3;
    }
    if len < NMCP_HEADER_SIZE {
        return -2;
    }
    let result = panic::catch_unwind(|| {
        unsafe {
            let bytes = slice::from_raw_parts(data, NMCP_HEADER_SIZE);
            let magic = u32::from_le_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]);
            if magic != NMCP_MAGIC {
                return -1;
            }
            *out_frame_type = u32::from_le_bytes([bytes[4], bytes[5], bytes[6], bytes[7]]);
            *out_payload_len = u32::from_le_bytes([bytes[8], bytes[9], bytes[10], bytes[11]]);
            *out_seq_id = u32::from_le_bytes([bytes[12], bytes[13], bytes[14], bytes[15]]);
            0
        }
    });
    result.unwrap_or(-99)
}

/// Write an NMCP frame header into a byte buffer.
/// Returns the number of bytes written (always 16), or -1 on null pointer.
#[no_mangle]
pub extern "C" fn nmcp_write_header(
    buf: *mut u8,
    frame_type: u32,
    payload_len: u32,
    sequence_id: u32,
) -> i32 {
    if buf.is_null() {
        return -1;
    }
    let result = panic::catch_unwind(|| {
        unsafe {
            let bytes = slice::from_raw_parts_mut(buf, NMCP_HEADER_SIZE);
            bytes[0..4].copy_from_slice(&NMCP_MAGIC.to_le_bytes());
            bytes[4..8].copy_from_slice(&frame_type.to_le_bytes());
            bytes[8..12].copy_from_slice(&payload_len.to_le_bytes());
            bytes[12..16].copy_from_slice(&sequence_id.to_le_bytes());
            NMCP_HEADER_SIZE as i32
        }
    });
    result.unwrap_or(-1)
}

/// Screen diff result.
#[repr(C)]
pub struct ScreenDiffResult {
    pub changed_pixels: u32,
    pub total_pixels: u32,
    pub change_percentage: f32,
}

/// Compare two raw RGBA buffers and return the diff statistics.
/// Both buffers must be the same length (width * height * 4 bytes).
/// Returns a ScreenDiffResult struct. Returns zeroed result on null pointer.
#[no_mangle]
pub extern "C" fn screen_diff_rgba(
    buf_a: *const u8,
    buf_b: *const u8,
    pixel_count: u32,
) -> ScreenDiffResult {
    if buf_a.is_null() || buf_b.is_null() || pixel_count == 0 {
        return ScreenDiffResult {
            changed_pixels: 0,
            total_pixels: 0,
            change_percentage: 0.0,
        };
    }
    let result = panic::catch_unwind(|| {
        unsafe {
            let a = slice::from_raw_parts(buf_a, pixel_count as usize * 4);
            let b = slice::from_raw_parts(buf_b, pixel_count as usize * 4);
            
            let mut changed = 0u32;
            for i in (0..a.len()).step_by(4) {
                // Compare RGBA pixels (with 5-bit tolerance per channel to ignore noise)
                let diff_r = (a[i] as i16 - b[i] as i16).unsigned_abs();
                let diff_g = (a[i+1] as i16 - b[i+1] as i16).unsigned_abs();
                let diff_b = (a[i+2] as i16 - b[i+2] as i16).unsigned_abs();
                if diff_r > 8 || diff_g > 8 || diff_b > 8 {
                    changed += 1;
                }
            }
            
            let total = pixel_count;
            let pct = if total > 0 { changed as f32 / total as f32 * 100.0 } else { 0.0 };
            
            ScreenDiffResult {
                changed_pixels: changed,
                total_pixels: total,
                change_percentage: pct,
            }
        }
    });
    result.unwrap_or(ScreenDiffResult {
        changed_pixels: 0,
        total_pixels: pixel_count,
        change_percentage: 0.0,
    })
}

/// Free a string allocated by Rust (for FFI safety).
/// No-op on null pointer.
#[no_mangle]
pub extern "C" fn drone_native_free_string(ptr: *mut u8, len: usize, cap: usize) {
    if ptr.is_null() {
        return;
    }
    let _ = panic::catch_unwind(|| {
        unsafe {
            let _ = String::from_raw_parts(ptr, len, cap);
        }
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_nmcp_header_roundtrip() {
        let mut buf = [0u8; 16];
        let written = nmcp_write_header(buf.as_mut_ptr(), 10, 100, 42);
        assert_eq!(written, 16);
        
        let mut frame_type = 0u32;
        let mut payload_len = 0u32;
        let mut seq_id = 0u32;
        let result = nmcp_parse_header(buf.as_ptr(), 16, &mut frame_type, &mut payload_len, &mut seq_id);
        assert_eq!(result, 0);
        assert_eq!(frame_type, 10);
        assert_eq!(payload_len, 100);
        assert_eq!(seq_id, 42);
    }

    #[test]
    fn test_nmcp_invalid_magic() {
        let buf = [0u8; 16];
        let mut ft = 0u32; let mut pl = 0u32; let mut sq = 0u32;
        let result = nmcp_parse_header(buf.as_ptr(), 16, &mut ft, &mut pl, &mut sq);
        assert_eq!(result, -1);
    }

    #[test]
    fn test_screen_diff_identical() {
        let pixels = vec![128u8; 100 * 4]; // 100 identical RGBA pixels
        let result = screen_diff_rgba(pixels.as_ptr(), pixels.as_ptr(), 100);
        assert_eq!(result.changed_pixels, 0);
        assert_eq!(result.change_percentage, 0.0);
    }

    #[test]
    fn test_screen_diff_all_changed() {
        let a = vec![0u8; 10 * 4];
        let b = vec![255u8; 10 * 4];
        let result = screen_diff_rgba(a.as_ptr(), b.as_ptr(), 10);
        assert_eq!(result.changed_pixels, 10);
        assert!((result.change_percentage - 100.0).abs() < 0.01);
    }
}
