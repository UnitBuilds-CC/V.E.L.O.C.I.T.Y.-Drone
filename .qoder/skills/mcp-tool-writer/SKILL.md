# MCP Tool Writer

## Description
Create new MCP (Model Context Protocol) tools that extend the drone's capabilities. Use when adding new remote-executable functions, integrating with external systems, or exposing platform features via the MCP interface.

## When to Use
- Adding a new remote-executable capability
- Integrating with external APIs or services
- Exposing platform-specific features (screen, input, files, etc.)
- Creating custom automation tools

## Tool Creation Workflow

### Step 1: Define Tool Metadata

Determine the tool's:
- **Name:** Snake_case identifier (e.g., `get_system_info`)
- **Description:** Human-readable purpose (shown to AI agents)
- **Parameters:** Typed inputs with descriptions
- **Return type:** JSON-serializable output

### Step 2: Implement Tool Handler

Add the tool to `Drone.MCP/Tools/SystemToolRegistrar.cs`:

```csharp
public static void RegisterAll(McpServer server, /* dependencies */)
{
    // Existing tools...
    
    server.RegisterTool(
        name: "my_new_tool",
        description: "Performs a specific action and returns result",
        parameters: new[]
        {
            new ToolParameter("param1", "string", "Description of param1", required: true),
            new ToolParameter("param2", "integer", "Description of param2", required: false)
        },
        handler: async (args) =>
        {
            // Extract parameters
            var param1 = args.GetProperty("param1").GetString();
            var param2 = args.TryGetProperty("param2", out var p2) ? p2.GetInt32() : 0;
            
            // Execute logic
            var result = await DoSomethingAsync(param1, param2);
            
            // Return JSON result
            return JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                success = true,
                data = result,
                timestamp = DateTime.UtcNow
            }));
        });
}
```

### Step 3: Add Custody Logging (Optional)

If the tool performs significant actions, log to custody trail:

```csharp
handler: async (args) =>
{
    // Log tool invocation
    custodyLogger.LogToolCall(
        "my_new_tool", 
        $"param1={param1},param2={param2}", 
        targetSystem: "external_api");
    
    // ... tool logic ...
}
```

### Step 4: Write Tests

Add unit tests in `tests/Drone.Tests/McpServerTests.cs`:

```csharp
[Fact]
public async Task McpServer_MyNewTool_ReturnsExpectedResult()
{
    // Arrange
    var server = new McpServer(new NullLogger());
    SystemToolRegistrar.RegisterAll(server, /* mocks */);
    
    var args = JsonDocument.Parse(@"{""param1"":""test"",""param2"":42}").RootElement;
    
    // Act
    var result = await server.InvokeToolAsync("my_new_tool", args);
    
    // Assert
    var resultObj = JsonDocument.Parse(result.ToString());
    Assert.True(resultObj.RootElement.GetProperty("success").GetBoolean());
    Assert.Equal("expected", resultObj.RootElement.GetProperty("data").GetString());
}
```

### Step 5: Add Messenger Command (Optional)

If the tool should be accessible via Messenger commands, add to `Drone.Agent/Program.cs`:

```csharp
else if (cmd.StartsWith("mycommand ", StringComparison.OrdinalIgnoreCase))
{
    var param = cmd.Substring(10);
    var argsJson = JsonSerializer.Serialize(new { param1 = param });
    var result = await mcpServer.InvokeToolAsync("my_new_tool", 
        JsonDocument.Parse(argsJson).RootElement);
    response = $"Result: {result}";
}
```

## Tool Templates

### Template 1: Simple Query Tool

```csharp
server.RegisterTool("get_status", "Get current system status", 
    Array.Empty<ToolParameter>(),
    async (args) =>
    {
        var status = new
        {
            uptime = DateTime.UtcNow - startTime,
            connections = new { messenger = messenger?.IsConnected ?? false },
            memory = Process.GetCurrentProcess().WorkingSet64
        };
        return JsonDocument.Parse(JsonSerializer.Serialize(status));
    });
```

### Template 2: Parameterized Action Tool

```csharp
server.RegisterTool("send_notification", "Send a notification to user",
    new[]
    {
        new ToolParameter("title", "string", "Notification title", required: true),
        new ToolParameter("message", "string", "Notification message", required: true),
        new ToolParameter("priority", "string", "low|medium|high", required: false)
    },
    async (args) =>
    {
        var title = args.GetProperty("title").GetString();
        var message = args.GetProperty("message").GetString();
        var priority = args.TryGetProperty("priority", out var p) ? p.GetString() : "medium";
        
        // Send notification
        await notificationService.SendAsync(title, message, priority);
        
        return JsonDocument.Parse(@"{""success"":true}");
    });
```

### Template 3: External API Integration

```csharp
server.RegisterTool("fetch_weather", "Get current weather for location",
    new[] { new ToolParameter("city", "string", "City name", required: true) },
    async (args) =>
    {
        var city = args.GetProperty("city").GetString();
        
        using var httpClient = new HttpClient();
        var response = await httpClient.GetStringAsync(
            $"https://api.weather.com/v1/current?city={Uri.EscapeDataString(city)}");
        
        return JsonDocument.Parse(response);
    });
```

### Template 4: File Operation Tool

```csharp
server.RegisterTool("read_file_content", "Read text file content",
    new[] { new ToolParameter("path", "string", "File path", required: true) },
    async (args) =>
    {
        var path = args.GetProperty("path").GetString();
        
        if (!File.Exists(path))
            throw new FileNotFoundException($"File not found: {path}");
        
        var content = await File.ReadAllTextAsync(path);
        
        return JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            path,
            size = new FileInfo(path).Length,
            content,
            lastModified = File.GetLastWriteTimeUtc(path)
        }));
    });
```

## Error Handling

Tools should handle errors gracefully:

```csharp
handler: async (args) =>
{
    try
    {
        // Tool logic
        var result = await DoWorkAsync();
        return JsonDocument.Parse(JsonSerializer.Serialize(new { success = true, data = result }));
    }
    catch (ArgumentException ex)
    {
        // Return error in result (don't throw)
        return JsonDocument.Parse(JsonSerializer.Serialize(new 
        { 
            success = false, 
            error = "invalid_argument", 
            message = ex.Message 
        }));
    }
    catch (Exception ex)
    {
        // Unexpected errors — let MCP server handle
        throw;
    }
}
```

## Best Practices

1. **Use descriptive names** — `get_system_info` not `gsinfo`
2. **Document parameters** — Help AI agents understand usage
3. **Validate inputs** — Check required params, validate types
4. **Return structured JSON** — Consistent format for parsing
5. **Log significant actions** — Custody trail for audit
6. **Handle timeouts** — Use `CancellationToken` for long operations
7. **Test thoroughly** — Unit test all code paths

## Testing Checklist

- [ ] Tool appears in `tools/list` response
- [ ] Parameters validated correctly
- [ ] Success case returns expected JSON
- [ ] Error cases handled gracefully
- [ ] Custody logging works (if applicable)
- [ ] Messenger command works (if added)
- [ ] Concurrent calls handled correctly

## Related Files

- `Drone.MCP/McpServer.cs` — Tool registration and invocation
- `Drone.MCP/Tools/SystemToolRegistrar.cs` — Tool definitions
- `tests/Drone.Tests/McpServerTests.cs` — Tool tests
- `Drone.Agent/Program.cs` — Messenger command handler
