namespace Drone.Autonomy;

public class EventBus
{
    private readonly List<Func<DroneEvent, Task>> _handlers = new();
    private readonly object _lock = new();

    public void Subscribe<T>(Func<DroneEvent, Task> handler) where T : DroneEvent
    {
        lock (_lock) { _handlers.Add(handler); }
    }

    public void Unsubscribe(Func<DroneEvent, Task> handler)
    {
        lock (_lock) { _handlers.Remove(handler); }
    }

    public async Task PublishAsync(DroneEvent evt)
    {
        Func<DroneEvent, Task>[] handlers;
        lock (_lock) { handlers = _handlers.ToArray(); }
        foreach (var handler in handlers)
        {
            try { await handler(evt); }
            catch { /* handler errors are non-fatal */ }
        }
    }
}

public class DroneEvent
{
    public string Type { get; }
    public object Data { get; }
    public DateTime Timestamp { get; } = DateTime.UtcNow;

    public DroneEvent(string type, object data) { Type = type; Data = data; }
}

// â”€â”€ Trigger event types (spec Phase 3) â”€â”€
public static class DroneEventTypes
{
    // Service events
    public const string MessageReceived = "MessageReceived";
    public const string FileChanged = "FileChanged";
    public const string ScreenChanged = "ScreenChanged";

    // Process events
    public const string ProcessStarted = "ProcessStarted";
    public const string ProcessStopped = "ProcessStopped";

    // System events
    public const string SystemAlert = "SystemAlert";
    public const string SystemMetrics = "SystemMetrics";

    // Scheduled
    public const string ScheduledTask = "ScheduledTask";
}
