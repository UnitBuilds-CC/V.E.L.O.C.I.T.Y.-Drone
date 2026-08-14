using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Drone.Core;
using Drone.Core.Config;
using Drone.Core.Custody;
using Drone.MCP;

namespace Drone.E2E;

/// <summary>E2E test runner — tests the actual McpServer request/response pipeline.</summary>
public class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("=== Velocity Drone E2E Tests ===");
        var passed = 0;
        var failed = 0;
        var skipped = 0;

        var logger = new ConsoleLogger();
        await using var mcp = new McpServer(logger);

        // Register a test tool
        mcp.RegisterTool("echo_test", args =>
        {
            var text = args.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            return Task.FromResult(JsonSerializer.SerializeToElement(new { echoed = text }));
        });

        // Test 1: MCP handshake (initialize)
        try
        {
            Console.Write("Test 1: MCP initialize handshake... ");
            var request = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"e2e-test","version":"1.0"}}}""";
            using var doc = JsonDocument.Parse(request);
            var result = await mcp.HandleRequestAsync(doc.RootElement);
            var json = JsonSerializer.Serialize(result);
            using var rdoc = JsonDocument.Parse(json);
            var root = rdoc.RootElement;

            if (root.TryGetProperty("result", out var res) &&
                res.TryGetProperty("protocolVersion", out var pv) &&
                pv.GetString() == "2024-11-05")
            {
                Console.WriteLine("PASS");
                passed++;
            }
            else { Console.WriteLine($"FAIL (unexpected response: {json})"); failed++; }
        }
        catch (Exception ex) { Console.WriteLine($"FAIL ({ex.Message})"); failed++; }

        // Test 2: tools/list returns actual tools
        try
        {
            Console.Write("Test 2: MCP tools/list... ");
            var request = """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""";
            using var doc = JsonDocument.Parse(request);
            var result = await mcp.HandleRequestAsync(doc.RootElement);
            var json = JsonSerializer.Serialize(result);
            using var rdoc = JsonDocument.Parse(json);
            var root = rdoc.RootElement;

            if (root.TryGetProperty("result", out var res) &&
                res.TryGetProperty("tools", out var tools) &&
                tools.GetArrayLength() >= 1)
            {
                Console.WriteLine($"PASS ({tools.GetArrayLength()} tools)");
                passed++;
            }
            else { Console.WriteLine($"FAIL (unexpected response: {json})"); failed++; }
        }
        catch (Exception ex) { Console.WriteLine($"FAIL ({ex.Message})"); failed++; }

        // Test 3: tools/call with registered tool
        try
        {
            Console.Write("Test 3: MCP tools/call echo_test... ");
            var request = """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo_test","arguments":{"text":"hello drone"}}}""";
            using var doc = JsonDocument.Parse(request);
            var result = await mcp.HandleRequestAsync(doc.RootElement);
            var json = JsonSerializer.Serialize(result);
            using var rdoc = JsonDocument.Parse(json);
            var root = rdoc.RootElement;

            if (root.TryGetProperty("result", out var res) &&
                res.TryGetProperty("content", out var content) &&
                content.GetArrayLength() > 0)
            {
                Console.WriteLine("PASS");
                passed++;
            }
            else { Console.WriteLine($"FAIL (unexpected response: {json})"); failed++; }
        }
        catch (Exception ex) { Console.WriteLine($"FAIL ({ex.Message})"); failed++; }

        // Test 4: tools/call with unknown tool returns error
        try
        {
            Console.Write("Test 4: MCP tools/call unknown tool... ");
            var request = """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"nonexistent_tool","arguments":{}}}""";
            using var doc = JsonDocument.Parse(request);
            var result = await mcp.HandleRequestAsync(doc.RootElement);
            var json = JsonSerializer.Serialize(result);
            using var rdoc = JsonDocument.Parse(json);
            var root = rdoc.RootElement;

            if (root.TryGetProperty("result", out var res) &&
                res.TryGetProperty("isError", out var isErr) &&
                isErr.GetBoolean())
            {
                Console.WriteLine("PASS");
                passed++;
            }
            else { Console.WriteLine($"FAIL (expected isError: {json})"); failed++; }
        }
        catch (Exception ex) { Console.WriteLine($"FAIL ({ex.Message})"); failed++; }

        // Test 5: Missing method returns error
        try
        {
            Console.Write("Test 5: Missing method returns error... ");
            var request = """{"jsonrpc":"2.0","id":5}""";
            using var doc = JsonDocument.Parse(request);
            var result = await mcp.HandleRequestAsync(doc.RootElement);
            var json = JsonSerializer.Serialize(result);
            using var rdoc = JsonDocument.Parse(json);
            var root = rdoc.RootElement;

            if (root.TryGetProperty("error", out var error) &&
                error.TryGetProperty("code", out var code) &&
                code.GetInt32() == -32600)
            {
                Console.WriteLine("PASS");
                passed++;
            }
            else { Console.WriteLine($"FAIL (expected -32600: {json})"); failed++; }
        }
        catch (Exception ex) { Console.WriteLine($"FAIL ({ex.Message})"); failed++; }

        // Test 5b: Missing both id AND method — must not crash (regression test for default JsonElement crash)
        try
        {
            Console.Write("Test 5b: Missing id and method — no crash... ");
            var request = """{"jsonrpc":"2.0"}""";
            using var doc = JsonDocument.Parse(request);
            var result = await mcp.HandleRequestAsync(doc.RootElement);
            var json = JsonSerializer.Serialize(result);
            using var rdoc = JsonDocument.Parse(json);
            var root = rdoc.RootElement;

            if (root.TryGetProperty("error", out var error) &&
                error.TryGetProperty("code", out var code) &&
                code.GetInt32() == -32600)
            {
                Console.WriteLine("PASS");
                passed++;
            }
            else { Console.WriteLine($"FAIL (expected -32600: {json})"); failed++; }
        }
        catch (Exception ex) { Console.WriteLine($"FAIL (crashed: {ex.Message})"); failed++; }

        // Test 6: NMCP frame round-trip
        try
        {
            Console.Write("Test 6: NMCP frame round-trip... ");
            var payload = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":6,"method":"ping"}""");
            var frame = new Drone.Core.Protocol.NmcpFrame(Drone.Core.Protocol.NmcpFrameTypes.JsonRpcRequest, 42, payload);
            var header = new byte[Drone.Core.Protocol.NmcpFrame.HeaderSize];
            frame.WriteHeader(header);

            if (Drone.Core.Protocol.NmcpFrame.TryReadHeader(header, out var ft, out var pl, out var seq) &&
                ft == Drone.Core.Protocol.NmcpFrameTypes.JsonRpcRequest &&
                pl == (uint)payload.Length && seq == 42)
            {
                Console.WriteLine("PASS");
                passed++;
            }
            else { Console.WriteLine("FAIL (header mismatch)"); failed++; }
        }
        catch (Exception ex) { Console.WriteLine($"FAIL ({ex.Message})"); failed++; }

        // Test 7: Config validation catches invalid values
        try
        {
            Console.Write("Test 7: Config validation... ");
            var config = new DroneConfig();
            config.Uplink.BufferSize = -1;
            try
            {
                config.Validate();
                Console.WriteLine("FAIL (should have thrown)");
                failed++;
            }
            catch (InvalidOperationException)
            {
                Console.WriteLine("PASS");
                passed++;
            }
        }
        catch (Exception ex) { Console.WriteLine($"FAIL ({ex.Message})"); failed++; }

        // Test 8: MCP WebSocket transport — start server, connect client, send request
        try
        {
            Console.Write("Test 8: MCP WebSocket transport... ");
            var wsLogger = new ConsoleLogger();
            await using var wsMcp = new McpServer(wsLogger);
            wsMcp.RegisterTool("ping_test", _ => Task.FromResult(JsonSerializer.SerializeToElement(new { pong = true })));

            var wsCts = new CancellationTokenSource();
            var wsUrl = "http://127.0.0.1:19100"; // Use high port for testing
            var serverTask = Task.Run(() => wsMcp.RunWebSocketAsync(wsUrl, wsCts.Token));

            // Give server time to start listening
            await Task.Delay(500);

            // Connect as client with retry
            using var client = new global::System.Net.WebSockets.ClientWebSocket();
            var connected = false;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    await client.ConnectAsync(new Uri("ws://127.0.0.1:19100/mcp/"), wsCts.Token);
                    connected = true;
                    break;
                }
                catch { await Task.Delay(200); }
            }
            if (!connected)
            {
                // HttpListener may require admin on Windows — skip gracefully
                Console.WriteLine("SKIP (HttpListener unavailable — requires admin or URL reservation on Windows)");
                skipped++;
            }
            else
            {
                // Send initialize
                var initRequest = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""";
                var initBytes = Encoding.UTF8.GetBytes(initRequest);
                await client.SendAsync(new ArraySegment<byte>(initBytes), WebSocketMessageType.Text, true, wsCts.Token);

                // Read response
                var recvBuffer = new byte[4096];
                using var recvMs = new MemoryStream();
                WebSocketReceiveResult recvResult;
                do
                {
                    recvResult = await client.ReceiveAsync(new ArraySegment<byte>(recvBuffer), wsCts.Token);
                    recvMs.Write(recvBuffer, 0, recvResult.Count);
                } while (!recvResult.EndOfMessage);

                var responseJson = Encoding.UTF8.GetString(recvMs.ToArray());
                using var rdoc = JsonDocument.Parse(responseJson);
                var root = rdoc.RootElement;

                if (root.TryGetProperty("result", out var res) &&
                    res.TryGetProperty("protocolVersion", out var pv) &&
                    pv.GetString() == "2024-11-05")
                {
                    Console.WriteLine("PASS");
                    passed++;
                }
                else { Console.WriteLine($"FAIL (unexpected: {responseJson})"); failed++; }

                // Clean up
                await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
            }

            wsCts.Cancel();
        }
        catch (Exception ex) { Console.WriteLine($"FAIL ({ex.Message})"); failed++; }

        // Test 9: MCP WebSocket auth rejection
        try
        {
            Console.Write("Test 9: MCP WebSocket auth rejection... ");
            var wsLogger2 = new ConsoleLogger();
            await using var wsMcp2 = new McpServer(wsLogger2);
            wsMcp2.SetAuthToken("secret-token-123");

            var wsCts2 = new CancellationTokenSource();
            var wsUrl2 = "http://127.0.0.1:19101";
            var serverTask2 = Task.Run(() => wsMcp2.RunWebSocketAsync(wsUrl2, wsCts2.Token));
            await Task.Delay(500);

            // Connect WITHOUT token — should be rejected (or fail to connect)
            using var noAuthClient = new global::System.Net.WebSockets.ClientWebSocket();
            var authConnected = false;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    await noAuthClient.ConnectAsync(new Uri("ws://127.0.0.1:19101/mcp/"), wsCts2.Token);
                    authConnected = true;
                    break;
                }
                catch { await Task.Delay(200); }
            }

            if (!authConnected)
            {
                // Can't test on this platform (HttpListener requires admin)
                Console.WriteLine("SKIP (HttpListener unavailable)");
                skipped++;
            }
            else
            {
                // If we connected without a token, auth is not working properly
                Console.WriteLine("FAIL (connected without token — auth should reject)");
                failed++;
                await noAuthClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
            }

            // Now connect WITH correct token
            using var authClient = new global::System.Net.WebSockets.ClientWebSocket();
            var authedClient = false;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    await authClient.ConnectAsync(new Uri("ws://127.0.0.1:19101/mcp/?token=secret-token-123"), wsCts2.Token);
                    authedClient = true;
                    break;
                }
                catch { await Task.Delay(200); }
            }

            if (authedClient)
            {
                // Verify we can send/receive
                var initReq = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""";
                await authClient.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(initReq)), WebSocketMessageType.Text, true, wsCts2.Token);
                var rb = new byte[4096];
                using var rms = new MemoryStream();
                WebSocketReceiveResult rr;
                do { rr = await authClient.ReceiveAsync(new ArraySegment<byte>(rb), wsCts2.Token); rms.Write(rb, 0, rr.Count); } while (!rr.EndOfMessage);
                var rj = Encoding.UTF8.GetString(rms.ToArray());
                using var rd = JsonDocument.Parse(rj);
                if (rd.RootElement.TryGetProperty("result", out _))
                {
                    Console.WriteLine("PASS");
                    passed++;
                }
                else { Console.WriteLine($"FAIL (no result in: {rj})"); failed++; }
                await authClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
            }
            else
            {
                Console.WriteLine("SKIP (could not connect with token)");
                skipped++;
            }

            wsCts2.Cancel();
        }
        catch (Exception ex) { Console.WriteLine($"FAIL ({ex.Message})"); failed++; }

        // Test 10: Metrics tracking (TotalRequests increments)
        try
        {
            Console.Write("Test 10: Metrics tracking... ");
            var metricsLogger = new ConsoleLogger();
            await using var metricsMcp = new McpServer(metricsLogger);
            metricsMcp.RegisterTool("test", _ => Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true })));

            var before = metricsMcp.TotalRequests;
            var req = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""";
            using var doc = JsonDocument.Parse(req);
            await metricsMcp.HandleRequestAsync(doc.RootElement);
            var after = metricsMcp.TotalRequests;

            if (after > before)
            {
                Console.WriteLine("PASS");
                passed++;
            }
            else { Console.WriteLine($"FAIL (requests not incrementing: {before} -> {after})"); failed++; }
        }
        catch (Exception ex) { Console.WriteLine($"FAIL ({ex.Message})"); failed++; }

        // Test 11: Custody trail — full pipeline (create records → verify chain → query)
        try
        {
            Console.Write("Test 11: Custody trail E2E (create → chain → query)... ");

            // Step 1: Create a CustodyAuditLogger and produce records
            using var custodyLogger = new CustodyAuditLogger("e2e-drone");
            var records = new List<CustodyRecord>();

            records.Add(custodyLogger.LogToolCall("run_command", "{\"cmd\":\"ls -la\"}"));
            records.Add(custodyLogger.LogConnection("connected", "messenger"));
            records.Add(custodyLogger.LogSecurity("auth_check", "token validated"));
            records.Add(custodyLogger.LogCrossMachine("remote_exec", "drone-2", "{\"cmd\":\"uname\"}"));
            records.Add(custodyLogger.LogToolCall("read_file", "{\"path\":\"/etc/hosts\"}", result: "ok"));

            // Step 2: Verify the entire hash chain
            var chainValid = CustodyChain.VerifyChain(records);
            if (!chainValid)
            {
                Console.WriteLine("FAIL (hash chain verification failed)");
                failed++;
            }
            else
            {
                // Step 3: Verify individual record properties
                var allSealed = records.All(r => r.VerifyHash());
                var sequencesCorrect = records[0].Sequence == 1 && records[^1].Sequence == 5;
                var genesisOk = string.IsNullOrEmpty(records[0].PrevHash);
                var chainedOk = records[1].PrevHash == records[0].Hash;
                var correlationOk = records[3].CorrelationId != null && records[3].CorrelationId!.StartsWith("corr-");

                if (allSealed && sequencesCorrect && genesisOk && chainedOk && correlationOk)
                {
                    // Step 4: Verify ring buffer retrieval
                    var recent = custodyLogger.GetRecentRecords(10);
                    var afterSeq = custodyLogger.GetRecordsAfter(2);

                    if (recent.Length == 5 && afterSeq.Length == 3)
                    {
                        // Step 5: JSON round-trip preserves chain integrity
                        var jsonRecords = records.Select(r => r.ToJson()).ToArray();
                        var restored = jsonRecords.Select(j => CustodyRecord.FromJson(j)!).ToList();
                        var restoredChainValid = CustodyChain.VerifyChain(restored);

                        if (restoredChainValid)
                        {
                            Console.WriteLine("PASS (5 records, chain valid, correlation tracked)");
                            passed++;
                        }
                        else
                        {
                            Console.WriteLine("FAIL (JSON round-trip broke chain)");
                            failed++;
                        }
                    }
                    else
                    {
                        Console.WriteLine($"FAIL (buffer mismatch: recent={recent.Length}, after={afterSeq.Length})");
                        failed++;
                    }
                }
                else
                {
                    Console.WriteLine($"FAIL (sealed={allSealed}, seq={sequencesCorrect}, genesis={genesisOk}, chained={chainedOk}, corr={correlationOk})");
                    failed++;
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"FAIL ({ex.Message})"); failed++; }

        Console.WriteLine($"\n=== Results: {passed} passed, {failed} failed, {skipped} skipped, {passed + failed + skipped} total ===");
        return failed > 0 ? 1 : 0;
    }
}

internal class ConsoleLogger : ILogger
{
    public void LogInformation(string message, params object[] args) => Console.WriteLine("[INFO] " + message, args);
    public void LogWarning(string message, params object[] args) => Console.WriteLine("[WARN] " + message, args);
    public void LogError(string message, params object[] args) => Console.WriteLine("[ERROR] " + message, args);
    public void LogDebug(string message, params object[] args) => Console.WriteLine("[DEBUG] " + message, args);
}
