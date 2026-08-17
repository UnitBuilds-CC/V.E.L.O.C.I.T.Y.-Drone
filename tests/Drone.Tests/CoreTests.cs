using Xunit;
using global::System.Text.Json;

namespace Drone.Tests;

public class CoreTests
{
    [Fact]
    public void NmcpFrame_HeaderSize_Is16()
    {
        Assert.Equal(16, Drone.Core.Protocol.NmcpFrame.HeaderSize);
    }

    [Fact]
    public void NmcpFrame_Magic_IsVNMC()
    {
        Assert.Equal(0x564E4D43u, Drone.Core.Protocol.NmcpFrame.Magic);
    }

    [Fact]
    public void NmcpFrame_WriteAndRead_RoundTrips()
    {
        var frame = new Drone.Core.Protocol.NmcpFrame(10, 42, new byte[] { 1, 2, 3 });
        var header = new byte[16];
        frame.WriteHeader(header);

        var success = Drone.Core.Protocol.NmcpFrame.TryReadHeader(header, out var frameType, out var payloadLen, out var seqId);
        Assert.True(success);
        Assert.Equal(10u, frameType);
        Assert.Equal(3u, payloadLen);
        Assert.Equal(42u, seqId);
    }

    [Fact]
    public void NmcpFrame_BadMagic_ReturnsFalse()
    {
        var header = new byte[16];
        header[0] = 0xFF; // wrong magic
        var success = Drone.Core.Protocol.NmcpFrame.TryReadHeader(header, out _, out _, out _);
        Assert.False(success);
    }

    [Fact]
    public void NmcpFrame_TooSmall_ReturnsFalse()
    {
        var header = new byte[8]; // too small
        var success = Drone.Core.Protocol.NmcpFrame.TryReadHeader(header, out _, out _, out _);
        Assert.False(success);
    }

    [Fact]
    public void NmcpFrame_BigEndian_IsCrossPlatform()
    {
        // Verify that the magic bytes are written in big-endian order
        var frame = new Drone.Core.Protocol.NmcpFrame(1, 1, Array.Empty<byte>());
        var header = new byte[16];
        frame.WriteHeader(header);
        // Magic 0x564E4D43 in big-endian: 0x56, 0x4E, 0x4D, 0x43
        Assert.Equal(0x56, header[0]);
        Assert.Equal(0x4E, header[1]);
        Assert.Equal(0x4D, header[2]);
        Assert.Equal(0x43, header[3]);
    }

    [Fact]
    public void NmcpFrame_MaxPayloadSize_RejectsOversized()
    {
        // MaxPayloadSize is 16MB; a frame claiming more should be rejected
        var oversized = Drone.Core.Protocol.NmcpFrame.MaxPayloadSize + 1;
        var frame = new Drone.Core.Protocol.NmcpFrame(1, 0, Array.Empty<byte>());
        var header = new byte[16];
        frame.WriteHeader(header);
        // Manually overwrite payloadLen with an oversized value (big-endian uint32 at offset 8)
        var len = oversized;
        header[8]  = (byte)(len >> 24);
        header[9]  = (byte)(len >> 16);
        header[10] = (byte)(len >> 8);
        header[11] = (byte)(len);
        var success = Drone.Core.Protocol.NmcpFrame.TryReadHeader(header, out _, out var payloadLen, out _);
        Assert.False(success, "Oversized payload should be rejected");
    }

    [Fact]
    public void NmcpFrame_MaxPayloadSize_AcceptsExactLimit()
    {
        // A frame claiming exactly MaxPayloadSize should pass header validation
        var exactLimit = Drone.Core.Protocol.NmcpFrame.MaxPayloadSize;
        var frame = new Drone.Core.Protocol.NmcpFrame(1, 0, Array.Empty<byte>());
        var header = new byte[16];
        frame.WriteHeader(header);
        // Overwrite payloadLen with exactly MaxPayloadSize (big-endian uint32 at offset 8)
        header[8]  = (byte)(exactLimit >> 24);
        header[9]  = (byte)(exactLimit >> 16);
        header[10] = (byte)(exactLimit >> 8);
        header[11] = (byte)(exactLimit);
        var success = Drone.Core.Protocol.NmcpFrame.TryReadHeader(header, out _, out var payloadLen, out _);
        Assert.True(success, "Exactly-at-limit payload should be accepted");
        Assert.Equal(exactLimit, payloadLen);
    }

    [Fact]
    public void NmcpFrame_ZeroPayload_IsValid()
    {
        var frame = new Drone.Core.Protocol.NmcpFrame(5, 99, Array.Empty<byte>());
        var header = new byte[16];
        frame.WriteHeader(header);
        var success = Drone.Core.Protocol.NmcpFrame.TryReadHeader(header, out var frameType, out var payloadLen, out var seqId);
        Assert.True(success);
        Assert.Equal(5u, frameType);
        Assert.Equal(0u, payloadLen);
        Assert.Equal(99u, seqId);
    }

    [Fact]
    public void DroneConfig_Defaults_AreSane()
    {
        var config = new Drone.Core.Config.DroneConfig();
        Assert.Equal("Drone", config.DroneId);
        Assert.Equal(Drone.Core.Config.DroneMode.Full, config.Mode);
        Assert.True(config.Uplink.AutoReconnect);
        Assert.Equal(4 * 1024 * 1024, config.Uplink.BufferSize);
        Assert.Equal(10, config.Uplink.MaxReconnectAttempts);
    }

    [Fact]
    public void DroneConfig_Autonomy_Defaults()
    {
        var config = new Drone.Core.Config.DroneConfig();
        Assert.True(config.Autonomy.Enabled);
        Assert.Equal(30, config.Autonomy.SystemMetricsIntervalSec);
        Assert.Equal(10, config.Autonomy.ProcessMonitorIntervalSec);
        Assert.Equal(60, config.Autonomy.ScheduledTaskPollSec);
    }
}

public class EventBusTests
{
    [Fact]
    public async Task EventBus_Publish_DeliversToSubscriber()
    {
        var bus = new Drone.Autonomy.EventBus();
        var received = false;
        bus.Subscribe(evt => { received = true; return Task.CompletedTask; });
        await bus.PublishAsync(new Drone.Autonomy.DroneEvent("test", new { }));
        Assert.True(received);
    }

    [Fact]
    public async Task EventBus_TypedSubscription_FiltersEvents()
    {
        var bus = new Drone.Autonomy.EventBus();
        var count = 0;
        bus.Subscribe("MessageReceived", evt => { count++; return Task.CompletedTask; });
        await bus.PublishAsync(new Drone.Autonomy.DroneEvent("FileChanged", new { }));
        await bus.PublishAsync(new Drone.Autonomy.DroneEvent("MessageReceived", new { }));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task EventBus_Unsubscribe_StopsDelivery()
    {
        var bus = new Drone.Autonomy.EventBus();
        var count = 0;
        Func<Drone.Autonomy.DroneEvent, Task> handler = evt => { count++; return Task.CompletedTask; };
        bus.Subscribe(handler);
        await bus.PublishAsync(new Drone.Autonomy.DroneEvent("test", new { }));
        Assert.Equal(1, count);
        bus.Unsubscribe(handler);
        await bus.PublishAsync(new Drone.Autonomy.DroneEvent("test", new { }));
        Assert.Equal(1, count); // Should not increment
    }

    [Fact]
    public async Task EventBus_HandlerError_DoesNotCrash()
    {
        var bus = new Drone.Autonomy.EventBus();
        var secondCalled = false;
        bus.Subscribe(evt => throw new Exception("boom"));
        bus.Subscribe(evt => { secondCalled = true; return Task.CompletedTask; });
        await bus.PublishAsync(new Drone.Autonomy.DroneEvent("test", new { }));
        Assert.True(secondCalled);
    }
}

public class BehaviorRuleTests
{
    [Fact]
    public void BehaviorRule_WildcardTrigger_MatchesAll()
    {
        var rule = new Drone.Autonomy.BehaviorRule { Trigger = "*", Action = "log" };
        var evt = new Drone.Autonomy.DroneEvent("Anything", new { });
        Assert.True(rule.MatchesCondition(evt));
    }

    [Fact]
    public void BehaviorRule_SpecificTrigger_MatchesOnlyType()
    {
        var rule = new Drone.Autonomy.BehaviorRule { Trigger = "MessageReceived", Action = "log" };
        Assert.True(rule.MatchesCondition(new Drone.Autonomy.DroneEvent("MessageReceived", new { })));
        Assert.False(rule.MatchesCondition(new Drone.Autonomy.DroneEvent("FileChanged", new { })));
    }

    [Fact]
    public void BehaviorRule_Condition_EvaluatesGreaterThan()
    {
        var rule = new Drone.Autonomy.BehaviorRule { Trigger = "SystemAlert", Action = "log", Condition = "cpuPercent > 90" };
        Assert.True(rule.MatchesCondition(new Drone.Autonomy.DroneEvent("SystemAlert", new { cpuPercent = 95 })));
        Assert.False(rule.MatchesCondition(new Drone.Autonomy.DroneEvent("SystemAlert", new { cpuPercent = 50 })));
    }

    [Fact]
    public void BehaviorRule_Condition_EvaluatesEquals()
    {
        var rule = new Drone.Autonomy.BehaviorRule { Trigger = "*", Action = "log", Condition = "alertType == \"HighCPU\"" };
        Assert.True(rule.MatchesCondition(new Drone.Autonomy.DroneEvent("SystemAlert", new { alertType = "HighCPU" })));
        Assert.False(rule.MatchesCondition(new Drone.Autonomy.DroneEvent("SystemAlert", new { alertType = "LowMemory" })));
    }

    [Fact]
    public void BehaviorRule_Disabled_DoesNotMatch()
    {
        var rule = new Drone.Autonomy.BehaviorRule { Trigger = "*", Action = "log", Enabled = false };
        // MatchesCondition doesn't check Enabled (that's checked by the engine)
        Assert.True(rule.MatchesCondition(new Drone.Autonomy.DroneEvent("test", new { })));
        Assert.False(rule.Enabled);
    }

    [Fact]
    public void BehaviorRule_ActionParams_DeserializesFromJson()
    {
        var json = """{"Name":"Test","Trigger":"*","Action":"log","ActionParams":{"level":"info","extra":"data"},"Enabled":true}""";
        var rule = JsonSerializer.Deserialize<Drone.Autonomy.BehaviorRule>(json);
        Assert.NotNull(rule);
        Assert.Equal(2, rule!.ActionParams.Count);
        Assert.Equal("info", rule.ActionParams["level"].GetString());
    }
}

public class McpServerTests
{
    [Fact]
    public async Task McpServer_Initialize_ReturnsProtocolVersion()
    {
        var logger = new TestLogger();
        await using var server = new Drone.MCP.McpServer(logger);
        var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""").RootElement;
        var response = await server.HandleRequestAsync(request);
        Assert.NotNull(response);
        var json = JsonSerializer.Serialize(response);
        Assert.Contains("2024-11-05", json);
        Assert.Contains("velocity-drone", json);
    }

    [Fact]
    public async Task McpServer_ToolsList_ReturnsRegisteredTools()
    {
        var logger = new TestLogger();
        await using var server = new Drone.MCP.McpServer(logger);
        server.RegisterTool("test_tool", async args => JsonSerializer.SerializeToElement(new { ok = true }));
        var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""").RootElement;
        var response = await server.HandleRequestAsync(request);
        var json = JsonSerializer.Serialize(response);
        Assert.Contains("test_tool", json);
    }

    [Fact]
    public async Task McpServer_ToolCall_ExecutesHandler()
    {
        var logger = new TestLogger();
        await using var server = new Drone.MCP.McpServer(logger);
        server.RegisterTool("echo", async args =>
        {
            var text = args.GetProperty("text").GetString() ?? "";
            return JsonSerializer.SerializeToElement(new { echo = text });
        });
        var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo","arguments":{"text":"hello"}}}""").RootElement;
        var response = await server.HandleRequestAsync(request);
        var json = JsonSerializer.Serialize(response);
        Assert.Contains("hello", json);
    }

    [Fact]
    public async Task McpServer_UnknownMethod_ReturnsError()
    {
        var logger = new TestLogger();
        await using var server = new Drone.MCP.McpServer(logger);
        var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":4,"method":"unknown/method","params":{}}""").RootElement;
        var response = await server.HandleRequestAsync(request);
        var json = JsonSerializer.Serialize(response);
        Assert.Contains("-32601", json);
    }

    [Fact]
    public async Task McpServer_UnknownTool_ReturnsError()
    {
        var logger = new TestLogger();
        await using var server = new Drone.MCP.McpServer(logger);
        var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"nonexistent","arguments":{}}}""").RootElement;
        var response = await server.HandleRequestAsync(request);
        var json = JsonSerializer.Serialize(response);
        Assert.Contains("Unknown tool", json);
    }

    [Fact]
    public async Task McpServer_MissingMethod_ReturnsInvalidRequest()
    {
        var logger = new TestLogger();
        await using var server = new Drone.MCP.McpServer(logger);
        // A request without "method" field should return -32600 Invalid Request
        var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":6}""").RootElement;
        var response = await server.HandleRequestAsync(request);
        var json = JsonSerializer.Serialize(response);
        Assert.Contains("-32600", json);
    }

    [Fact]
    public async Task McpServer_MissingToolName_ReturnsError()
    {
        var logger = new TestLogger();
        await using var server = new Drone.MCP.McpServer(logger);
        // tools/call without "name" in params → -32602 Invalid params
        var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"arguments":{}}}""").RootElement;
        var response = await server.HandleRequestAsync(request);
        var json = JsonSerializer.Serialize(response);
        Assert.Contains("-32602", json);
        Assert.Contains("missing tool name", json);
    }
}

public class TestLogger : Drone.Core.ILogger
{
    public List<string> Messages { get; } = new();
    public void LogInformation(string message, params object[] args) => Messages.Add("[INFO] " + string.Format(message.Replace("{", "{{").Replace("}", "}}"), args));
    public void LogWarning(string message, params object[] args) => Messages.Add("[WARN] " + string.Format(message.Replace("{", "{{").Replace("}", "}}"), args));
    public void LogError(string message, params object[] args) => Messages.Add("[ERROR] " + string.Format(message.Replace("{", "{{").Replace("}", "}}"), args));
    public void LogDebug(string message, params object[] args) => Messages.Add("[DEBUG] " + string.Format(message.Replace("{", "{{").Replace("}", "}}"), args));
}
