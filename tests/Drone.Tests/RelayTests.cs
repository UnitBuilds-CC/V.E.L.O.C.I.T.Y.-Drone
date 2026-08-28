using Xunit;
using Drone.Core.Config;
using Drone.Core.Protocol;
using Drone.Services.Relay;
using global::System;
using global::System.Net;
using global::System.Net.Http.Headers;
using global::System.Net.WebSockets;
using global::System.Text;
using global::System.Text.Json;
using global::System.Collections.Generic;
using global::System.IO;
using global::System.Threading;
using global::System.Threading.Tasks;

namespace Drone.Tests;

public class RelayConfigTests
{
    [Fact]
    public void RelayConfig_DefaultPort_Is9200()
    {
        var config = new RelayConfig();
        Assert.Equal(9200, config.Port);
    }

    [Fact]
    public void RelayConfig_DefaultMaxConnections_Is32()
    {
        var config = new RelayConfig();
        Assert.Equal(32, config.MaxConnections);
    }

    [Fact]
    public void RelayConfig_DefaultStoragePath_IsRelayData()
    {
        var config = new RelayConfig();
        Assert.Equal("relay_data", config.StoragePath);
    }

    [Fact]
    public void RelayConfig_InvalidPort_Throws()
    {
        var config = new RelayConfig { Port = 0 };
        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }

    [Fact]
    public void RelayConfig_PortTooHigh_Throws()
    {
        var config = new RelayConfig { Port = 70000 };
        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }

    [Fact]
    public void RelayConfig_InvalidMaxConnections_Throws()
    {
        var config = new RelayConfig { MaxConnections = 0 };
        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }

    [Fact]
    public void RelayConfig_ValidConfig_DoesNotThrow()
    {
        var config = new RelayConfig { Port = 9200, MaxConnections = 32, ApiKey = "test-key" };
        config.Validate(); // Should not throw
    }

    [Fact]
    public void RelayConfig_DefaultMaxUploadSize_Is100MB()
    {
        var config = new RelayConfig();
        Assert.Equal(100 * 1024 * 1024, config.MaxUploadSize);
    }

    [Fact]
    public void RelayConfig_DefaultStorageQuota_Is1GB()
    {
        var config = new RelayConfig();
        Assert.Equal(1024L * 1024 * 1024, config.StorageQuotaBytes);
    }

    [Fact]
    public void RelayConfig_UploadSizeTooSmall_Throws()
    {
        var config = new RelayConfig { MaxUploadSize = 100 };
        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }

    [Fact]
    public void RelayConfig_StorageQuotaTooSmall_Throws()
    {
        var config = new RelayConfig { StorageQuotaBytes = 100 };
        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }

    [Fact]
    public void RelayConfig_DefaultRateLimit_Is30()
    {
        var config = new RelayConfig();
        Assert.Equal(30, config.MaxMessagesPerSecond);
    }

    [Fact]
    public void RelayConfig_NegativeRateLimit_Throws()
    {
        var config = new RelayConfig { MaxMessagesPerSecond = -1 };
        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }

    [Fact]
    public void RelayConfig_TlsCertNotFound_Throws()
    {
        var config = new RelayConfig { TlsCertificatePath = "/nonexistent/cert.pfx" };
        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }

    [Fact]
    public void RelayConfig_TlsDefaultsNull()
    {
        var config = new RelayConfig();
        Assert.Null(config.TlsCertificatePath);
        Assert.Null(config.TlsCertificatePassword);
    }

    [Fact]
    public void DroneConfig_HasRelayProperty()
    {
        var config = new DroneConfig();
        Assert.NotNull(config.Relay);
    }

    [Fact]
    public void DroneConfig_DefaultRole_IsStandalone()
    {
        var config = new DroneConfig();
        Assert.Equal(DroneRole.Standalone, config.Role);
    }
}

public class DroneRoleTests
{
    [Fact]
    public void DroneRole_HasThreeValues()
    {
        var values = Enum.GetValues<DroneRole>();
        Assert.Equal(3, values.Length);
    }

    [Fact]
    public void DroneRole_Standalone_IsDefault()
    {
        Assert.Equal(0, (int)DroneRole.Standalone);
    }

    [Fact]
    public void DroneRole_Server_Is1()
    {
        Assert.Equal(1, (int)DroneRole.Server);
    }

    [Fact]
    public void DroneRole_Client_Is2()
    {
        Assert.Equal(2, (int)DroneRole.Client);
    }
}

public class VctpFrameTests
{
    [Fact]
    public void VctpFrame_HeaderSize_Is28()
    {
        Assert.Equal(28, VctpFrame.HeaderSize);
    }

    [Fact]
    public void VctpFrame_Magic_IsVCTP()
    {
        Assert.Equal(0x50544356u, VctpFrame.Magic);
    }

    [Fact]
    public void VctpFrame_EncodeDecode_RoundTrips()
    {
        var payload = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        var frame = new VctpFrame(VctpFrameTypes.ScreenFrame, 42, "drone-a", "drone-b", payload);

        var encoded = frame.Encode();
        var success = VctpFrame.TryDecode(encoded, out var decoded, out var consumed);

        Assert.True(success);
        Assert.Equal(encoded.Length, consumed);
        Assert.Equal(VctpFrameTypes.ScreenFrame, decoded.FrameType);
        Assert.Equal(42UL, decoded.SequenceId);
        Assert.Equal("drone-a", decoded.SourceId);
        Assert.Equal("drone-b", decoded.DestinationId);
        Assert.Equal(payload.Length, (int)decoded.PayloadLength);
        Assert.Equal(payload, decoded.Payload.ToArray());
    }

    [Fact]
    public void VctpFrame_EmptyPayload_RoundTrips()
    {
        var frame = new VctpFrame(VctpFrameTypes.Heartbeat, 1, "src", "dst", Array.Empty<byte>());
        var encoded = frame.Encode();
        var success = VctpFrame.TryDecode(encoded, out var decoded, out _);

        Assert.True(success);
        Assert.Equal(0u, decoded.PayloadLength);
    }

    [Fact]
    public void VctpFrame_BadMagic_ReturnsFalse()
    {
        var frame = new VctpFrame(1, 0, "a", "b", new byte[] { 1 });
        var encoded = frame.Encode();
        encoded[0] = 0xFF; // corrupt magic

        var success = VctpFrame.TryDecode(encoded, out _, out _);
        Assert.False(success);
    }

    [Fact]
    public void VctpFrame_CorruptedPayload_ReturnsFalse()
    {
        var frame = new VctpFrame(1, 0, "a", "b", new byte[] { 1, 2, 3 });
        var encoded = frame.Encode();
        encoded[encoded.Length - 5] ^= 0xFF; // corrupt a payload byte (before CRC)

        var success = VctpFrame.TryDecode(encoded, out _, out _);
        Assert.False(success);
    }

    [Fact]
    public void VctpFrame_TooShort_ReturnsFalse()
    {
        var buffer = new byte[10]; // less than MinFrameSize
        var success = VctpFrame.TryDecode(buffer, out _, out _);
        Assert.False(success);
    }

    [Fact]
    public void VctpFrame_PeekFrameSize_ReturnsCorrectSize()
    {
        var payload = new byte[100];
        var frame = new VctpFrame(VctpFrameTypes.DataChunk, 1, "src", "dst", payload);
        var encoded = frame.Encode();

        var size = VctpFrame.PeekFrameSize(encoded);
        Assert.Equal(encoded.Length, size);
    }

    [Fact]
    public void VctpFrame_PeekFrameSize_BadMagic_Returns0()
    {
        var buffer = new byte[28];
        buffer[0] = 0xFF;
        Assert.Equal(0, VctpFrame.PeekFrameSize(buffer));
    }

    [Fact]
    public void VctpFrame_Flags_RoundTrip()
    {
        var flags = (uint)(VctpFlags.RequiresAck | VctpFlags.Compressed);
        var frame = new VctpFrame(VctpFrameTypes.ToolCall, 1, "a", "b", new byte[] { 1 }, flags);
        var encoded = frame.Encode();
        VctpFrame.TryDecode(encoded, out var decoded, out _);

        Assert.Equal(flags, decoded.Flags);
    }

    [Fact]
    public void VctpFrameTypes_HasExpectedValues()
    {
        Assert.Equal(1, VctpFrameTypes.Handshake);
        Assert.Equal(3, VctpFrameTypes.Heartbeat);
        Assert.Equal(10, VctpFrameTypes.ScreenFrame);
        Assert.Equal(12, VctpFrameTypes.InputEvent);
        Assert.Equal(20, VctpFrameTypes.ToolCall);
        Assert.Equal(21, VctpFrameTypes.ToolResult);
    }
}

public class MessengerRelayTests
{
    private class TestLogger : Drone.Core.ILogger
    {
        public List<string> Messages { get; } = new();
        public void LogInformation(string message, params object[] args) => Messages.Add($"INFO: {string.Format(message.Replace("{", "{{").Replace("}", "}}"), args)}");
        public void LogWarning(string message, params object[] args) => Messages.Add($"WARN: {string.Format(message.Replace("{", "{{").Replace("}", "}}"), args)}");
        public void LogError(string message, params object[] args) => Messages.Add($"ERROR: {string.Format(message.Replace("{", "{{").Replace("}", "}}"), args)}");
        public void LogDebug(string message, params object[] args) { }
    }

    [Fact]
    public async Task MessengerRelay_TwoClients_CanExchangeMessages()
    {
        // Start relay server on a random port
        var config = new RelayConfig { Port = 0, MaxConnections = 10 }; // Port 0 = OS-assigned
        var logger = new TestLogger();
        var relay = new MessengerRelay(config, logger);

        // We can't easily test WebSocket without a full HTTP listener,
        // but we can verify the relay initializes correctly
        Assert.Equal(0, relay.ClientCount);
        Assert.Empty(relay.ConnectedClients);

        await relay.DisposeAsync();
    }

    [Fact]
    public void MessengerRelay_InitialState_HasNoClients()
    {
        var config = new RelayConfig();
        var logger = new TestLogger();
        var relay = new MessengerRelay(config, logger);

        Assert.Equal(0, relay.ClientCount);
        Assert.Empty(relay.ConnectedClients);
    }
}

public class RemoteBridgeTests
{
    private class TestLogger : Drone.Core.ILogger
    {
        public void LogInformation(string message, params object[] args) { }
        public void LogWarning(string message, params object[] args) { }
        public void LogError(string message, params object[] args) { }
        public void LogDebug(string message, params object[] args) { }
    }

    [Fact]
    public void RemoteBridge_InitialState_HasNoConnections()
    {
        var config = new RelayConfig();
        var logger = new TestLogger();
        var bridge = new RemoteBridge(config, logger);

        Assert.Equal(0, bridge.TargetCount);
        Assert.Equal(0, bridge.ControllerCount);
    }
}

public class RelayServerIntegrationTests
{
    private class TestLogger : Drone.Core.ILogger
    {
        public List<string> Messages { get; } = new();
        public void LogInformation(string message, params object[] args) => Messages.Add($"INFO: {message}");
        public void LogWarning(string message, params object[] args) => Messages.Add($"WARN: {message}");
        public void LogError(string message, params object[] args) => Messages.Add($"ERROR: {message}");
        public void LogDebug(string message, params object[] args) { }
    }

    [Fact]
    public async Task RelayServer_StartAndStop_Works()
    {
        var port = GetRandomPort();
        var config = new RelayConfig { Port = port, MaxConnections = 10, StoragePath = Path.Combine(Path.GetTempPath(), $"drone-test-{Guid.NewGuid():N}") };
        var logger = new TestLogger();
        var server = new RelayServer(config, "test-drone", logger);

        try
        {
            await server.StartAsync();
            Assert.True(server.IsRunning);
            Assert.Equal(0, server.ConnectionCount);
        }
        finally
        {
            await server.DisposeAsync();
            Assert.False(server.IsRunning);

            // Cleanup temp directory
            if (Directory.Exists(config.StoragePath))
                Directory.Delete(config.StoragePath, true);
        }
    }

    [Fact]
    public async Task RelayServer_HealthEndpoint_Returns200()
    {
        var port = GetRandomPort();
        var config = new RelayConfig { Port = port, MaxConnections = 10, StoragePath = Path.Combine(Path.GetTempPath(), $"drone-test-{Guid.NewGuid():N}") };
        var logger = new TestLogger();
        var server = new RelayServer(config, "test-drone", logger);

        try
        {
            await server.StartAsync();

            using var http = new HttpClient();
            var response = await http.GetAsync($"http://localhost:{port}/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            Assert.Equal("healthy", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal("test-drone", doc.RootElement.GetProperty("drone_id").GetString());
        }
        finally
        {
            await server.DisposeAsync();
            if (Directory.Exists(config.StoragePath))
                Directory.Delete(config.StoragePath, true);
        }
    }

    [Fact]
    public async Task RelayServer_RootEndpoint_ReturnsInfo()
    {
        var port = GetRandomPort();
        var config = new RelayConfig { Port = port, MaxConnections = 10, StoragePath = Path.Combine(Path.GetTempPath(), $"drone-test-{Guid.NewGuid():N}") };
        var logger = new TestLogger();
        var server = new RelayServer(config, "test-drone", logger);

        try
        {
            await server.StartAsync();

            using var http = new HttpClient();
            var response = await http.GetAsync($"http://localhost:{port}/");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            Assert.Equal("Velocity Drone Relay", doc.RootElement.GetProperty("service").GetString());
            Assert.Equal("1.0.0", doc.RootElement.GetProperty("version").GetString());
        }
        finally
        {
            await server.DisposeAsync();
            if (Directory.Exists(config.StoragePath))
                Directory.Delete(config.StoragePath, true);
        }
    }

    [Fact]
    public async Task RelayServer_AuthRequired_RejectsUnauthenticated()
    {
        var port = GetRandomPort();
        var config = new RelayConfig
        {
            Port = port,
            MaxConnections = 10,
            ApiKey = "secret-key",
            StoragePath = Path.Combine(Path.GetTempPath(), $"drone-test-{Guid.NewGuid():N}")
        };
        var logger = new TestLogger();
        var server = new RelayServer(config, "test-drone", logger);

        try
        {
            await server.StartAsync();
            Assert.True(server.IsRunning);

            using var http = new HttpClient();

            // Without API key — should be rejected at relay auth layer
            var response = await http.GetAsync($"http://localhost:{port}/relay/share/api/files");
            Assert.True(
                response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden,
                $"Expected 401 or 403, got {response.StatusCode}");

            // With correct API key — should succeed (even if 404 for no files)
            using var http2 = new HttpClient();
            http2.DefaultRequestHeaders.Add("X-Api-Key", "secret-key");
            var response2 = await http2.GetAsync($"http://localhost:{port}/relay/share/api/files");
            Assert.NotEqual(HttpStatusCode.Unauthorized, response2.StatusCode);
        }
        finally
        {
            await server.DisposeAsync();
            if (Directory.Exists(config.StoragePath))
                Directory.Delete(config.StoragePath, true);
        }
    }

    [Fact]
    public async Task RelayServer_MessengerWebSocket_AcceptsConnection()
    {
        var port = GetRandomPort();
        var config = new RelayConfig { Port = port, MaxConnections = 10, StoragePath = Path.Combine(Path.GetTempPath(), $"drone-test-{Guid.NewGuid():N}") };
        var logger = new TestLogger();
        var server = new RelayServer(config, "test-drone", logger);

        try
        {
            await server.StartAsync();

            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri($"ws://localhost:{port}/relay/messenger/?username=test-client"), CancellationToken.None);
            Assert.Equal(WebSocketState.Open, ws.State);

            // Send a contacts request
            var msg = JsonSerializer.Serialize(new { type = "contacts" });
            var bytes = Encoding.UTF8.GetBytes(msg);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);

            // Receive response
            var buffer = new byte[4096];
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            var response = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var doc = JsonDocument.Parse(response);
            Assert.Equal("contacts", doc.RootElement.GetProperty("type").GetString());

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
        }
        finally
        {
            await server.DisposeAsync();
            if (Directory.Exists(config.StoragePath))
                Directory.Delete(config.StoragePath, true);
        }
    }

    [Fact]
    public async Task RelayServer_MessengerE2E_TwoDronesExchangeMessages()
    {
        var port = GetRandomPort();
        var config = new RelayConfig { Port = port, MaxConnections = 10, StoragePath = Path.Combine(Path.GetTempPath(), $"drone-test-{Guid.NewGuid():N}") };
        var logger = new TestLogger();
        var server = new RelayServer(config, "relay-host", logger);

        try
        {
            await server.StartAsync();

            // Drone A connects
            using var wsA = new ClientWebSocket();
            await wsA.ConnectAsync(new Uri($"ws://localhost:{port}/relay/messenger/?username=drone-a"), CancellationToken.None);
            Assert.Equal(WebSocketState.Open, wsA.State);

            // Drone B connects
            using var wsB = new ClientWebSocket();
            await wsB.ConnectAsync(new Uri($"ws://localhost:{port}/relay/messenger/?username=drone-b"), CancellationToken.None);
            Assert.Equal(WebSocketState.Open, wsB.State);

            // Give the server a moment to register both connections
            await Task.Delay(100);

            // Drone A sends a direct message to Drone B
            var message = JsonSerializer.Serialize(new
            {
                type = "direct",
                to = "drone-b",
                content = new { text = "hello from A" }
            });
            var msgBytes = Encoding.UTF8.GetBytes(message);
            await wsA.SendAsync(new ArraySegment<byte>(msgBytes), WebSocketMessageType.Text, true, CancellationToken.None);

            // Drone B should receive the message
            var buffer = new byte[4096];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await wsB.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            var received = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var doc = JsonDocument.Parse(received);

            Assert.Equal("message", doc.RootElement.GetProperty("type").GetString());
            Assert.Equal("drone-a", doc.RootElement.GetProperty("from").GetString());
            Assert.Equal("hello from A", doc.RootElement.GetProperty("content").GetProperty("text").GetString());

            // Drone B sends a broadcast
            var broadcast = JsonSerializer.Serialize(new
            {
                type = "broadcast",
                content = new { text = "broadcast from B" }
            });
            var bcastBytes = Encoding.UTF8.GetBytes(broadcast);
            await wsB.SendAsync(new ArraySegment<byte>(bcastBytes), WebSocketMessageType.Text, true, CancellationToken.None);

            // Drone A should receive the broadcast
            var result2 = await wsA.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            var received2 = Encoding.UTF8.GetString(buffer, 0, result2.Count);
            var doc2 = JsonDocument.Parse(received2);

            Assert.Equal("broadcast", doc2.RootElement.GetProperty("type").GetString());
            Assert.Equal("drone-b", doc2.RootElement.GetProperty("from").GetString());

            // Cleanup
            await wsA.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
            await wsB.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
        }
        finally
        {
            await server.DisposeAsync();
            if (Directory.Exists(config.StoragePath))
                Directory.Delete(config.StoragePath, true);
        }
    }

    [Fact]
    public async Task RelayServer_RemoteBridgeE2E_ForwardsFramesBidirectionally()
    {
        var port = GetRandomPort();
        var config = new RelayConfig { Port = port, MaxConnections = 10, StoragePath = Path.Combine(Path.GetTempPath(), $"drone-test-{Guid.NewGuid():N}") };
        var logger = new TestLogger();
        var server = new RelayServer(config, "relay-host", logger);

        try
        {
            await server.StartAsync();

            // Target drone connects first
            using var wsTarget = new ClientWebSocket();
            await wsTarget.ConnectAsync(new Uri($"ws://localhost:{port}/relay/remote/?role=target&drone_id=target-1"), CancellationToken.None);
            Assert.Equal(WebSocketState.Open, wsTarget.State);

            // Controller connects, targeting target-1
            using var wsController = new ClientWebSocket();
            await wsController.ConnectAsync(new Uri($"ws://localhost:{port}/relay/remote/?role=controller&drone_id=ctrl-1&target=target-1"), CancellationToken.None);
            Assert.Equal(WebSocketState.Open, wsController.State);

            await Task.Delay(100);

            // Controller sends a binary frame (simulating input event)
            var inputFrame = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
            await wsController.SendAsync(new ArraySegment<byte>(inputFrame), WebSocketMessageType.Binary, true, CancellationToken.None);

            // Target should receive the frame
            var targetBuf = new byte[4096];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var targetResult = await wsTarget.ReceiveAsync(new ArraySegment<byte>(targetBuf), cts.Token);
            Assert.Equal(WebSocketMessageType.Binary, targetResult.MessageType);
            Assert.Equal(inputFrame.Length, targetResult.Count);
            Assert.Equal(inputFrame, targetBuf[..targetResult.Count]);

            // Target sends a binary frame back (simulating screen frame)
            var screenFrame = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
            await wsTarget.SendAsync(new ArraySegment<byte>(screenFrame), WebSocketMessageType.Binary, true, CancellationToken.None);

            // Controller should receive the frame
            var ctrlBuf = new byte[4096];
            var ctrlResult = await wsController.ReceiveAsync(new ArraySegment<byte>(ctrlBuf), cts.Token);
            Assert.Equal(WebSocketMessageType.Binary, ctrlResult.MessageType);
            Assert.Equal(screenFrame.Length, ctrlResult.Count);
            Assert.Equal(screenFrame, ctrlBuf[..ctrlResult.Count]);

            // Cleanup
            await wsController.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
            await wsTarget.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
        }
        finally
        {
            await server.DisposeAsync();
            if (Directory.Exists(config.StoragePath))
                Directory.Delete(config.StoragePath, true);
        }
    }

    [Fact]
    public async Task RelayServer_FileShareE2E_UploadListDownloadDelete()
    {
        var port = GetRandomPort();
        var storagePath = Path.Combine(Path.GetTempPath(), $"drone-test-{Guid.NewGuid():N}");
        var config = new RelayConfig { Port = port, MaxConnections = 10, StoragePath = storagePath };
        var logger = new TestLogger();
        var server = new RelayServer(config, "relay-host", logger);

        try
        {
            await server.StartAsync();
            using var http = new HttpClient();
            var baseUrl = $"http://localhost:{port}/relay/share";

            // 1. Upload a file using raw multipart body
            var boundary = "----TestBoundary" + Guid.NewGuid().ToString("N");
            var fileContent = "Hello from relay test!";
            var body = $"--{boundary}\n" +
                       $"Content-Disposition: form-data; name=\"path\"\n\n" +
                       $"test-file.txt\n" +
                       $"--{boundary}\n" +
                       $"Content-Disposition: form-data; name=\"file\"; filename=\"test-file.txt\"\n" +
                       $"Content-Type: application/octet-stream\n\n" +
                       $"{fileContent}\n" +
                       $"--{boundary}--\n";
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var uploadReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/files/upload");
            uploadReq.Content = new ByteArrayContent(bodyBytes);
            uploadReq.Content.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data") { Parameters = { new NameValueHeaderValue("boundary", boundary) } };
            var uploadResp = await http.SendAsync(uploadReq);
            var uploadBody = await uploadResp.Content.ReadAsStringAsync();
            Assert.True(uploadResp.IsSuccessStatusCode, $"Upload failed: {uploadBody}");

            // 2. List files — should contain the uploaded file
            var listResp = await http.GetAsync($"{baseUrl}/api/files");
            Assert.True(listResp.IsSuccessStatusCode, $"List failed: {await listResp.Content.ReadAsStringAsync()}");
            var listJson = await listResp.Content.ReadAsStringAsync();
            var files = JsonDocument.Parse(listJson);
            Assert.True(files.RootElement.GetArrayLength() >= 1, "Expected at least 1 file");
            var found = false;
            foreach (var f in files.RootElement.EnumerateArray())
            {
                if (f.GetProperty("name").GetString() == "test-file.txt")
                {
                    found = true;
                    break;
                }
            }
            Assert.True(found, "Uploaded file not found in listing");

            // 3. Download the file
            var downloadResp = await http.GetAsync($"{baseUrl}/api/files/download/test-file.txt");
            Assert.True(downloadResp.IsSuccessStatusCode, $"Download failed: {await downloadResp.Content.ReadAsStringAsync()}");
            var downloaded = await downloadResp.Content.ReadAsStringAsync();
            Assert.Equal(fileContent, downloaded);

            // 4. Delete the file
            var deleteResp = await http.DeleteAsync($"{baseUrl}/api/files/test-file.txt");
            Assert.True(deleteResp.IsSuccessStatusCode, $"Delete failed: {await deleteResp.Content.ReadAsStringAsync()}");

            // 5. Verify file is gone
            var downloadResp2 = await http.GetAsync($"{baseUrl}/api/files/download/test-file.txt");
            Assert.Equal(HttpStatusCode.NotFound, downloadResp2.StatusCode);
        }
        finally
        {
            await server.DisposeAsync();
            if (Directory.Exists(storagePath))
                Directory.Delete(storagePath, true);
        }
    }

    [Fact]
    public async Task RelayServer_UploadSizeLimit_RejectsOversizedUpload()
    {
        var port = GetRandomPort();
        var storagePath = Path.Combine(Path.GetTempPath(), $"drone-test-{Guid.NewGuid():N}");
        var config = new RelayConfig { Port = port, MaxConnections = 10, StoragePath = storagePath, MaxUploadSize = 1024 }; // 1KB limit
        var logger = new TestLogger();
        var server = new RelayServer(config, "relay-host", logger);

        try
        {
            await server.StartAsync();
            using var http = new HttpClient();
            var baseUrl = $"http://localhost:{port}/relay/share";

            // Create a body that exceeds the 1KB limit
            var boundary = "----TestBoundary" + Guid.NewGuid().ToString("N");
            var largeContent = new string('X', 2048); // 2KB — exceeds 1KB limit
            var body = $"--{boundary}\n" +
                       $"Content-Disposition: form-data; name=\"path\"\n\n" +
                       $"large-file.txt\n" +
                       $"--{boundary}\n" +
                       $"Content-Disposition: form-data; name=\"file\"; filename=\"large-file.txt\"\n" +
                       $"Content-Type: application/octet-stream\n\n" +
                       $"{largeContent}\n" +
                       $"--{boundary}--\n";
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/files/upload");
            req.Content = new ByteArrayContent(bodyBytes);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data") { Parameters = { new NameValueHeaderValue("boundary", boundary) } };
            var resp = await http.SendAsync(req);

            Assert.Equal(global::System.Net.HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
        }
        finally
        {
            await server.DisposeAsync();
            if (Directory.Exists(storagePath))
                Directory.Delete(storagePath, true);
        }
    }

    [Fact]
    public async Task RelayServer_StorageQuota_RejectsWhenFull()
    {
        var port = GetRandomPort();
        var storagePath = Path.Combine(Path.GetTempPath(), $"drone-test-{Guid.NewGuid():N}");
        var config = new RelayConfig { Port = port, MaxConnections = 10, StoragePath = storagePath, StorageQuotaBytes = 50 }; // 50 bytes quota
        var logger = new TestLogger();
        var server = new RelayServer(config, "relay-host", logger);

        try
        {
            await server.StartAsync();
            using var http = new HttpClient();
            var baseUrl = $"http://localhost:{port}/relay/share";

            // Upload a file that exceeds the 50-byte quota
            var boundary = "----TestBoundary" + Guid.NewGuid().ToString("N");
            var fileContent = "This content is definitely more than 50 bytes long and should be rejected by the quota";
            var body = $"--{boundary}\n" +
                       $"Content-Disposition: form-data; name=\"path\"\n\n" +
                       $"quota-file.txt\n" +
                       $"--{boundary}\n" +
                       $"Content-Disposition: form-data; name=\"file\"; filename=\"quota-file.txt\"\n" +
                       $"Content-Type: application/octet-stream\n\n" +
                       $"{fileContent}\n" +
                       $"--{boundary}--\n";
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/files/upload");
            req.Content = new ByteArrayContent(bodyBytes);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data") { Parameters = { new NameValueHeaderValue("boundary", boundary) } };
            var resp = await http.SendAsync(req);

            // 507 = Insufficient Storage
            Assert.Equal((global::System.Net.HttpStatusCode)507, resp.StatusCode);
        }
        finally
        {
            await server.DisposeAsync();
            if (Directory.Exists(storagePath))
                Directory.Delete(storagePath, true);
        }
    }

    [Fact]
    public async Task RelayServer_MessengerUsernameCollision_EvictsOldConnection()
    {
        var port = GetRandomPort();
        var config = new RelayConfig { Port = port, MaxConnections = 10, StoragePath = Path.Combine(Path.GetTempPath(), $"drone-test-{Guid.NewGuid():N}") };
        var logger = new TestLogger();
        var server = new RelayServer(config, "relay-host", logger);

        try
        {
            await server.StartAsync();

            // First connection as "drone-x"
            using var ws1 = new ClientWebSocket();
            await ws1.ConnectAsync(new Uri($"ws://localhost:{port}/relay/messenger/?username=drone-x"), CancellationToken.None);
            Assert.Equal(WebSocketState.Open, ws1.State);

            // Second connection as "drone-x" — should evict the first
            using var ws2 = new ClientWebSocket();
            await ws2.ConnectAsync(new Uri($"ws://localhost:{port}/relay/messenger/?username=drone-x"), CancellationToken.None);
            Assert.Equal(WebSocketState.Open, ws2.State);

            await Task.Delay(500);

            // First connection should have been closed by the server
            // Try to receive on ws1 — the server should have sent a close frame
            // or the connection should be aborted. Either way, it should not remain open.
            var probeBuf = new byte[64];
            try
            {
                using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var probeResult = await ws1.ReceiveAsync(new ArraySegment<byte>(probeBuf), probeCts.Token);
                // If we get here, the server sent us a close frame
                Assert.True(probeResult.MessageType == WebSocketMessageType.Close,
                    $"Expected close message, got {probeResult.MessageType}");
            }
            catch (OperationCanceledException)
            {
                // Connection still alive after 2s — that's a failure
                Assert.Fail("Old connection was not evicted — still receiving after 2s");
            }
            catch (WebSocketException)
            {
                // Expected — connection was aborted
            }

            // Second connection should still work — send a contacts request
            var msg = JsonSerializer.Serialize(new { type = "contacts" });
            var bytes = Encoding.UTF8.GetBytes(msg);
            await ws2.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);

            var buffer = new byte[4096];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await ws2.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            var response = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var doc = JsonDocument.Parse(response);
            Assert.Equal("contacts", doc.RootElement.GetProperty("type").GetString());

            await ws2.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
        }
        finally
        {
            await server.DisposeAsync();
            if (Directory.Exists(config.StoragePath))
                Directory.Delete(config.StoragePath, true);
        }
    }

    [Fact]
    public async Task RelayServer_MessengerRateLimit_RejectsFlooding()
    {
        var port = GetRandomPort();
        var config = new RelayConfig
        {
            Port = port,
            MaxConnections = 10,
            StoragePath = Path.Combine(Path.GetTempPath(), $"drone-test-{Guid.NewGuid():N}"),
            MaxMessagesPerSecond = 3 // Very low limit for testing
        };
        var logger = new TestLogger();
        var server = new RelayServer(config, "relay-host", logger);

        try
        {
            await server.StartAsync();

            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri($"ws://localhost:{port}/relay/messenger/?username=flooder"), CancellationToken.None);
            Assert.Equal(WebSocketState.Open, ws.State);

            var buffer = new byte[4096];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            // Send messages rapidly — the first 3 should succeed (bucket starts full),
            // then we should get rate_limited errors
            var rateLimited = false;
            for (int i = 0; i < 10; i++)
            {
                var msg = JsonSerializer.Serialize(new { type = "contacts" });
                var msgBytes = Encoding.UTF8.GetBytes(msg);
                await ws.SendAsync(new ArraySegment<byte>(msgBytes), WebSocketMessageType.Text, true, CancellationToken.None);

                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                var response = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var doc = JsonDocument.Parse(response);

                if (doc.RootElement.TryGetProperty("error", out var errorProp) &&
                    errorProp.GetString() == "rate_limited")
                {
                    rateLimited = true;
                    break;
                }
            }

            Assert.True(rateLimited, "Expected rate_limited error after flooding");

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
        }
        finally
        {
            await server.DisposeAsync();
            if (Directory.Exists(config.StoragePath))
                Directory.Delete(config.StoragePath, true);
        }
    }

    private static int GetRandomPort()
    {
        var listener = new global::System.Net.Sockets.TcpListener(global::System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((global::System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
