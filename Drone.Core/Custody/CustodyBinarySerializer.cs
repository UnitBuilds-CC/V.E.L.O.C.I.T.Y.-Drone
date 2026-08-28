using System.Buffers.Binary;
using global::System.Text;
using Drone.Core.Protocol;

namespace Drone.Core.Custody;

/// <summary>
/// Binary serialization for custody records using NMCP Merkle frame format.
/// Each record is serialized as a fixed-width 256-byte structure for zero-allocation parsing.
/// A batch is wrapped in an NMCP Merkle frame: [4-byte magic "NMCP"] [32-byte Merkle root] [records...].
/// </summary>
public static class CustodyBinarySerializer
{
    /// <summary>Fixed size of a single binary-serialized custody record.</summary>
    public const int RecordSize = 256;

    /// <summary>NMCP Merkle frame header: 4 (magic) + 32 (merkle root) = 36 bytes.</summary>
    public const int MerkleHeaderSize = 36;

    // Field offsets within a record
    private const int ContentHashOffset = 0;     // 32 bytes
    private const int PrevHashOffset = 32;       // 32 bytes
    private const int MerkleRootOffset = 64;     // 32 bytes
    private const int TimestampOffset = 96;      // 8 bytes (long, UTC ticks)
    private const int SequenceOffset = 104;      // 8 bytes (long)
    private const int EventTypeOffset = 112;     // 2 bytes (ushort)
    private const int SuccessOffset = 114;       // 1 byte (bool)
    private const int DroneIdOffset = 115;       // 48 bytes (UTF-8, null-padded)
    private const int ActionOffset = 163;        // 48 bytes (UTF-8, null-padded)
    private const int CorrelationIdOffset = 211; // 40 bytes (UTF-8, null-padded)
    private const int TargetSystemOffset = 251;  // 4 bytes (ushort event type enum + 1 byte flags + 1 byte reserved)

    /// <summary>Event type as ushort for binary encoding.</summary>
    private static ushort EncodeEventType(string eventType) => eventType switch
    {
        "tool_call" => 1,
        "connection" => 2,
        "security" => 3,
        "cross_machine" => 4,
        _ => 0
    };

    private static string DecodeEventType(ushort code) => code switch
    {
        1 => "tool_call",
        2 => "connection",
        3 => "security",
        4 => "cross_machine",
        _ => "unknown"
    };

    /// <summary>
    /// Serialize a batch of custody records as an NMCP Merkle frame.
    /// Layout: [4-byte magic "NMCP"] [32-byte Merkle root] [N x 256-byte records]
    /// </summary>
    public static byte[] SerializeBatch(CustodyRecord[] records)
    {
        // Compute Merkle root over the batch
        var merkleRoot = CustodyChain.ComputeBatchMerkleRoot(records);
        var merkleRootBytes = string.IsNullOrEmpty(merkleRoot) ? new byte[32] : Convert.FromHexString(merkleRoot);

        var totalSize = MerkleHeaderSize + (records.Length * RecordSize);
        var buffer = new byte[totalSize];

        // Write NMCP Merkle header
        buffer[0] = 0x4E; // 'N'
        buffer[1] = 0x4D; // 'M'
        buffer[2] = 0x43; // 'C'
        buffer[3] = 0x50; // 'P'
        Buffer.BlockCopy(merkleRootBytes, 0, buffer, 4, 32);

        // Write records
        for (int i = 0; i < records.Length; i++)
        {
            var recordOffset = MerkleHeaderSize + (i * RecordSize);
            SerializeRecord(records[i], buffer, recordOffset);
        }

        return buffer;
    }

    /// <summary>
    /// Deserialize custody records from an NMCP Merkle frame.
    /// Validates the magic bytes and Merkle root.
    /// </summary>
    /// <returns>Deserialized records, or null if the frame is invalid.</returns>
    public static CustodyRecord[]? DeserializeBatch(byte[] data)
    {
        if (data.Length < MerkleHeaderSize)
            return null;

        // Validate magic
        if (data[0] != 0x4E || data[1] != 0x4D || data[2] != 0x43 || data[3] != 0x50)
            return null;

        // Extract Merkle root from header
        var expectedRoot = new byte[32];
        Buffer.BlockCopy(data, 4, expectedRoot, 0, 32);

        // Calculate number of records
        var payloadSize = data.Length - MerkleHeaderSize;
        if (payloadSize % RecordSize != 0)
            return null;

        var recordCount = payloadSize / RecordSize;
        var records = new CustodyRecord[recordCount];

        // Deserialize each record
        for (int i = 0; i < recordCount; i++)
        {
            var recordOffset = MerkleHeaderSize + (i * RecordSize);
            records[i] = DeserializeRecord(data, recordOffset);
        }

        // Verify Merkle root (constant-time comparison)
        var computedRoot = CustodyChain.ComputeBatchMerkleRoot(records);
        var computedRootBytes = string.IsNullOrEmpty(computedRoot) ? new byte[32] : Convert.FromHexString(computedRoot);

        if (!global::System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expectedRoot, computedRootBytes))
            return null;

        return records;
    }

    /// <summary>
    /// Extract the Merkle root from an NMCP Merkle frame without deserializing records.
    /// </summary>
    public static byte[]? ExtractMerkleRoot(byte[] data)
    {
        if (data.Length < MerkleHeaderSize)
            return null;
        if (data[0] != 0x4E || data[1] != 0x4D || data[2] != 0x43 || data[3] != 0x50)
            return null;

        var root = new byte[32];
        Buffer.BlockCopy(data, 4, root, 0, 32);
        return root;
    }

    /// <summary>
    /// Build an NMCP CustodyBinary frame (16-byte Drone NMCP header + Merkle frame payload).
    /// </summary>
    public static byte[] BuildFrame(CustodyRecord[] records, uint sequenceId)
    {
        var merklePayload = SerializeBatch(records);
        var frame = new NmcpFrame(NmcpFrameTypes.CustodyBinary, sequenceId, merklePayload);
        var buffer = new byte[NmcpFrame.HeaderSize + merklePayload.Length];
        frame.WriteHeader(buffer);
        Buffer.BlockCopy(merklePayload, 0, buffer, NmcpFrame.HeaderSize, merklePayload.Length);
        return buffer;
    }

    private static void SerializeRecord(CustodyRecord record, byte[] buffer, int offset)
    {
        // Clear the record area
        Array.Clear(buffer, offset, RecordSize);

        // Content hash (32 bytes)
        if (!string.IsNullOrEmpty(record.Hash))
        {
            var hashBytes = Convert.FromHexString(record.Hash);
            Buffer.BlockCopy(hashBytes, 0, buffer, offset + ContentHashOffset, Math.Min(hashBytes.Length, 32));
        }

        // Prev hash (32 bytes)
        if (!string.IsNullOrEmpty(record.PrevHash))
        {
            var prevBytes = Convert.FromHexString(record.PrevHash);
            Buffer.BlockCopy(prevBytes, 0, buffer, offset + PrevHashOffset, Math.Min(prevBytes.Length, 32));
        }

        // Merkle root (32 bytes)
        if (!string.IsNullOrEmpty(record.MerkleRoot))
        {
            var rootBytes = Convert.FromHexString(record.MerkleRoot);
            Buffer.BlockCopy(rootBytes, 0, buffer, offset + MerkleRootOffset, Math.Min(rootBytes.Length, 32));
        }

        // Timestamp (8 bytes, UTC ticks)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset + TimestampOffset), record.Timestamp.Ticks);

        // Sequence (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset + SequenceOffset), record.Sequence);

        // Event type (2 bytes)
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + EventTypeOffset), EncodeEventType(record.EventType));

        // Success (1 byte)
        buffer[offset + SuccessOffset] = record.Success ? (byte)1 : (byte)0;

        // DroneId (48 bytes, UTF-8, null-padded)
        WriteFixedString(record.DroneId, buffer, offset + DroneIdOffset, 48);

        // Action (48 bytes, UTF-8, null-padded)
        WriteFixedString(record.Action, buffer, offset + ActionOffset, 48);

        // CorrelationId (40 bytes, UTF-8, null-padded)
        WriteFixedString(record.CorrelationId ?? "", buffer, offset + CorrelationIdOffset, 40);
    }

    private static CustodyRecord DeserializeRecord(byte[] buffer, int offset)
    {
        var record = new CustodyRecord
        {
            Hash = ReadHexString(buffer, offset + ContentHashOffset, 32),
            PrevHash = ReadHexString(buffer, offset + PrevHashOffset, 32),
            MerkleRoot = ReadHexString(buffer, offset + MerkleRootOffset, 32),
            Timestamp = new DateTime(BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset + TimestampOffset)), DateTimeKind.Utc),
            Sequence = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset + SequenceOffset)),
            EventType = DecodeEventType(BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + EventTypeOffset))),
            Success = buffer[offset + SuccessOffset] != 0,
            DroneId = ReadFixedString(buffer, offset + DroneIdOffset, 48),
            Action = ReadFixedString(buffer, offset + ActionOffset, 48),
            CorrelationId = ReadFixedString(buffer, offset + CorrelationIdOffset, 40),
            EventId = "" // Will be reconstructed from DroneId + Sequence
        };

        record.EventId = $"{record.DroneId}:{record.Sequence}";
        return record;
    }

    private static void WriteFixedString(string value, byte[] buffer, int offset, int maxLen)
    {
        if (string.IsNullOrEmpty(value)) return;
        var bytes = Encoding.UTF8.GetBytes(value);
        var len = Math.Min(bytes.Length, maxLen);
        Buffer.BlockCopy(bytes, 0, buffer, offset, len);
    }

    private static string ReadFixedString(byte[] buffer, int offset, int maxLen)
    {
        // Find null terminator
        var end = offset;
        while (end < offset + maxLen && buffer[end] != 0) end++;
        if (end == offset) return "";
        return Encoding.UTF8.GetString(buffer, offset, end - offset);
    }

    private static string ReadHexString(byte[] buffer, int offset, int length)
    {
        // Check if all zeros
        var allZero = true;
        for (int i = offset; i < offset + length; i++)
        {
            if (buffer[i] != 0) { allZero = false; break; }
        }
        if (allZero) return "";
        return Convert.ToHexString(buffer, offset, length);
    }
}
