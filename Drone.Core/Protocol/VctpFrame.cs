using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Drone.Core.Protocol;

/// <summary>
/// VCTP (Velocity Transfer Protocol) binary frame for cross-machine relay.
/// VCTP-inspired framing over WebSocket transport (NAT-traversable).
/// 
/// Frame layout (header = 28 bytes, little-endian):
///   [0..3]   Magic         = 0x50544356 ("VCTP")
///   [4..5]   FrameType     (ushort)
///   [6..13]  SequenceId    (ulong)
///   [14..15] SrcLen        (ushort) — source drone ID length
///   [16..17] DstLen        (ushort) — destination drone ID length
///   [18..21] PayloadLen    (uint)
///   [22..25] Flags         (uint)
///   [26..27] Reserved      (ushort)
///   [28..]   SrcId + DstId + Payload
///   [last 4] CRC32         (over header + payload)
/// 
/// Total overhead: 28 (header) + 4 (CRC) = 32 bytes per frame.
/// </summary>
public readonly struct VctpFrame
{
    public const uint Magic = 0x50544356; // "VCTP" in little-endian
    public const int HeaderSize = 28;
    public const int CrcSize = 4;
    public const int MinFrameSize = HeaderSize + CrcSize; // 32 bytes
    public const uint MaxPayloadSize = 16 * 1024 * 1024; // 16 MB
    public const int MaxIdLength = 255;

    public ushort FrameType { get; }
    public ulong SequenceId { get; }
    public string SourceId { get; }
    public string DestinationId { get; }
    public uint PayloadLength { get; }
    public uint Flags { get; }
    public ReadOnlyMemory<byte> Payload { get; }

    public VctpFrame(ushort frameType, ulong sequenceId, string sourceId, string destinationId, ReadOnlyMemory<byte> payload, uint flags = 0)
    {
        if (sourceId.Length > MaxIdLength) throw new ArgumentException($"Source ID too long: {sourceId.Length} > {MaxIdLength}");
        if (destinationId.Length > MaxIdLength) throw new ArgumentException($"Destination ID too long: {destinationId.Length} > {MaxIdLength}");

        FrameType = frameType;
        SequenceId = sequenceId;
        SourceId = sourceId;
        DestinationId = destinationId;
        PayloadLength = (uint)payload.Length;
        Flags = flags;
        Payload = payload;
    }

    /// <summary>
    /// Encode this frame into a byte array ready for transmission.
    /// Layout: [header:28][srcId:srcLen][dstId:dstLen][payload:payloadLen][crc32:4]
    /// </summary>
    public byte[] Encode()
    {
        var srcBytes = Encoding.UTF8.GetBytes(SourceId);
        var dstBytes = Encoding.UTF8.GetBytes(DestinationId);
        var totalSize = HeaderSize + srcBytes.Length + dstBytes.Length + (int)PayloadLength + CrcSize;
        var buffer = new byte[totalSize];
        var span = buffer.AsSpan();

        // Header
        BinaryPrimitives.WriteUInt32LittleEndian(span[0..4], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..6], FrameType);
        BinaryPrimitives.WriteUInt64LittleEndian(span[6..14], SequenceId);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..16], (ushort)srcBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[16..18], (ushort)dstBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[18..22], PayloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(span[22..26], Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(span[26..28], 0); // Reserved

        // IDs
        var offset = HeaderSize;
        srcBytes.CopyTo(span[offset..]);
        offset += srcBytes.Length;
        dstBytes.CopyTo(span[offset..]);
        offset += dstBytes.Length;

        // Payload
        Payload.Span.CopyTo(span[offset..]);
        offset += (int)PayloadLength;

        // CRC32 over everything before it
        var crc = ComputeCrc32(buffer.AsSpan(0, offset));
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..(offset + 4)], crc);

        return buffer;
    }

    /// <summary>
    /// Try to decode a frame from a byte buffer.
    /// Returns false if the buffer is too small, magic is wrong, or CRC doesn't match.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> buffer, out VctpFrame frame, out int bytesConsumed)
    {
        frame = default;
        bytesConsumed = 0;

        if (buffer.Length < MinFrameSize) return false;

        // Verify magic
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer[0..4]);
        if (magic != Magic) return false;

        // Parse header
        var frameType = BinaryPrimitives.ReadUInt16LittleEndian(buffer[4..6]);
        var sequenceId = BinaryPrimitives.ReadUInt64LittleEndian(buffer[6..14]);
        var srcLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer[14..16]);
        var dstLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer[16..18]);
        var payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(buffer[18..22]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(buffer[22..26]);

        // Sanity checks
        if (srcLen > MaxIdLength || dstLen > MaxIdLength) return false;
        if (payloadLen > MaxPayloadSize) return false;

        var totalSize = HeaderSize + srcLen + dstLen + (int)payloadLen + CrcSize;
        if (buffer.Length < totalSize) return false;

        // Verify CRC32
        var dataEnd = totalSize - CrcSize;
        var expectedCrc = ComputeCrc32(buffer[..dataEnd]);
        var actualCrc = BinaryPrimitives.ReadUInt32LittleEndian(buffer[dataEnd..totalSize]);
        if (expectedCrc != actualCrc) return false;

        // Extract IDs
        var offset = HeaderSize;
        var sourceId = Encoding.UTF8.GetString(buffer.Slice(offset, srcLen));
        offset += srcLen;
        var destId = Encoding.UTF8.GetString(buffer.Slice(offset, dstLen));
        offset += dstLen;

        // Extract payload
        var payload = buffer.Slice(offset, (int)payloadLen).ToArray();

        frame = new VctpFrame(frameType, sequenceId, sourceId, destId, payload, flags);
        bytesConsumed = totalSize;
        return true;
    }

    /// <summary>
    /// Read only the header to determine frame size without parsing the full frame.
    /// Returns total frame size (header + IDs + payload + CRC) or 0 if header is incomplete/invalid.
    /// </summary>
    public static int PeekFrameSize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < HeaderSize) return 0;

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer[0..4]);
        if (magic != Magic) return 0;

        var srcLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer[14..16]);
        var dstLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer[16..18]);
        var payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(buffer[18..22]);

        if (srcLen > MaxIdLength || dstLen > MaxIdLength || payloadLen > MaxPayloadSize) return 0;

        return HeaderSize + srcLen + dstLen + (int)payloadLen + CrcSize;
    }

    /// <summary>CRC32 (ISO 3309 / ITU-T V.42) with polynomial 0xEDB88320.</summary>
    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        return System.IO.Hashing.Crc32.HashToUInt32(data);
    }
}

/// <summary>
/// VCTP frame types for the relay remote bridge.
/// </summary>
public static class VctpFrameTypes
{
    // Connection management
    public const ushort Handshake = 1;
    public const ushort HandshakeAck = 2;
    public const ushort Heartbeat = 3;
    public const ushort Disconnect = 4;

    // Screen/Input
    public const ushort ScreenFrame = 10;
    public const ushort ScreenDelta = 11;     // WebP delta frame
    public const ushort InputEvent = 12;
    public const ushort ClipboardSync = 13;

    // Tool execution
    public const ushort ToolCall = 20;
    public const ushort ToolResult = 21;
    public const ushort ToolCallStream = 22;  // Streaming tool output

    // Data transfer
    public const ushort DataChunk = 30;
    public const ushort DataAck = 31;
    public const ushort FileTransfer = 32;

    // Control
    public const ushort StatusRequest = 40;
    public const ushort StatusResponse = 41;
    public const ushort Error = 50;
}

/// <summary>
/// VCTP frame flags.
/// </summary>
[Flags]
public enum VctpFlags : uint
{
    None = 0,
    /// <summary>Frame requires acknowledgment.</summary>
    RequiresAck = 1 << 0,
    /// <summary>This is an acknowledgment frame.</summary>
    IsAck = 1 << 1,
    /// <summary>Frame is compressed (payload is gzipped).</summary>
    Compressed = 1 << 2,
    /// <summary>Frame is encrypted.</summary>
    Encrypted = 1 << 3,
    /// <summary>This is the last fragment of a multi-frame message.</summary>
    LastFragment = 1 << 4,
}
