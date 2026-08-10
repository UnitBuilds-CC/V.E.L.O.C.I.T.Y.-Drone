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
            _logger.LogInformation("No rules file found, using defaults.");
            _rules.Add(new BehaviorRule { Name = "LogAllEvents", Trigger = "*", Action = "log", Enabled = true });
            return;
        }
        try
        {
            var json = global::System.IO.File.ReadAllText(rulesPath);
            var rules = JsonSerializer.Deserialize<BehaviorRule[]>(json);
            if (rules != null)
            {
                _rules.Clear();
                _rules.AddRange(rules);
                _logger.LogInformation("Loaded " + rules.Length + " behavior rules from " + rulesPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to load rules: " + ex.Message);
            _rules.Add(new BehaviorRule { Name = "LogAllEvents", Trigger = "*", Action = "log", Enabled = true });
        }
    }

    public void AddRule(BehaviorRule rule) => _rules.Add(rule);

    public Task StartAsync(EventBus eventBus, CancellationToken ct = default)
    {
        if (!_config.Enabled) { _logger.LogInformation("Autonomy engine disabled."); return Task.CompletedTask; }
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        LoadRules();

        // Subscribe to all events and execute matching rules
        eventBus.Subscribe<DroneEvent>(async evt =>
        {
            foreach (var rule in _rules.Where(r => r.MatchesCondition(evt) && r.Enabled))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var handler = Actions.ActionHandlers.GetHandler(rule.Action);
                        await handler(evt, rule.ActionParams, _logger);
                        if (OnActionExecuted != null) await OnActionExecuted(rule.Name, evt.Type, evt.Data);
                    }
                    catch (Exception ex) { _logger.LogWarning("Rule " + rule.Name + " failed: " + ex.Message); }
                }, _cts.Token);
            }
        });

        // System metrics timer (triggers SystemMetrics + SystemAlert events)
        if (_config.SystemMetricsIntervalSec > 0)
        {
            var timer = new Timer(async _ =>
            {
                try
                {
                    var proc = global::System.Diagnostics.Process.GetCurrentProcess();
                    var cpuTime1 = proc.TotalProcessorTime;
                    var wallTime1 = DateTime.UtcNow;
                    await Task.Delay(1000);
                    var cpuTime2 = proc.TotalProcessorTime;
                    var wallTime2 = DateTime.UtcNow;
                    var cpuPercent = (int)(((cpuTime2 - cpuTime1).TotalMilliseconds / (wallTime2 - wallTime1).TotalMilliseconds) * 100 / global::System.Environment.ProcessorCount);
                    var memoryMB = proc.WorkingSet64 / 1024 / 1024;

                    await eventBus.PublishAsync(new DroneEvent(DroneEventTypes.SystemMetrics, new
                    {
                        timestamp = DateTime.UtcNow,
                        cpuPercent,
                        memoryMB
                    }));

                    if (cpuPercent > 90)
                    {
                        await eventBus.PublishAsync(new DroneEvent(DroneEventTypes.SystemAlert, new
                        {
                            alertType = "HighCPU",
                            value = cpuPercent,
                            threshold = 90,
                            timestamp = DateTime.UtcNow
                        }));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("System metrics error: " + ex.Message);
                }
            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(_config.SystemMetricsIntervalSec));
            _timers.Add(timer);
        }

        // Process monitor timer (triggers ProcessStarted/Stopped events)
        if (_config.ProcessMonitorIntervalSec > 0)
        {
            var knownPids = new HashSet<int>();
            var procTimer = new Timer(async _ =>
            {
                try
                {
                    var current = global::System.Diagnostics.Process.GetProcesses().Select(p => p.Id).ToHashSet();
                    var started = current.Except(knownPids).ToArray();
                    var stopped = knownPids.Except(current).ToArray();

                    foreach (var pid in started)
                    {
                        try
                        {
                            var p = global::System.Diagnostics.Process.GetProcessById(pid);
                            await eventBus.PublishAsync(new DroneEvent(DroneEventTypes.ProcessStarted, new { pid, name = p.ProcessName }));
                        }
                        catch { }
                    }

                    foreach (var pid in stopped)
                    {
                        await eventBus.PublishAsync(new DroneEvent(DroneEventTypes.ProcessStopped, new { pid }));
                    }

                    knownPids = current;
                }
                catch (Exception ex) { _logger.LogWarning("Process monitor error: " + ex.Message); }
            }, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(_config.ProcessMonitorIntervalSec));
            _timers.Add(procTimer);
        }

        // Scheduled task poll timer
        if (_config.ScheduledTaskPollSec > 0)
        {
            var schedTimer = new Timer(async _ =>
            {
                await eventBus.PublishAsync(new DroneEvent(DroneEventTypes.ScheduledTask, new
                {
                    timestamp = DateTime.UtcNow,
                    message = "Scheduled task check"
                }));
            }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(_config.ScheduledTaskPollSec));
            _timers.Add(schedTimer);
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
