using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using Drone.Core;
using Drone.Core.Protocol;
using Drone.Native;
using Drone.System;

namespace Drone.Agent.Benchmarks;

/// <summary>
/// Comprehensive benchmark suite for all Velocity Drone subsystems.
/// Results encoded as NDA triples — no JSON.
/// </summary>
public static class DroneBenchmark
{
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<NdaPayload> RunAllAsync(
        string fileServerUrl,
        IScreenCapture? screen = null,
        IWindowManager? windows = null,
        IProcessManager? process = null)
    {
        var results = new NdaPayload();
        var counter = new BenchCounter();
        var totalSw = Stopwatch.StartNew();

        // === 1. NDA Encode/Decode ===
        BenchmarkNda(results, counter);

        // === 2. NMCP Frame Build/Parse ===
        BenchmarkNmcp(results, counter);

        // === 3. File Server (Share) ===
        await BenchmarkFileServer(results, counter, fileServerUrl);

        // === 4. Screen Capture ===
        if (screen != null)
            await BenchmarkScreenCapture(results, counter, screen);

        // === 5. Delta Engine ===
        if (screen != null)
            await BenchmarkDelta(results, counter, screen);

        // === 6. Window Enumeration ===
        if (windows != null)
            await BenchmarkWindowEnum(results, counter, windows);

        // === 7. Process Listing ===
        if (process != null)
            await BenchmarkProcessList(results, counter, process);

        // === 8. System Info ===
        if (process != null)
            await BenchmarkSystemInfo(results, counter, process);

        totalSw.Stop();
        AddResult(results, counter, "total", "All benchmarks completed", totalSw.ElapsedMilliseconds);

        return results;
    }

    // ─── NDA BENCHMARKS ──────────────────────────────────────────────

    private static void BenchmarkNda(NdaPayload results, BenchCounter counter)
    {
        int[] tripleCounts = { 1, 5, 10, 50, 100 };
        const int iterations = 1000;

        foreach (var count in tripleCounts)
        {
            var sample = new NdaPayload();
            for (int i = 0; i < count; i++)
                sample.Triples.Add(new NdaTriple($"subject{i}", $"predicate{i % 5}", $"object_value_{i}"));

            // Encode
            var sw = Stopwatch.StartNew();
            byte[]? encoded = null;
            for (int i = 0; i < iterations; i++)
                encoded = sample.Encode();
            sw.Stop();

            AddResult(results, counter, "nda_encode",
                $"Encode {count} triples", sw.ElapsedMilliseconds, iterations, encoded?.Length ?? 0);

            // Decode
            if (encoded != null)
            {
                sw.Restart();
                for (int i = 0; i < iterations; i++)
                    NdaPayload.Decode(encoded);
                sw.Stop();

                AddResult(results, counter, "nda_decode",
                    $"Decode {count} triples", sw.ElapsedMilliseconds, iterations);
            }
        }

        // Single-triple fast path
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
                NdaPayload.SingleTriple("bench", "test", "single_triple_encode");
            sw.Stop();
            AddResult(results, counter, "nda_single_encode",
                "SingleTriple encode", sw.ElapsedMilliseconds, iterations);
        }
    }

    // ─── NMCP FRAME BENCHMARKS ───────────────────────────────────────

    private static void BenchmarkNmcp(NdaPayload results, BenchCounter counter)
    {
        const int iterations = 10000;
        var ndaPayload = NdaPayload.SingleTriple("bench", "nmcp", "frame_test");

        // Frame build
        var sw = Stopwatch.StartNew();
        for (uint i = 0; i < iterations; i++)
        {
            var frame = new NmcpFrame(NmcpFrameTypes.JsonRpcRequest, i, ndaPayload);
            var header = new byte[NmcpFrame.HeaderSize];
            frame.WriteHeader(header);
        }
        sw.Stop();
        AddResult(results, counter, "nmcp_frame_build",
            "NMCP frame build", sw.ElapsedMilliseconds, iterations);

        // Header parse
        var testFrame = new NmcpFrame(NmcpFrameTypes.Handshake, 1, ndaPayload);
        var testHeader = new byte[NmcpFrame.HeaderSize];
        testFrame.WriteHeader(testHeader);

        sw.Restart();
        for (int i = 0; i < iterations; i++)
            NmcpFrame.TryReadHeader(testHeader, out _, out _, out _);
        sw.Stop();
        AddResult(results, counter, "nmcp_header_parse",
            "NMCP header parse", sw.ElapsedMilliseconds, iterations);

        // Full round-trip
        sw.Restart();
        for (uint i = 0; i < iterations; i++)
        {
            var frame = new NmcpFrame(NmcpFrameTypes.JsonRpcRequest, i, ndaPayload);
            var header = new byte[NmcpFrame.HeaderSize];
            frame.WriteHeader(header);
            NmcpFrame.TryReadHeader(header, out _, out _, out _);
        }
        sw.Stop();
        AddResult(results, counter, "nmcp_roundtrip",
            "NMCP build+parse roundtrip", sw.ElapsedMilliseconds, iterations);
    }

    // ─── FILE SERVER BENCHMARKS ──────────────────────────────────────

    private static async Task BenchmarkFileServer(NdaPayload results, BenchCounter counter, string baseUrl)
    {
        try
        {
            const int listIters = 100;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < listIters; i++)
                await s_http.GetStringAsync($"{baseUrl}/api/files?path=");
            sw.Stop();
            AddResult(results, counter, "share_list", "File list (empty)", sw.ElapsedMilliseconds, listIters);

            // Upload 1KB
            var smallData = new byte[1024];
            Random.Shared.NextBytes(smallData);
            const int uploadIters = 50;
            sw.Restart();
            for (int i = 0; i < uploadIters; i++)
            {
                var content = new MultipartFormDataContent();
                content.Add(new StringContent($"bench_s_{i}.dat"), "path");
                content.Add(new ByteArrayContent(smallData), "file", $"bench_s_{i}.dat");
                var resp = await s_http.PostAsync($"{baseUrl}/api/files/upload", content);
                resp.EnsureSuccessStatusCode();
            }
            sw.Stop();
            AddResult(results, counter, "share_upload_1kb", "Upload 1KB", sw.ElapsedMilliseconds, uploadIters, 1024);

            // Upload 100KB
            var medData = new byte[100 * 1024];
            Random.Shared.NextBytes(medData);
            const int medIters = 20;
            sw.Restart();
            for (int i = 0; i < medIters; i++)
            {
                var content = new MultipartFormDataContent();
                content.Add(new StringContent($"bench_m_{i}.dat"), "path");
                content.Add(new ByteArrayContent(medData), "file", $"bench_m_{i}.dat");
                var resp = await s_http.PostAsync($"{baseUrl}/api/files/upload", content);
                resp.EnsureSuccessStatusCode();
            }
            sw.Stop();
            AddResult(results, counter, "share_upload_100kb", "Upload 100KB", sw.ElapsedMilliseconds, medIters, 100 * 1024);

            // Download 1KB
            sw.Restart();
            for (int i = 0; i < uploadIters; i++)
                await s_http.GetByteArrayAsync($"{baseUrl}/api/files/download/bench_s_0.dat");
            sw.Stop();
            AddResult(results, counter, "share_download_1kb", "Download 1KB", sw.ElapsedMilliseconds, uploadIters, 1024);

            // Download 100KB
            sw.Restart();
            for (int i = 0; i < medIters; i++)
                await s_http.GetByteArrayAsync($"{baseUrl}/api/files/download/bench_m_0.dat");
            sw.Stop();
            AddResult(results, counter, "share_download_100kb", "Download 100KB", sw.ElapsedMilliseconds, medIters, 100 * 1024);

            // List populated
            sw.Restart();
            for (int i = 0; i < listIters; i++)
                await s_http.GetStringAsync($"{baseUrl}/api/files?path=");
            sw.Stop();
            AddResult(results, counter, "share_list_populated", "File list (populated)", sw.ElapsedMilliseconds, listIters);

            // Cleanup
            for (int i = 0; i < uploadIters; i++)
                try { await s_http.DeleteAsync($"{baseUrl}/api/files/bench_s_{i}.dat"); } catch { /* benchmark cleanup — file may not exist */ }
            for (int i = 0; i < medIters; i++)
                try { await s_http.DeleteAsync($"{baseUrl}/api/files/bench_m_{i}.dat"); } catch { /* benchmark cleanup — file may not exist */ }
        }
        catch (Exception ex)
        {
            AddResult(results, counter, "share_error", $"File server failed: {ex.Message}", 0);
        }
    }

    // ─── SCREEN CAPTURE ──────────────────────────────────────────────

    private static async Task BenchmarkScreenCapture(NdaPayload results, BenchCounter counter, IScreenCapture screen)
    {
        try
        {
            const int iterations = 10;
            var times = new List<double>();

            await screen.CaptureScreenAsync(); // warmup

            var totalSw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                var sw = Stopwatch.StartNew();
                var data = await screen.CaptureScreenAsync();
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
                if (i == 0)
                    AddResult(results, counter, "screen_frame_size", "Single capture size", 0, 1, data.Length);
            }
            totalSw.Stop();

            AddResult(results, counter, "screen_capture_avg", "Screen capture avg", (long)times.Average(), iterations);
            AddResult(results, counter, "screen_capture_min", "Screen capture min", (long)times.Min());
            AddResult(results, counter, "screen_capture_max", "Screen capture max", (long)times.Max());
            AddResult(results, counter, "screen_fps", "Screen FPS", (long)(iterations / totalSw.Elapsed.TotalSeconds), iterations);

            var swSize = Stopwatch.StartNew();
            for (int i = 0; i < 100; i++)
                await screen.GetScreenSizeAsync();
            swSize.Stop();
            AddResult(results, counter, "screen_size_query", "Screen size query", swSize.ElapsedMilliseconds, 100);
        }
        catch (Exception ex)
        {
            AddResult(results, counter, "screen_error", $"Screen capture failed: {ex.Message}", 0);
        }
    }


    // ─── DELTA ENGINE ────────────────────────────────────────────────

    private static async Task BenchmarkDelta(NdaPayload results, BenchCounter counter, IScreenCapture screen)
    {
        try
        {
            // 1. Check native availability
            var swAvail = Stopwatch.StartNew();
            bool avail = false;
            for (int i = 0; i < 100; i++)
                avail = DeltaEngine.IsNativeAvailable;
            swAvail.Stop();
            AddResult(results, counter, "delta_native_check", "DeltaEngine.IsNativeAvailable", swAvail.ElapsedMilliseconds, 100);
            AddResult(results, counter, "delta_available", avail ? "yes" : "no", 0);

            if (!avail) return;

            // 2. Get screen dimensions
            var (screenW, screenH) = await screen.GetScreenSizeAsync();
            if (screenW <= 0 || screenH <= 0) { AddResult(results, counter, "delta_error", "Invalid screen size", 0); return; }



            // 4. Delta pipeline benchmark
            using var pipeline = new Drone.Services.Remote.DeltaScreenPipeline(screen, new DeltaBenchLogger());

            if (!pipeline.IsDeltaAvailable)
            {
                AddResult(results, counter, "delta_pipeline", "Not available", 0);
                return;
            }
            AddResult(results, counter, "delta_pipeline", "Available", 0);

            const int warmup = 3;
            const int iterations = 10;

            // Warmup
            for (int i = 0; i < warmup; i++)
                await pipeline.CaptureAndDeltaAsync();

            // Measure delta frames
            var deltaTimes = new List<double>();
            long totalWireBytes = 0;
            int totalRects = 0;
            long totalRawBytes = 0;

            for (int i = 0; i < iterations; i++)
            {
                var sw = Stopwatch.StartNew();
                var (wireLen, isDelta, rectCount, rawBytes) = await pipeline.CaptureAndDeltaAsync();
                sw.Stop();

                deltaTimes.Add(sw.Elapsed.TotalMilliseconds);
                totalWireBytes += wireLen;
                totalRects += rectCount;
                totalRawBytes += rawBytes;

                if (i == 0)
                {
                    AddResult(results, counter, "delta_first_wire", "First delta frame wire size", 0, 1, wireLen);
                    AddResult(results, counter, "delta_first_rects", "First delta rect count", 0, 1, rectCount);
                    AddResult(results, counter, "delta_first_raw", "First delta raw pixel bytes", 0, 1, rawBytes);
                    AddResult(results, counter, "delta_first_is_delta", isDelta ? "true" : "false", 0);
                }
            }

            // Also measure full BMP for comparison
            var bmpTimes = new List<double>();
            long bmpSize = 0;
            for (int i = 0; i < iterations; i++)
            {
                var sw = Stopwatch.StartNew();
                var bmpData = await screen.CaptureScreenAsync();
                sw.Stop();
                bmpTimes.Add(sw.Elapsed.TotalMilliseconds);
                if (i == 0) bmpSize = bmpData.Length;
            }

            AddResult(results, counter, "delta_avg_ms", "Delta pipeline avg", (long)deltaTimes.Average(), iterations);
            AddResult(results, counter, "delta_min_ms", "Delta pipeline min", (long)deltaTimes.Min());
            AddResult(results, counter, "delta_max_ms", "Delta pipeline max", (long)deltaTimes.Max());
            AddResult(results, counter, "delta_avg_wire", "Avg delta wire size", totalWireBytes / iterations);
            AddResult(results, counter, "delta_avg_rects", "Avg delta rects", totalRects / iterations);
            AddResult(results, counter, "delta_avg_raw", "Avg delta raw pixels", totalRawBytes / iterations);
            AddResult(results, counter, "delta_bmp_size", "Full BMP size (comparison)", 0, 1, bmpSize);
            AddResult(results, counter, "bmp_avg_ms", "Full BMP capture avg", (long)bmpTimes.Average(), iterations);

            if (bmpSize > 0 && totalWireBytes > 0)
            {
                var avgWire = totalWireBytes / iterations;
                var ratio = (double)bmpSize / avgWire;
                AddResult(results, counter, "delta_compression_ratio", $"BMP/Delta ratio: {ratio:F1}x", 0);
            }
        }
        catch (Exception ex)
        {
            AddResult(results, counter, "delta_error", $"Delta benchmark failed: {ex.Message}", 0);
        }
    }

    private sealed class DeltaBenchLogger : Drone.Core.ILogger
    {
        public void LogInformation(string message, params object[] args) { }
        public void LogWarning(string message, params object[] args) { }
        public void LogError(string message, params object[] args) { }
        public void LogDebug(string message, params object[] args) { }
    }
    // ─── WINDOW ENUMERATION ──────────────────────────────────────────

    private static async Task BenchmarkWindowEnum(NdaPayload results, BenchCounter counter, IWindowManager windows)
    {
        try
        {
            const int iterations = 100;
            int windowCount = 0;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                var wins = await windows.ListWindowsAsync();
                if (i == 0) windowCount = wins.Length;
            }
            sw.Stop();
            AddResult(results, counter, "window_enum", "Window enumeration", sw.ElapsedMilliseconds, iterations, windowCount);
        }
        catch (Exception ex)
        {
            AddResult(results, counter, "window_error", $"Window enum failed: {ex.Message}", 0);
        }
    }

    // ─── PROCESS LISTING ─────────────────────────────────────────────

    private static async Task BenchmarkProcessList(NdaPayload results, BenchCounter counter, IProcessManager process)
    {
        try
        {
            const int iterations = 50;
            int procCount = 0;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                var procs = await process.ListProcessesAsync();
                if (i == 0) procCount = procs.Length;
            }
            sw.Stop();
            AddResult(results, counter, "process_list", "Process listing", sw.ElapsedMilliseconds, iterations, procCount);
        }
        catch (Exception ex)
        {
            AddResult(results, counter, "process_error", $"Process list failed: {ex.Message}", 0);
        }
    }

    // ─── SYSTEM INFO ─────────────────────────────────────────────────

    private static async Task BenchmarkSystemInfo(NdaPayload results, BenchCounter counter, IProcessManager process)
    {
        try
        {
            const int iterations = 20;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
                await process.GetSystemInfoAsync();
            sw.Stop();
            AddResult(results, counter, "sysinfo", "System info query", sw.ElapsedMilliseconds, iterations);
        }
        catch (Exception ex)
        {
            AddResult(results, counter, "sysinfo_error", $"System info failed: {ex.Message}", 0);
        }
    }

    // ─── HELPERS ──────────────────────────────────────────────────────

    private sealed class BenchCounter { public int Value; }

    private static void AddResult(NdaPayload results, BenchCounter counter, string name, string description, long elapsedMs, int iterations = 0, long dataSize = 0)
    {
        var prefix = $"b{counter.Value++}";
        results.Triples.Add(new NdaTriple(prefix, "name", name));
        results.Triples.Add(new NdaTriple(prefix, "desc", description));
        results.Triples.Add(new NdaTriple(prefix, "elapsedMs", elapsedMs.ToString()));
        if (iterations > 0)
            results.Triples.Add(new NdaTriple(prefix, "iterations", iterations.ToString()));
        if (iterations > 0 && elapsedMs > 0)
        {
            var perOp = (double)elapsedMs / iterations;
            results.Triples.Add(new NdaTriple(prefix, "perOpMs", perOp.ToString("F4")));
        }
        if (dataSize > 0)
            results.Triples.Add(new NdaTriple(prefix, "dataSize", dataSize.ToString()));
    }
}
