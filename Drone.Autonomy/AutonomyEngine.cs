using global::System.Text.Json;
using Drone.Core;
using Drone.Core.Config;

namespace Drone.Autonomy;

public class AutonomyEngine : IAsyncDisposable
{
    private readonly AutonomyConfig _config;
    private readonly ILogger _logger;
    private readonly List<BehaviorRule> _rules = new();
    private readonly List<Timer> _timers = new();
    private CancellationTokenSource? _cts;

    public event Func<string, string, object, Task>? OnActionExecuted;

    public AutonomyEngine(AutonomyConfig config, ILogger logger) { _config = config; _logger = logger; }

    public void LoadRules(string? path = null)
    {
        var rulesPath = path ?? _config.RulesPath;
        if (!global::System.IO.File.Exists(rulesPath))
        {
            _logger.LogInformation("No rules file, using defaults.");
            _rules.Add(new BehaviorRule { Name = "LogAllEvents", Trigger = "*", Action = "log", Enabled = true });
            return;
        }
        try
        {
            var json = global::System.IO.File.ReadAllText(rulesPath);
            var rules = JsonSerializer.Deserialize<BehaviorRule[]>(json);
            if (rules != null) { _rules.Clear(); _rules.AddRange(rules); _logger.LogInformation("Loaded " + rules.Length + " behavior rules"); }
        }
        catch (Exception ex) { _logger.LogError("Failed to load rules: " + ex.Message); _rules.Add(new BehaviorRule { Name = "LogAllEvents", Trigger = "*", Action = "log", Enabled = true }); }
    }

    public void AddRule(BehaviorRule rule) => _rules.Add(rule);

    public Task StartAsync(EventBus eventBus, CancellationToken ct = default)
    {
        if (!_config.Enabled) { _logger.LogInformation("Autonomy engine disabled."); return Task.CompletedTask; }
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        LoadRules();

        eventBus.Subscribe<DroneEvent>(async evt =>
        {
            foreach (var rule in _rules.Where(r => (r.Trigger == "*" || r.Trigger == evt.Type) && r.Enabled))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (OnActionExecuted != null) await OnActionExecuted(rule.Name, evt.Type, evt.Data);
                    }
                    catch (Exception ex) { _logger.LogWarning("Rule " + rule.Name + " failed: " + ex.Message); }
                }, _cts.Token);
            }
        });

        if (_config.SystemMetricsIntervalSec > 0)
        {
            var timer = new Timer(async _ =>
            {
                await eventBus.PublishAsync(new DroneEvent("SystemMetrics", new { timestamp = DateTime.UtcNow }));
            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(_config.SystemMetricsIntervalSec));
            _timers.Add(timer);
        }

        _logger.LogInformation("Autonomy engine started with " + _rules.Count + " rules");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        foreach (var t in _timers) t.Dispose();
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }
}
