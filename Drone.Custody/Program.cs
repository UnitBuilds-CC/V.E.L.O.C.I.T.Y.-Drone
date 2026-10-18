using Drone.Core;
using Drone.Core.Custody;

namespace Drone.Custody;

/// <summary>
/// Standalone Custody Server entry point.
/// Receives hash-chained custody records from drones, validates chains,
/// and provides query/streaming endpoints.
/// </summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        var logger = new ConsoleLogger();
        var storagePath = Environment.GetEnvironmentVariable("CUSTODY_STORAGE_PATH") ?? "./custody-data";
        var listenUrl = Environment.GetEnvironmentVariable("CUSTODY_LISTEN_URL") ?? "http://+:5010/";

        logger.LogInformation("=== Velocity Custody Server ===");
        logger.LogInformation("Storage: {Path}", storagePath);
        logger.LogInformation("Listening: {Url}", listenUrl);

        using var store = new CustodyLogStore(storagePath, logger: logger);
        await using var server = new CustodyServerHost(store, logger);

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var serverTask = Task.Run(() => server.StartAsync(listenUrl, cts.Token));

        logger.LogInformation("Custody Server ready. Press Ctrl+C to stop.");

        try { await serverTask; }
        catch (OperationCanceledException) { }

        logger.LogInformation("Custody Server stopped. Total records: {Count}", store.TotalRecords);
    }
}

internal class ConsoleLogger : Drone.Core.ILogger
{
    private static string Format(string message, object[] args)
    {
        if (args == null || args.Length == 0) return message;
        var result = message;
        for (int i = 0; i < args.Length; i++)
        {
            // Replace first {Xxx} placeholder with arg value (Serilog-style)
            var idx = result.IndexOf('{');
            if (idx < 0) break;
            var end = result.IndexOf('}', idx);
            if (end < 0) break;
            result = result.Substring(0, idx) + args[i]?.ToString() + result.Substring(end + 1);
        }
        return result;
    }
    public void LogInformation(string message, params object[] args) => Console.WriteLine("[INFO] " + Format(message, args));
    public void LogWarning(string message, params object[] args) => Console.WriteLine("[WARN] " + Format(message, args));
    public void LogError(string message, params object[] args) => Console.WriteLine("[ERROR] " + Format(message, args));
    public void LogDebug(string message, params object[] args) => Console.WriteLine("[DBG]  " + Format(message, args));
}
