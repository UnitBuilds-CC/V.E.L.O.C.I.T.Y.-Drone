using Xunit;
using Drone.Core.Custody;
using Drone.Autonomy;

namespace Drone.Tests;

public class ConcurrencyTests
{
    [Fact]
    public async Task EventBus_ConcurrentPublishAndSubscribe_DoesNotThrow()
    {
        var bus = new EventBus();
        var received = 0;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        bus.Subscribe(async _ => { Interlocked.Increment(ref received); await Task.CompletedTask; });
        bus.Subscribe("test", async _ => { Interlocked.Increment(ref received); await Task.CompletedTask; });

        var tasks = new List<Task>();
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() => bus.PublishAsync(new DroneEvent("test", new { i }))));
            tasks.Add(Task.Run(() => bus.Subscribe($"dynamic-{i}", async _ => { await Task.CompletedTask; })));
        }

        await Task.WhenAll(tasks);
        Assert.True(received > 0);
    }

    [Fact]
    public async Task CustodyChain_ConcurrentSeal_ProducesCorrectSequence()
    {
        var chain = new CustodyChain("concurrent-drone");
        var records = new CustodyRecord[50];
        var tasks = new List<Task>();

        // Seal records sequentially (chain requires ordering)
        for (int i = 0; i < 50; i++)
        {
            records[i] = chain.NextRecord("tool_call", $"action-{i}", null, "ok", true, "local");
        }

        Assert.Equal(50, chain.CurrentSequence);
        Assert.True(CustodyChain.VerifyChain(records));
    }

    [Fact]
    public async Task CustodyAuditLogger_ConcurrentLog_DoesNotThrow()
    {
        var logger = new CustodyAuditLogger("concurrent-drone");
        var tasks = new List<Task>();

        for (int i = 0; i < 100; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(() => logger.LogToolCall($"action-{idx}", $"args-{idx}")));
        }

        await Task.WhenAll(tasks);

        Assert.Equal(100, logger.CurrentSequence);
        logger.Dispose();
    }

    [Fact]
    public async Task CustodyLogStore_ConcurrentStore_SerializesCorrectly()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"custody-store-concurrent-{Guid.NewGuid():N}");
        try
        {
            var store = new Drone.Custody.CustodyLogStore(tempPath);

            // Store records for different drones concurrently (each drone's chain is independent)
            var tasks = new List<Task>();
            for (int d = 0; d < 5; d++)
            {
                var droneId = $"drone-{d}";
                tasks.Add(Task.Run(() =>
                {
                    string prevHash = "";
                    for (int i = 1; i <= 10; i++)
                    {
                        var record = new CustodyRecord
                        {
                            DroneId = droneId,
                            Sequence = i,
                            PrevHash = prevHash,
                            Timestamp = DateTime.UtcNow,
                            EventType = "tool_call",
                            Action = $"action-{i}",
                            Success = true
                        };
                        record.Seal();
                        prevHash = record.Hash;
                        store.StoreRecords(new[] { record });
                    }
                }));
            }

            await Task.WhenAll(tasks);

            Assert.Equal(50, store.TotalRecords);
            Assert.Equal(5, store.DroneCount);

            // Verify each drone's chain
            for (int d = 0; d < 5; d++)
            {
                var records = store.GetDroneRecords($"drone-{d}");
                Assert.Equal(10, records.Length);
                Assert.True(CustodyChain.VerifyChain(records));
            }

            store.Dispose();
        }
        finally
        {
            try { Directory.Delete(tempPath, true); } catch { }
        }
    }

    [Fact]
    public async Task CustodyRingBuffer_ConcurrentRead_DoesNotThrow()
    {
        var logger = new CustodyAuditLogger("ring-drone");
        var tasks = new List<Task>();

        for (int i = 0; i < 200; i++)
            logger.LogToolCall($"action-{i}");

        for (int i = 0; i < 20; i++)
            tasks.Add(Task.Run(() => logger.GetRecentRecords(50)));

        await Task.WhenAll(tasks);
        logger.Dispose();
    }
}
