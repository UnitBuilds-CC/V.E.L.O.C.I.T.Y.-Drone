namespace Drone.Core.Protocol;

/// <summary>
/// NMCP binary frame â€” zero-allocation structure for high-performance IPC.
/// Mirrors the Velocity-MCP binary protocol for cross-compatibility.
/// 
/// Frame layout (header = 16 bytes):
///   [0..3]   Magic      â€” 0x564E4D43 ("VNMC")
///   [4..7]   FrameType  â€” enum
///   [8..11]  PayloadLen â€” uint32 payload length
///   [12..15] SequenceId â€” uint32 monotonic counter
///   [16..]   Payload    â€” variable-length data
/// </summary>
public readonly struct NmcpFrame
{
    public const uint Magic = 0x564E4D43; // "VNMC"
    public const int HeaderSize = 16;

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

    /// <summary>Write frame header into buffer. Returns bytes written (always 16).</summary>
    public int WriteHeader(Span<byte> buffer)
    {
        BitConverter.TryWriteBytes(buffer[0..4], Magic);
        BitConverter.TryWriteBytes(buffer[4..8], FrameType);
        BitConverter.TryWriteBytes(buffer[8..12], PayloadLength);
        BitConverter.TryWriteBytes(buffer[12..16], SequenceId);
        return HeaderSize;
    }

    /// <summary>Try to read a frame header from buffer. Returns false if magic doesn't match.</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> buffer, out uint frameType, out uint payloadLen, out uint seqId)
    {
        frameType = 0;
        payloadLen = 0;
        seqId = 0;

        if (buffer.Length < HeaderSize) return false;

        var magic = BitConverter.ToUInt32(buffer[0..4]);
        if (magic != Magic) return false;

        frameType = BitConverter.ToUInt32(buffer[4..8]);
        payloadLen = BitConverter.ToUInt32(buffer[8..12]);
        seqId = BitConverter.ToUInt32(buffer[12..16]);
        return true;
    }
}

/// <summary>Well-known NMCP frame types for Drone communication.</summary>
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
    public const uint Heartbeat = 100;
    public const uint Handshake = 101;
}
