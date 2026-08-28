using Drone.Core;
using Drone.Core.Protocol;
using Drone.Native;
using Drone.System;

namespace Drone.Services.Remote;

/// <summary>
/// Zero-alloc delta screen pipeline.
/// All buffers pre-allocated once in constructor. Hot path produces zero GC garbage.
/// </summary>
public sealed class DeltaScreenPipeline : IDisposable
{
    private readonly IScreenCapture _screenCapture;
    private readonly ILogger _logger;
    private DeltaEngine? _engine;
    private bool _disposed;
    private uint _frameSeq;

    // Pre-allocated buffers (allocated once, reused every frame)
    private byte[]? _bgraBuffer;       // Raw BGRA capture (width * height * 4)
    private byte[]? _scratchBuffer;    // Rect pixel extraction + compression scratch
    private byte[]? _wireBuffer;       // Final wire-format output
    private int _screenW, _screenH;

    // Pre-allocated delta result (reused every frame - zero GC)
    private DeltaEngine.DeltaResult _deltaResult;

    // Frame skip detection
    private ulong _lastFrameHash;

    public bool IsDeltaAvailable => _engine != null;
    public ulong FrameCount => _engine?.FrameCount ?? 0;

    public DeltaScreenPipeline(IScreenCapture screenCapture, ILogger logger)
    {
        _screenCapture = screenCapture;
        _logger = logger;
        TryInitDelta();
    }

    private void TryInitDelta()
    {
        try
        {
            if (!DeltaEngine.IsNativeAvailable)
            {
                _logger.LogWarning("Delta engine native library not available.");
                return;
            }

            // Run async initialization on thread pool to avoid sync-over-async deadlock
            var (width, height) = Task.Run(() => _screenCapture.GetScreenSizeAsync()).GetAwaiter().GetResult();
            if (width <= 0 || height <= 0) return;

            _screenW = width;
            _screenH = height;
            _engine = new DeltaEngine((uint)width, (uint)height, (uint)(width * 4));
            _deltaResult = new DeltaEngine.DeltaResult { Rects = new DeltaEngine.DeltaRect[256], Motions = new DeltaEngine.MotionRegion[64] };

            // Pre-allocate all buffers ONCE
            int bgraSize = width * height * 4;
            _bgraBuffer = new byte[bgraSize];
            _scratchBuffer = new byte[bgraSize]; // worst case: entire screen is one rect
            _wireBuffer = new byte[bgraSize + 4096]; // wire output (compressed should be smaller, but worst-case safe)

            _logger.LogInformation("Delta pipeline: {W}x{H}, buffers={MB}MB (zero-alloc)",
                width, height, (bgraSize * 3) / 1024 / 1024);

            if (WebpCompressor.IsAvailable)
                _logger.LogInformation("WebP compression: available");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Delta engine init failed: {Error}", ex.Message);
        }
    }

    /// <summary>Fast FNV-1a hash sampling every 512th byte for change detection.</summary>
    private static ulong HashPixels(byte[] pixels)
    {
        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < pixels.Length; i += 512)
        {
            hash ^= pixels[i];
            hash *= 1099511628211UL;
        }
        return hash;
    }

    /// <summary>
    /// Capture and produce delta frame. Zero-alloc on hot path.
    /// Returns (wireData, isDelta, rectCount, rawPixelBytes).
    /// wireData points into the pre-allocated _wireBuffer — caller must copy if needed beyond this call.
    /// </summary>
    public async Task<(int WireLen, bool IsDelta, int RectCount, int RawBytes)> CaptureAndDeltaAsync()
    {
        if (_engine != null && _bgraBuffer != null && _scratchBuffer != null && _wireBuffer != null)
        {
            try
            {
                // Ultra-cheap change detection FIRST
                _logger.LogDebug("[Delta] frameSeq={Seq}, hasChanged={Changed}", _frameSeq, _screenCapture.HasScreenChanged());
                if (_frameSeq > 0 && !_screenCapture.HasScreenChanged())
                {
                    unsafe
                    {
                        fixed (byte* pWire = _wireBuffer)
                        {
                            int len = DeltaFrameSerializer.SerializeInto(
                                Interlocked.Increment(ref _frameSeq),
                                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                (ushort)_screenW, (ushort)_screenH, 0, 0,
                                Array.Empty<DeltaEngine.DeltaRect>(), 0,
                                null, 0, pWire, _wireBuffer.Length,
                                null, 0);
                            return (len, true, 0, 0);
                        }
                    }
                }

                // Screen changed - do full capture
                var bgraResult = await _screenCapture.CaptureRawBgraAsync(_bgraBuffer);
                
                // If capture returned null, screen hasn't changed (DXGI dirty rects = 0)
                if (bgraResult == null)
                {
                    unsafe
                    {
                        fixed (byte* pWire = _wireBuffer)
                        {
                            int len = DeltaFrameSerializer.SerializeInto(
                                Interlocked.Increment(ref _frameSeq),
                                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                (ushort)_screenW, (ushort)_screenH, 0, 0,
                                Array.Empty<DeltaEngine.DeltaRect>(), 0,
                                null, 0, pWire, _wireBuffer.Length,
                                null, 0);
                            return (len, true, 0, 0);
                        }
                    }
                }
                
                if (bgraResult != null)
                {
                    var (stride, w, h) = bgraResult.Value;

                    // Frame skip: hash check
                    ulong hash = HashPixels(_bgraBuffer);
                    if (hash == _lastFrameHash && _frameSeq > 0)
                    {
                        // Heartbeat: zero-rect delta frame
                        unsafe
                        {
                            fixed (byte* pWire = _wireBuffer)
                            {
                            int len = DeltaFrameSerializer.SerializeInto(
                                Interlocked.Increment(ref _frameSeq),
                                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                (ushort)w, (ushort)h, 0, 0,
                                Array.Empty<DeltaEngine.DeltaRect>(), 0,
                                null, 0, pWire, _wireBuffer.Length,
                                null, 0);
                            return (len, true, 0, 0);
                            }
                        }
                    }
                    _lastFrameHash = hash;

                    // Re-init engine if screen size changed
                    var (ew, eh, _) = _engine.GetDimensions();
                    if (ew != (uint)w || eh != (uint)h)
                    {
                        _engine.Dispose();
                        _engine = new DeltaEngine((uint)w, (uint)h, (uint)(w * 4));
                        _deltaResult = new DeltaEngine.DeltaResult { Rects = new DeltaEngine.DeltaRect[256], Motions = new DeltaEngine.MotionRegion[64] };
                        int bgraSize = w * h * 4;
                        if (_bgraBuffer.Length < bgraSize) _bgraBuffer = new byte[bgraSize];
                        if (_scratchBuffer.Length < bgraSize) _scratchBuffer = new byte[bgraSize];
                        if (_wireBuffer.Length < bgraSize + 4096) _wireBuffer = new byte[bgraSize + 4096];
                    }

                    unsafe
                    {
                        fixed (byte* pPixels = _bgraBuffer)
                        fixed (byte* pScratch = _scratchBuffer)
                        fixed (byte* pWire = _wireBuffer)
                        {
                            _logger.LogDebug("[Delta] Processing frame");
                            _engine.ProcessFrameInto(pPixels, stride, ref _deltaResult);
                            ref var deltaResult = ref _deltaResult;
                            
                            _logger.LogDebug("[Delta] RectCount={Count}", deltaResult.RectCount);
                            // If no changes detected, send heartbeat instead of full frame
                            if (deltaResult.RectCount == 0)
                            {
                                int len = DeltaFrameSerializer.SerializeInto(
                                    Interlocked.Increment(ref _frameSeq),
                                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                    (ushort)w, (ushort)h, 0, 0,
                                    Array.Empty<DeltaEngine.DeltaRect>(), 0,
                                    null, 0, pWire, _wireBuffer.Length,
                                    null, 0);
                                _logger.LogDebug("[Delta] Sending heartbeat, wireLen={Len}", len);
                                return (len, true, 0, _bgraBuffer.Length);
                            }
                            
                            var framePtr = _engine.GetFramePtr();
                            var (_, _, frameStride) = _engine.GetDimensions();

                            int wireLen = DeltaFrameSerializer.SerializeInto(
                                Interlocked.Increment(ref _frameSeq),
                                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                (ushort)w, (ushort)h,
                                deltaResult.GlobalShiftDx, deltaResult.GlobalShiftDy,
                                deltaResult.Rects, deltaResult.RectCount,
                                (byte*)framePtr, frameStride,
                                pWire, _wireBuffer.Length,
                                pScratch, _scratchBuffer.Length);

                            int rawBytes = 0;
                            for (int i = 0; i < deltaResult.RectCount; i++)
                                rawBytes += deltaResult.Rects[i].Width * deltaResult.Rects[i].Height * 4;

                            return (wireLen, true, deltaResult.RectCount, rawBytes);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Delta capture failed: {Error}", ex.Message);
            }
        }

        // Fallback: full BMP (allocates, but this is the fallback path)
        var bmpData = await _screenCapture.CaptureScreenAsync();
        // Copy into wire buffer if possible
        if (_wireBuffer != null && bmpData.Length <= _wireBuffer.Length)
        {
            Buffer.BlockCopy(bmpData, 0, _wireBuffer, 0, bmpData.Length);
            return (bmpData.Length, false, 0, bmpData.Length);
        }
        // Extreme fallback: allocate
        _wireBuffer = new byte[bmpData.Length + 4096];
        Buffer.BlockCopy(bmpData, 0, _wireBuffer, 0, bmpData.Length);
        return (bmpData.Length, false, 0, bmpData.Length);
    }

    /// <summary>Get the pre-allocated wire buffer. Valid until next CaptureAndDeltaAsync call.</summary>
    public byte[] WireBuffer => _wireBuffer!;

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _engine?.Dispose();
            _engine = null;
        }
    }
}
