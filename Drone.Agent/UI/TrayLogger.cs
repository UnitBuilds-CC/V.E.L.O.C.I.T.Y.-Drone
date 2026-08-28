using System;
using Microsoft.Extensions.Logging;

namespace Drone.Agent.UI;

public class TrayLoggerProvider : ILoggerProvider
{
    private readonly TrayApp _trayApp;
    public TrayLoggerProvider(TrayApp trayApp) => _trayApp = trayApp;
    public ILogger CreateLogger(string categoryName) => new TrayLogger(_trayApp, categoryName);
    public void Dispose() { }
}

public class TrayLogger : ILogger
{
    private readonly TrayApp _trayApp;
    private readonly string _category;

    public TrayLogger(TrayApp trayApp, string category)
    {
        _trayApp = trayApp;
        _category = category;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        var prefix = logLevel switch
        {
            LogLevel.Error => "ERROR",
            LogLevel.Warning => "WARN ",
            LogLevel.Information => "INFO ",
            LogLevel.Debug => "DBG  ",
            LogLevel.Trace => "TRACE",
            _ => "     "
        };
        _trayApp.Log($"[{prefix}] {message}");

        if (exception != null)
            _trayApp.Log($"       {exception.GetType().Name}: {exception.Message}");
    }
}