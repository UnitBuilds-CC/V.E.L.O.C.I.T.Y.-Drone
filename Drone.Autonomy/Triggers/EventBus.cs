namespace Drone.Autonomy;

public class EventBus
{
    private readonly List<(string? EventType, Func<DroneEvent, Task> Handler)> _handlers = new();
    private readonly object _lock = new();

    /// <summary>Subscribe to all events.</summary>
    public void Subscribe(Func<DroneEvent, Task> handler)
    {
        lock (_lock) { _handlers.Add((null, handler)); }
    }

    /// <summary>Subscribe only to events of a specific type.</summary>
    public void Subscribe(string eventType, Func<DroneEvent, Task> handler)
    {
        lock (_lock) { _handlers.Add((eventType, handler)); }
    }

    /// <summary>Unsubscribe a handler.</summary>
    public void Unsubscribe(Func<DroneEvent, Task> handler)
    {
        lock (_lock) { _handlers.RemoveAll(h => h.Handler == handler); }
    }

    /// <summary>Publish an event to all matching handlers.</summary>
    public async Task PublishAsync(DroneEvent evt)
    {
        (string? EventType, Func<DroneEvent, Task> Handler)[] snapshot;
        lock (_lock) { snapshot = _handlers.ToArray(); }
        foreach (var (eventType, handler) in snapshot)
        {
            if (eventType != null && eventType != evt.Type) continue;
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

public static class DroneEventTypes
{
    public const string MessageReceived = "MessageReceived";
    public const string FileChanged = "FileChanged";
    public const string ScreenChanged = "ScreenChanged";
    public const string ProcessStarted = "ProcessStarted";
    public const string ProcessStopped = "ProcessStopped";
    public const string SystemAlert = "SystemAlert";
    public const string SystemMetrics = "SystemMetrics";
    public const string ScheduledTask = "ScheduledTask";
}
