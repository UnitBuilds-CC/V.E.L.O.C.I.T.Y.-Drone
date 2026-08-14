//! NMCP binary frame protocol.
//!
//! Frame layout (header = 16 bytes, big-endian):
//!   [0..3]   Magic      = 0x564E4D43 ("VNMC")
//!   [4..7]   FrameType
//!   [8..11]  PayloadLen
//!   [12..15] SequenceId
//!   [16..]   Payload

/// NMCP frame magic bytes: "VNMC" in big-endian.
pub const NMCP_MAGIC: u32 = 0x564E4D43;

/// Size of the NMCP frame header in bytes.
pub const HEADER_SIZE: usize = 16;

/// Maximum allowed payload size (16 MB). Frames exceeding this are rejected.
pub const MAX_PAYLOAD_SIZE: u32 = 16 * 1024 * 1024;

/// A parsed NMCP binary frame.
#[derive(Debug, Clone)]
pub struct NmcpFrame {
    pub frame_type: u32,
    pub sequence_id: u32,
    pub payload: Vec<u8>,
}

impl NmcpFrame {
    /// Create a new frame.
    pub fn new(frame_type: u32, sequence_id: u32, payload: Vec<u8>) -> Self {
        Self { frame_type, sequence_id, payload }
    }

    /// Write the 16-byte header into the given buffer (big-endian).
    /// Buffer must be at least 16 bytes.
    pub fn write_header(&self, buf: &mut [u8]) -> usize {
        assert!(buf.len() >= HEADER_SIZE);
        buf[0..4].copy_from_slice(&NMCP_MAGIC.to_be_bytes());
        buf[4..8].copy_from_slice(&self.frame_type.to_be_bytes());
        buf[8..12].copy_from_slice(&(self.payload.len() as u32).to_be_bytes());
        buf[12..16].copy_from_slice(&self.sequence_id.to_be_bytes());
        HEADER_SIZE
    }

    /// Serialize the full frame (header + payload) into bytes.
    pub fn to_bytes(&self) -> Vec<u8> {
        let mut out = vec![0u8; HEADER_SIZE + self.payload.len()];
        self.write_header(&mut out);
        out[HEADER_SIZE..].copy_from_slice(&self.payload);
        out
    }

    /// Try to parse a frame header from a byte buffer.
    /// Returns (frame_type, payload_len, sequence_id) on success.
    pub fn try_read_header(buf: &[u8]) -> Option<(u32, u32, u32)> {
        if buf.len() < HEADER_SIZE {
            return None;
        }
        let magic = u32::from_be_bytes([buf[0], buf[1], buf[2], buf[3]]);
        if magic != NMCP_MAGIC {
            return None;
        }
        let frame_type = u32::from_be_bytes([buf[4], buf[5], buf[6], buf[7]]);
        let payload_len = u32::from_be_bytes([buf[8], buf[9], buf[10], buf[11]]);
        let seq_id = u32::from_be_bytes([buf[12], buf[13], buf[14], buf[15]]);

        // Reject frames with unreasonably large payloads (corruption guard)
        if payload_len > MAX_PAYLOAD_SIZE {
            return None;
        }

        Some((frame_type, payload_len, seq_id))
    }

    /// Try to parse a complete frame (header + payload) from a byte buffer.
    pub fn try_parse(buf: &[u8]) -> Option<Self> {
        let (frame_type, payload_len, seq_id) = Self::try_read_header(buf)?;
        let total = HEADER_SIZE + payload_len as usize;
        if buf.len() < total {
            return None;
        }
        let payload = buf[HEADER_SIZE..total].to_vec();
        Some(Self { frame_type, sequence_id: seq_id, payload })
    }
}

/// NMCP frame type constants.
pub struct NmcpFrameTypes;

impl NmcpFrameTypes {
    // JSON-RPC frame types
    pub const JSON_RPC_REQUEST: u32 = 1;
    pub const JSON_RPC_RESPONSE: u32 = 2;
    pub const JSON_RPC_NOTIFICATION: u32 = 3;

    // Tool frame types
    pub const TOOL_CALL: u32 = 10;
    pub const TOOL_RESULT: u32 = 11;

    // Screen/input frame types
    pub const SCREEN_CAPTURE: u32 = 20;
    pub const INPUT_EVENT: u32 = 21;

    // System metrics
    pub const SYSTEM_METRICS: u32 = 30;

    // Custody trail frame types (40-49)
    pub const CUSTODY_REPORT: u32 = 40;
    pub const CUSTODY_QUERY: u32 = 41;
    pub const CUSTODY_TIMELINE: u32 = 42;
    pub const CUSTODY_STREAM: u32 = 43;
    /// NMCP Merkle-signed binary custody batch.
    pub const CUSTODY_BINARY: u32 = 44;

    // Connection management
    pub const HEARTBEAT: u32 = 100;
    pub const HANDSHAKE: u32 = 101;
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn frame_header_roundtrip() {
        let frame = NmcpFrame::new(NmcpFrameTypes::TOOL_CALL, 42, vec![1, 2, 3]);
        let bytes = frame.to_bytes();
        assert_eq!(bytes.len(), HEADER_SIZE + 3);

        let parsed = NmcpFrame::try_parse(&bytes).unwrap();
        assert_eq!(parsed.frame_type, NmcpFrameTypes::TOOL_CALL);
        assert_eq!(parsed.sequence_id, 42);
        assert_eq!(parsed.payload, vec![1, 2, 3]);
    }

    #[test]
    fn invalid_magic_rejected() {
        let mut buf = [0u8; 16];
        buf[0] = 0xFF; // wrong magic
        assert!(NmcpFrame::try_read_header(&buf).is_none());
    }

    #[test]
    fn oversized_payload_rejected() {
        let mut buf = [0u8; 16];
        buf[0..4].copy_from_slice(&NMCP_MAGIC.to_be_bytes());
        // Set payload_len to 17MB (> MAX_PAYLOAD_SIZE)
        let big_len: u32 = 17 * 1024 * 1024;
        buf[8..12].copy_from_slice(&big_len.to_be_bytes());
        assert!(NmcpFrame::try_read_header(&buf).is_none());
    }

    #[test]
    fn empty_payload_frame() {
        let frame = NmcpFrame::new(NmcpFrameTypes::HEARTBEAT, 1, vec![]);
        let bytes = frame.to_bytes();
        assert_eq!(bytes.len(), HEADER_SIZE);
        let parsed = NmcpFrame::try_parse(&bytes).unwrap();
        assert!(parsed.payload.is_empty());
    }
}
