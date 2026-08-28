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

        using var store = new CustodyLogStore(storagePath);
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
    public void LogInformation(string message, params object[] args) => Console.WriteLine("[INFO] " + message, args);
    public void LogWarning(string message, params object[] args) => Console.WriteLine("[WARN] " + message, args);
    public void LogError(string message, params object[] args) => Console.WriteLine("[ERROR] " + message, args);
    public void LogDebug(string message, params object[] args) => Console.WriteLine("[DBG]  " + message, args);
}
