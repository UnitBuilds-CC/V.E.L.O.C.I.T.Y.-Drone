using Xunit;
using Drone.Core.Custody;
using Drone.Custody;

namespace Drone.Tests;

public class CustodyLogStoreTests : IDisposable
{
    private readonly string _tempPath;
    private readonly CustodyLogStore _store;

    public CustodyLogStoreTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"custody-test-{Guid.NewGuid():N}");
        _store = new CustodyLogStore(_tempPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempPath, true); } catch { }
    }

    private CustodyRecord MakeRecord(string droneId, long seq, string prevHash)
    {
        var record = new CustodyRecord
        {
            DroneId = droneId,
            Sequence = seq,
            PrevHash = prevHash,
            Timestamp = DateTime.UtcNow,
            EventType = "tool_call",
            Action = "test_action",
            Success = true
        };
        record.Seal();
        if (seq > 1) record.PrevHash = prevHash;
        return record;
    }

    [Fact]
    public void StoreRecords_AcceptsValidRecord()
    {
        var record = MakeRecord("drone-1", 1, "");
        var (accepted, rejected) = _store.StoreRecords(new[] { record });
        Assert.Equal(1, accepted);
        Assert.Equal(0, rejected);
        Assert.Equal(1, _store.TotalRecords);
    }

    [Fact]
    public void StoreRecords_RejectsInvalidHash()
    {
        var record = MakeRecord("drone-1", 1, "");
        record.Hash = "TAMPERED";
        var (accepted, rejected) = _store.StoreRecords(new[] { record });
        Assert.Equal(0, accepted);
        Assert.Equal(1, rejected);
    }

    [Fact]
    public void StoreRecords_RejectsBrokenChain()
    {
        var r1 = MakeRecord("drone-1", 1, "");
        _store.StoreRecords(new[] { r1 });

        var r2 = MakeRecord("drone-1", 3, ""); // Skips seq 2
        var (accepted, rejected) = _store.StoreRecords(new[] { r2 });
        Assert.Equal(0, accepted);
        Assert.Equal(1, rejected);
    }

    [Fact]
    public void StoreRecords_MultipleDrones()
    {
        var r1 = MakeRecord("drone-1", 1, "");
        var r2 = MakeRecord("drone-2", 1, "");
        _store.StoreRecords(new[] { r1, r2 });

        Assert.Equal(2, _store.TotalRecords);
        Assert.Equal(2, _store.DroneCount);
        Assert.Contains("drone-1", _store.GetDroneIds());
        Assert.Contains("drone-2", _store.GetDroneIds());
    }

    [Fact]
    public void GetDroneRecords_ReturnsCorrectRecords()
    {
        var r1 = MakeRecord("drone-1", 1, "");
        var r2 = MakeRecord("drone-1", 2, r1.Hash);
        _store.StoreRecords(new[] { r1, r2 });

        var records = _store.GetDroneRecords("drone-1");
        Assert.Equal(2, records.Length);
        Assert.Equal(1, records[0].Sequence);
        Assert.Equal(2, records[1].Sequence);
    }

    [Fact]
    public void GetRecordsByEventType_Filters()
    {
        var r1 = MakeRecord("drone-1", 1, "");
        r1.EventType = "security";
        r1.Seal();

        var r2 = MakeRecord("drone-1", 2, r1.Hash);
        r2.EventType = "tool_call";

        _store.StoreRecords(new[] { r1, r2 });

        var securityRecords = _store.GetRecordsByEventType("security");
        Assert.Single(securityRecords);
    }

    [Fact]
    public void GetLastRecord_ReturnsLatest()
    {
        var r1 = MakeRecord("drone-1", 1, "");
        var r2 = MakeRecord("drone-1", 2, r1.Hash);
        _store.StoreRecords(new[] { r1, r2 });

        var last = _store.GetLastRecord("drone-1");
        Assert.NotNull(last);
        Assert.Equal(2, last!.Sequence);
    }

    [Fact]
    public void GetLastRecord_ReturnsNullForUnknown()
    {
        Assert.Null(_store.GetLastRecord("nonexistent"));
    }
}

public class CustodyQueryEngineTests : IDisposable
{
    private readonly string _tempPath;
    private readonly CustodyLogStore _store;
    private readonly CustodyQueryEngine _engine;

    public CustodyQueryEngineTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"custody-query-test-{Guid.NewGuid():N}");
        _store = new CustodyLogStore(_tempPath);
        _engine = new CustodyQueryEngine(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempPath, true); } catch { }
    }

    private CustodyRecord MakeRecord(string droneId, long seq, string prevHash, string eventType = "tool_call")
    {
        var record = new CustodyRecord
        {
            DroneId = droneId,
            Sequence = seq,
            PrevHash = prevHash,
            Timestamp = DateTime.UtcNow,
            EventType = eventType,
            Action = "test",
            Success = true
        };
        record.Seal();
        return record;
    }

    [Fact]
    public void Query_ByDroneId_ReturnsRecords()
    {
        var r1 = MakeRecord("drone-1", 1, "");
        _store.StoreRecords(new[] { r1 });

        var results = _engine.Query(droneId: "drone-1");
        Assert.Single(results);
    }

    [Fact]
    public void Query_ByEventType_Filters()
    {
        var r1 = MakeRecord("drone-1", 1, "", "security");
        var r2 = MakeRecord("drone-1", 2, r1.Hash, "tool_call");
        _store.StoreRecords(new[] { r1, r2 });

        var results = _engine.Query(eventType: "security");
        Assert.Single(results);
        Assert.Equal("security", results[0].EventType);
    }

    [Fact]
    public void Query_WithLimit_Truncates()
    {
        var r1 = MakeRecord("drone-1", 1, "");
        var r2 = MakeRecord("drone-1", 2, r1.Hash);
        var r3 = MakeRecord("drone-1", 3, r2.Hash);
        _store.StoreRecords(new[] { r1, r2, r3 });

        var results = _engine.Query(droneId: "drone-1", limit: 2);
        Assert.Equal(2, results.Length);
    }

    [Fact]
    public void GetVerifiedDroneTrail_ValidChain()
    {
        var r1 = MakeRecord("drone-1", 1, "");
        var r2 = MakeRecord("drone-1", 2, r1.Hash);
        _store.StoreRecords(new[] { r1, r2 });

        var (records, chainValid) = _engine.GetVerifiedDroneTrail("drone-1");
        Assert.True(chainValid);
        Assert.Equal(2, records.Length);
    }

    [Fact]
    public void GetSummary_ReturnsCorrectCounts()
    {
        var r1 = MakeRecord("drone-1", 1, "");
        var r2 = MakeRecord("drone-2", 1, "");
        _store.StoreRecords(new[] { r1, r2 });

        var summary = _engine.GetSummary();
        Assert.Equal(2, summary.TotalRecords);
        Assert.Equal(2, summary.DroneCount);
    }
}
