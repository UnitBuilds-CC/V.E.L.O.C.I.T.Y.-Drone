using System.Buffers.Binary;

namespace Drone.Core.Protocol;

/// <summary>
/// NMCP binary frame using big-endian byte order for cross-platform compatibility.
/// Frame layout (header = 16 bytes):
///   [0..3]   Magic      = 0x564E4D43 (VNMC)
///   [4..7]   FrameType
///   [8..11]  PayloadLen
///   [12..15] SequenceId
///   [16..]   Payload
/// </summary>
public readonly struct NmcpFrame
{
    public const uint Magic = 0x564E4D43;
    public const int HeaderSize = 16;
    /// <summary>Maximum allowed payload size (16 MB). Frames exceeding this are rejected as corrupted.</summary>
    public const uint MaxPayloadSize = 16 * 1024 * 1024;

    public uint FrameType { get; }
    public uint PayloadLength { get; }
    public uint SequenceId { get; }
    public ReadOnlyMemory<byte> Payload { get; }

    public NmcpFrame(uint frameType, uint sequenceId, ReadOnlyMemory<byte> payload)
    {
        FrameType = frameType;
        SequenceId = sequenceId;
        PayloadLength = (uint)payload.Length;
        Payload = payload;
    }

    public int WriteHeader(Span<byte> buffer)
    {
        BinaryPrimitives.WriteUInt32BigEndian(buffer[0..4], Magic);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[4..8], FrameType);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[8..12], PayloadLength);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[12..16], SequenceId);
        return HeaderSize;
    }

    public static bool TryReadHeader(ReadOnlySpan<byte> buffer, out uint frameType, out uint payloadLen, out uint seqId)
    {
        frameType = 0; payloadLen = 0; seqId = 0;
        if (buffer.Length < HeaderSize) return false;
        var magic = BinaryPrimitives.ReadUInt32BigEndian(buffer[0..4]);
        if (magic != Magic) return false;
        frameType = BinaryPrimitives.ReadUInt32BigEndian(buffer[4..8]);
        payloadLen = BinaryPrimitives.ReadUInt32BigEndian(buffer[8..12]);
        seqId = BinaryPrimitives.ReadUInt32BigEndian(buffer[12..16]);
        // Reject frames with unreasonably large payloads (corruption guard)
        if (payloadLen > MaxPayloadSize) return false;
        return true;
    }
}

public static class NmcpFrameTypes
{
    public const uint JsonRpcRequest = 1;
    public const uint JsonRpcResponse = 2;
    public const uint JsonRpcNotification = 3;
    public const uint ToolCall = 10;
    public const uint ToolResult = 11;
    public const uint ScreenCapture = 20;
    public const uint InputEvent = 21;
    public const uint SystemMetrics = 30;

    // Custody trail frame types (40-49)
    public const uint CustodyReport = 40;
    public const uint CustodyQuery = 41;
    public const uint CustodyTimeline = 42;
    public const uint CustodyStream = 43;

    public const uint Heartbeat = 100;
    public const uint Handshake = 101;
}
