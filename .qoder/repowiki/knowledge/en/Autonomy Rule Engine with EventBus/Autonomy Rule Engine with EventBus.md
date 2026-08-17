---
kind: behavior_system
name: Autonomy Rule Engine with EventBus
category: behavior
scope:
    - 'Drone.Autonomy/**'
    - 'Drone.Core/EventBus.cs'
source_files:
    - Drone.Autonomy/AutonomyEngine.cs
    - Drone.Autonomy/Triggers/BehaviorRule.cs
    - Drone.Autonomy/Actions/ActionHandler.cs
    - Drone.Core/EventBus.cs
---

The Autonomy system provides event-driven autonomous behavior through a rule engine that evaluates triggers and executes actions. It enables the drone to react to events without explicit remote commands.

**Core Components:**

1. **EventBus** — Pub/sub event system:
   - `PublishAsync(DroneEvent)` — Broadcast event to all subscribers
   - `Subscribe(string eventType, Func<DroneEvent, Task> handler)` — Register handler
   - `ConcurrentDictionary` of subscribers for lock-free reads
   - Fire-and-forget dispatch (errors logged, don't block publisher)

2. **DroneEvent** — Event data structure:
   - `EventType` — String identifier (e.g., "message_received", "file_changed")
   - `Data` — Anonymous object with event-specific payload
   - `Timestamp` — UTC timestamp

3. **DroneEventTypes** — Standard event type constants:
   - `MessageReceived` — Incoming Messenger command
   - `FileChanged` — Share directory file change
   - `ConnectionChanged` — Connection state change
   - `ToolExecuted` — MCP tool call completed

4. **BehaviorRule** — Trigger + action pairing:
   - `Name` — Rule identifier
   - `Trigger` — Condition for rule activation
   - `Action` — Handler to execute when triggered
   - `Enabled` — Whether rule is active

5. **ActionHandler** — Action execution:
   - `ExecuteAsync(DroneEvent, ActionParams)` — Perform action
   - Access to connectors (Messenger, Share, etc.)
   - Can send messages, upload files, execute commands

6. **AutonomyEngine** — Rule evaluation:
   - `StartAsync(EventBus)` — Begin listening for events
   - `OnActionExecuted` — Event fired after action execution
   - Evaluates rules against incoming events
   - Executes matching actions asynchronously

**Rule Configuration (rules.json):**

```json
{
  "rules": [
    {
      "name": "auto_acknowledge",
      "trigger": {
        "eventType": "message_received",
        "conditions": [
          { "field": "from", "operator": "contains", "value": "admin" }
        ]
      },
      "action": {
        "type": "send_message",
        "params": {
          "template": "[Auto-reply] Acknowledged by Velocity Drone"
        }
      },
      "enabled": true
    }
  ]
}
```

**Event Flow:**

```
External Event (Messenger, File Change, etc.)
    │
    ▼
EventBus.PublishAsync(DroneEvent)
    │
    ├──▶ Subscriber 1: AutonomyEngine
    │       │
    │       ▼
    │   Evaluate Rules
    │       │
    │       ▼
    │   Rule Matches? ──Yes──▶ Execute Action
    │       │                       │
    │       No                      ▼
    │       │               OnActionExecuted event
    │       │                       │
    │       └───────────────┬───────┘
    │                       │
    ├──▶ Subscriber 2: Logger
    │
    └──▶ Subscriber 3: Custom handler
```

**Trigger Conditions:**

| Operator | Description | Example |
|----------|-------------|---------|
| `equals` | Exact match | `from == "admin"` |
| `contains` | Substring match | `from contains "admin"` |
| `startsWith` | Prefix match | `content startsWith "status"` |
| `endsWith` | Suffix match | `path endsWith ".txt"` |
| `regex` | Regular expression | `content matches "^run .*"` |
| `exists` | Field present | `data.field exists` |

**Action Types:**

| Type | Description | Params |
|------|-------------|--------|
| `send_message` | Send Messenger reply | `template`, `to` |
| `run_command` | Execute shell command | `command` |
| `upload_file` | Upload to Share | `localPath`, `remotePath` |
| `download_file` | Download from Share | `remotePath`, `localPath` |
| `notify_uplink` | Send notification to uplink | `event`, `data` |

**Integration with Agent:**

```csharp
// Drone.Agent/Program.cs
var eventBus = new EventBus();
var autonomy = new AutonomyEngine(config.Autonomy, logger);

// Subscribe to autonomy engine
await autonomy.StartAsync(eventBus);

// Publish events from connectors
messenger.OnMessageReceived += async (from, content, msgId) =>
{
    // ... handle command ...
    await eventBus.PublishAsync(new DroneEvent(
        DroneEventTypes.MessageReceived, 
        new { from, content, messageId = msgId }
    ));
};

// Handle action execution
autonomy.OnActionExecuted += async (ruleName, eventType, data) =>
{
    // Send auto-reply via Messenger
    if (eventType == DroneEventTypes.MessageReceived && messenger?.IsConnected == true)
    {
        await messenger.SendMessageAsync(from, "[Auto-reply] Acknowledged");
    }
    
    // Notify uplink
    if (uplink?.IsConnected == true)
    {
        await uplink.SendNotificationAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "notifications/droneEvent",
            @params = new { eventType, data, ruleName }
        }));
    }
};
```

**Configuration:**

```json
{
  "Autonomy": {
    "Enabled": true,
    "RulesPath": "rules.json",
    "EvaluationInterval": "00:00:01"
  }
}
```

**Error Handling:**

- Rule evaluation errors logged, don't crash agent
- Action execution errors caught and logged
- EventBus isolates subscriber failures (one bad handler doesn't affect others)
- Disabled rules skipped during evaluation

**Extensibility:**

- Add new event types by publishing to EventBus
- Add new triggers by extending BehaviorRule conditions
- Add new actions by implementing ActionHandler types
- Rules can be loaded from file or database
