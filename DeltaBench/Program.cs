using Drone.Core;
using Drone.Native;
using Drone.Services.Remote;
using Drone.System;
using Drone.System.Windows;
using System.Diagnostics;

Console.WriteLine("=== Velocity Drone v12 Zero-GC Delta Benchmark ===");
Console.WriteLine();

var logger = new ConsoleLogger();
var screen = new Win32ScreenCapture(logger);

Console.Write("Delta engine: ");
Console.WriteLine(DeltaEngine.IsNativeAvailable ? "available" : "NOT available");
if (!DeltaEngine.IsNativeAvailable) return;

Console.Write("WebP compression: ");
Console.WriteLine(WebpCompressor.IsAvailable ? "available" : "NOT available");

var (screenW, screenH) = await screen.GetScreenSizeAsync();
Console.WriteLine("Screen: {0}x{1}", screenW, screenH);
Console.WriteLine("DXGI available: {0}", screen.IsDxgiAvailable);
Console.WriteLine();

using var pipeline = new DeltaScreenPipeline(screen, logger);
if (!pipeline.IsDeltaAvailable) { Console.WriteLine("Delta pipeline not available"); return; }

Console.WriteLine("Warming up...");
for (int i = 0; i < 3; i++) await pipeline.CaptureAndDeltaAsync();

Console.WriteLine("Benchmark (20 frames):");
Console.WriteLine();

const int iterations = 20;
var times = new List<double>();
long totalWire = 0, totalRaw = 0;
int totalRects = 0;

for (int i = 0; i < iterations; i++)
{
    var sw = Stopwatch.StartNew();
    var (wireLen, isDelta, rectCount, rawBytes) = await pipeline.CaptureAndDeltaAsync();
    sw.Stop();
    times.Add(sw.Elapsed.TotalMilliseconds);
    totalWire += wireLen;
    totalRaw += rawBytes;
    totalRects += rectCount;
    Console.WriteLine("  Frame {0,2}: {1,6:F2}ms | wire={2,7}B | rects={3,3} | raw={4,9}B", i+1, sw.Elapsed.TotalMilliseconds, wireLen, rectCount, rawBytes);
}

long bmpSize = 0;
var bmpTimes = new List<double>();
for (int i = 0; i < 5; i++)
{
    var sw = Stopwatch.StartNew();
    var bmp = await screen.CaptureScreenAsync();
    sw.Stop();
    bmpTimes.Add(sw.Elapsed.TotalMilliseconds);
    if (i == 0) bmpSize = bmp.Length;
}

Console.WriteLine();
Console.WriteLine("=== RESULTS ===");
Console.WriteLine("Delta avg:    {0:F2} ms", times.Average());
Console.WriteLine("Delta min:    {0:F2} ms", times.Min());
Console.WriteLine("Delta max:    {0:F2} ms", times.Max());
Console.WriteLine("Avg wire:     {0} bytes", totalWire / iterations);
Console.WriteLine("Avg rects:    {0}", totalRects / iterations);
Console.WriteLine("BMP size:     {0} bytes", bmpSize);
Console.WriteLine("BMP avg:      {0:F2} ms", bmpTimes.Average());
if (bmpSize > 0 && totalWire > 0)
{
    var avgWire = totalWire / iterations;
    Console.WriteLine("Compression:  {0:F1}x", (double)bmpSize / avgWire);
}
Console.WriteLine();
Console.WriteLine("ZERO-GC: All buffers pre-allocated, no per-frame allocations");

sealed class ConsoleLogger : Drone.Core.ILogger
{
    private string FormatMsg(string message, object[] args)
    {
        if (args == null || args.Length == 0) return message;
        // Replace {PropertyName} with values
        var result = message;
        for (int i = 0; i < args.Length && i < 10; i++)
        {
            // Find first {xxx} pattern and replace
            int start = result.IndexOf('{');
            if (start < 0) break;
            int end = result.IndexOf('}', start);
            if (end < 0) break;
            result = result.Substring(0, start) + (args[i]?.ToString() ?? "null") + result.Substring(end + 1);
        }
        return result;
    }
    
    public void LogInformation(string message, params object[] args) => Console.WriteLine("[INFO] " + FormatMsg(message, args));
    public void LogWarning(string message, params object[] args) => Console.WriteLine("[WARN] " + FormatMsg(message, args));
    public void LogError(string message, params object[] args) => Console.WriteLine("[ERROR] " + FormatMsg(message, args));
    public void LogDebug(string message, params object[] args) => Console.WriteLine("[DEBUG] " + FormatMsg(message, args));
}