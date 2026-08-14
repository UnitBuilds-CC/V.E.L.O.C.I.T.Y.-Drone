using Xunit;
using Drone.Core.Custody;
using Drone.Core.Protocol;

namespace Drone.Tests;

/// <summary>
/// Tests for NMCP binary custody serialization — round-trip, Merkle frame parsing,
/// and chain integrity after binary encode/decode.
/// </summary>
public class NmcpBinaryCustodyTests
{
    private static CustodyRecord CreateTestRecord(string droneId, long seq, string action)
    {
        var record = new CustodyRecord
        {
            DroneId = droneId,
            Sequence = seq,
            Timestamp = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc),
            EventType = "tool_call",
            Action = action,
            Success = true,
            CorrelationId = $"corr-test-{seq}",
            EventId = $"{droneId}:{seq}"
        };
        record.Seal();
        return record;
    }

    private static CustodyRecord[] CreateTestBatch(int count)
    {
        var chain = new CustodyChain("test-drone");
        var records = new CustodyRecord[count];
        for (int i = 0; i < count; i++)
        {
            records[i] = chain.NextRecord("tool_call", $"action-{i}");
        }
        return records;
    }

    [Fact]
    public void SerializeBatch_SingleRecord_ProducesValidFrame()
    {
        var records = CreateTestBatch(1);
        var frame = CustodyBinarySerializer.SerializeBatch(records);

        Assert.NotNull(frame);
        Assert.Equal(CustodyBinarySerializer.MerkleHeaderSize + CustodyBinarySerializer.RecordSize, frame.Length);

        // Check NMCP magic
        Assert.Equal(0x4E, frame[0]); // 'N'
        Assert.Equal(0x4D, frame[1]); // 'M'
        Assert.Equal(0x43, frame[2]); // 'C'
        Assert.Equal(0x50, frame[3]); // 'P'
    }

    [Fact]
    public void SerializeBatch_MultipleRecords_CorrectSize()
    {
        var records = CreateTestBatch(5);
        var frame = CustodyBinarySerializer.SerializeBatch(records);

        var expectedSize = CustodyBinarySerializer.MerkleHeaderSize + (5 * CustodyBinarySerializer.RecordSize);
        Assert.Equal(expectedSize, frame.Length);
    }

    [Fact]
    public void DeserializeBatch_RoundTrip_PreservesData()
    {
        var records = CreateTestBatch(3);
        var frame = CustodyBinarySerializer.SerializeBatch(records);
        var restored = CustodyBinarySerializer.DeserializeBatch(frame);

        Assert.NotNull(restored);
        Assert.Equal(3, restored!.Length);

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(records[i].DroneId, restored[i].DroneId);
            Assert.Equal(records[i].Sequence, restored[i].Sequence);
            Assert.Equal(records[i].EventType, restored[i].EventType);
            Assert.Equal(records[i].Action, restored[i].Action);
            Assert.Equal(records[i].Success, restored[i].Success);
            Assert.Equal(records[i].Hash, restored[i].Hash);
            Assert.Equal(records[i].PrevHash, restored[i].PrevHash);
        }
    }

    [Fact]
    public void DeserializeBatch_RoundTrip_PreservesChainIntegrity()
    {
        var records = CreateTestBatch(5);
        var frame = CustodyBinarySerializer.SerializeBatch(records);
        var restored = CustodyBinarySerializer.DeserializeBatch(frame);

        Assert.NotNull(restored);
        // Verify the hash chain is intact after round-trip
        Assert.True(CustodyChain.VerifyChain(restored!));
    }

    [Fact]
    public void DeserializeBatch_VerifiesMerkleRoot()
    {
        var records = CreateTestBatch(3);
        var frame = CustodyBinarySerializer.SerializeBatch(records);

        // Tamper with a record in the frame (after the header)
        frame[CustodyBinarySerializer.MerkleHeaderSize + 10] ^= 0xFF;

        // Deserialization should fail because Merkle root won't match
        var restored = CustodyBinarySerializer.DeserializeBatch(frame);
        Assert.Null(restored);
    }

    [Fact]
    public void DeserializeBatch_InvalidMagic_ReturnsNull()
    {
        var records = CreateTestBatch(1);
        var frame = CustodyBinarySerializer.SerializeBatch(records);

        // Corrupt the magic bytes
        frame[0] = 0x00;
        var restored = CustodyBinarySerializer.DeserializeBatch(frame);
        Assert.Null(restored);
    }

    [Fact]
    public void DeserializeBatch_TooShort_ReturnsNull()
    {
        var restored = CustodyBinarySerializer.DeserializeBatch(new byte[10]);
        Assert.Null(restored);
    }

    [Fact]
    public void DeserializeBatch_BadRecordCount_ReturnsNull()
    {
        var records = CreateTestBatch(1);
        var frame = CustodyBinarySerializer.SerializeBatch(records);

        // Add extra bytes that don't align to RecordSize
        var badFrame = new byte[frame.Length + 100];
        Buffer.BlockCopy(frame, 0, badFrame, 0, frame.Length);

        var restored = CustodyBinarySerializer.DeserializeBatch(badFrame);
        Assert.Null(restored);
    }

    [Fact]
    public void ExtractMerkleRoot_ReturnsRoot()
    {
        var records = CreateTestBatch(3);
        var frame = CustodyBinarySerializer.SerializeBatch(records);

        var root = CustodyBinarySerializer.ExtractMerkleRoot(frame);
        Assert.NotNull(root);
        Assert.Equal(32, root!.Length);

        // Verify it matches the computed root
        var computedRoot = CustodyChain.ComputeBatchMerkleRoot(records);
        var computedBytes = Convert.FromHexString(computedRoot);
        Assert.Equal(computedBytes, root);
    }

    [Fact]
    public void ExtractMerkleRoot_InvalidFrame_ReturnsNull()
    {
        Assert.Null(CustodyBinarySerializer.ExtractMerkleRoot(new byte[10]));
        Assert.Null(CustodyBinarySerializer.ExtractMerkleRoot(new byte[40])); // Wrong magic
    }

    [Fact]
    public void BuildFrame_ProducesValidNmcpFrame()
    {
        var records = CreateTestBatch(2);
        var frameBytes = CustodyBinarySerializer.BuildFrame(records, 42);

        Assert.NotNull(frameBytes);
        Assert.True(frameBytes.Length > NmcpFrame.HeaderSize + CustodyBinarySerializer.MerkleHeaderSize);

        // Parse the NMCP header
        Assert.True(NmcpFrame.TryReadHeader(frameBytes, out var frameType, out var payloadLen, out var seqId));
        Assert.Equal(NmcpFrameTypes.CustodyBinary, frameType);
        Assert.Equal(42u, seqId);

        // Payload should contain the Merkle frame
        Assert.Equal((uint)(frameBytes.Length - NmcpFrame.HeaderSize), payloadLen);
    }

    [Fact]
    public void BuildFrame_PayloadContainsMerkleFrame()
    {
        var records = CreateTestBatch(2);
        var frameBytes = CustodyBinarySerializer.BuildFrame(records, 1);

        // Extract the payload (after 16-byte NMCP header)
        var payload = new byte[frameBytes.Length - NmcpFrame.HeaderSize];
        Buffer.BlockCopy(frameBytes, NmcpFrame.HeaderSize, payload, 0, payload.Length);

        // Payload should start with NMCP Merkle magic
        Assert.Equal(0x4E, payload[0]);
        Assert.Equal(0x4D, payload[1]);
        Assert.Equal(0x43, payload[2]);
        Assert.Equal(0x50, payload[3]);

        // Should deserialize correctly
        var restored = CustodyBinarySerializer.DeserializeBatch(payload);
        Assert.NotNull(restored);
        Assert.Equal(2, restored!.Length);
    }

    [Fact]
    public void BinarySerialization_PreservesTimestamp()
    {
        var chain = new CustodyChain("ts-drone");
        var record = chain.NextRecord("tool_call", "test");
        var originalTicks = record.Timestamp.Ticks;

        var frame = CustodyBinarySerializer.SerializeBatch(new[] { record });
        var restored = CustodyBinarySerializer.DeserializeBatch(frame);

        Assert.NotNull(restored);
        Assert.Equal(originalTicks, restored![0].Timestamp.Ticks);
    }

    [Fact]
    public void BinarySerialization_PreservesCorrelationId()
    {
        var record = CreateTestRecord("corr-drone", 1, "test_action");
        record.CorrelationId = "corr-abcdef123456";

        var frame = CustodyBinarySerializer.SerializeBatch(new[] { record });
        var restored = CustodyBinarySerializer.DeserializeBatch(frame);

        Assert.NotNull(restored);
        Assert.Equal("corr-abcdef123456", restored![0].CorrelationId);
    }

    [Fact]
    public void BinarySerialization_PreservesSuccess()
    {
        var r1 = new CustodyRecord
        {
            DroneId = "flag-drone", Sequence = 1, EventType = "tool_call",
            Action = "success_action", Success = true, PrevHash = "",
            Timestamp = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc),
            EventId = "flag-drone:1"
        };
        r1.Seal();

        var r2 = new CustodyRecord
        {
            DroneId = "flag-drone", Sequence = 2, EventType = "tool_call",
            Action = "fail_action", Success = false, PrevHash = r1.Hash,
            Timestamp = new DateTime(2026, 1, 15, 12, 0, 1, DateTimeKind.Utc),
            EventId = "flag-drone:2"
        };
        r2.Seal();

        var frame = CustodyBinarySerializer.SerializeBatch(new[] { r1, r2 });
        var restored = CustodyBinarySerializer.DeserializeBatch(frame);

        Assert.NotNull(restored);
        Assert.True(restored![0].Success);
        Assert.False(restored[1].Success);
    }

    [Fact]
    public void CustodyBinary_FrameType_Is44()
    {
        Assert.Equal(44u, NmcpFrameTypes.CustodyBinary);
    }

    [Fact]
    public void RecordSize_Is256()
    {
        Assert.Equal(256, CustodyBinarySerializer.RecordSize);
    }

    [Fact]
    public void MerkleHeaderSize_Is36()
    {
        Assert.Equal(36, CustodyBinarySerializer.MerkleHeaderSize);
    }
}
