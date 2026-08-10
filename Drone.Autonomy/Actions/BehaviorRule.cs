namespace Drone.Autonomy;

public class BehaviorRule
{
    public string Name { get; set; } = "";
    public string Trigger { get; set; } = "";
    public string Action { get; set; } = "";
    public Dictionary<string, string> ActionParams { get; set; } = new();
    public string? Condition { get; set; }
    public bool Enabled { get; set; } = true;
    public bool MatchesCondition(DroneEvent evt) => Trigger == "*" || Trigger == evt.Type;
    public Task ExecuteAction(DroneEvent evt) => Task.CompletedTask;
}
