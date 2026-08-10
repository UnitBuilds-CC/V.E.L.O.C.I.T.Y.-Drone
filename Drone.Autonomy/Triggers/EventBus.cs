namespace Drone.Autonomy;

public class EventBus
{
    private readonly List<Func<DroneEvent, Task>> _handlers = new();
    private readonly object _lock = new();
    public void Subscribe<T>(Func<DroneEvent, Task> handler) where T : DroneEvent { lock (_lock) { _handlers.Add(handler); } }
    public async Task PublishAsync(DroneEvent evt) { Func<DroneEvent, Task>[] handlers; lock (_lock) { handlers = _handlers.ToArray(); } foreach (var handler in handlers) { try { await handler(evt); } catch { } } }
}

public class DroneEvent
{
    public string Type { get; }
    public object Data { get; }
    public DateTime Timestamp { get; } = DateTime.UtcNow;
    public DroneEvent(string type, object data) { Type = type; Data = data; }
}
