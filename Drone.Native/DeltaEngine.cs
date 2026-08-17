using global::System.Runtime.InteropServices;

namespace Drone.Native;

/// <summary>
/// Zero-copy delta engine for motion-compensated frame diffing.
/// Wraps the native Rust velocity_delta.dll.
/// Ported from Velocity-Remote's DeltaEngine.
/// </summary>
public sealed class DeltaEngine : IDisposable
{
    private const string DeltaLib = "velocity_delta";

    [StructLayout(LayoutKind.Sequential)]
    public struct DeltaRect
    {
        public ushort X;
        public ushort Y;
        public ushort Width;
        public ushort Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MotionRegion
    {
        public ushort SrcX;
        public ushort SrcY;
        public ushort Width;
        public ushort Height;
        public short Dx;
        public short Dy;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DeltaResult
    {
        public short GlobalShiftDx;
        public short GlobalShiftDy;
        public ushort RectCount;
        public ushort MotionCount;
        public uint TotalPixelBytes;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public DeltaRect[] Rects;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public MotionRegion[] Motions;
    }

    [DllImport(DeltaLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr v2_delta_engine_new(uint width, uint height, uint stride);

    [DllImport(DeltaLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void v2_delta_engine_free(IntPtr ptr);

    [DllImport(DeltaLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int v2_delta_process_frame(IntPtr engine, IntPtr src, uint srcStride, ref DeltaResult result);

    [DllImport(DeltaLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr v2_delta_get_frame_ptr(IntPtr engine);

    [DllImport(DeltaLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int v2_delta_apply_rect(IntPtr engine, uint x, uint y, uint width, uint height, IntPtr pixels, uint pixelStride);

    [DllImport(DeltaLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int v2_delta_apply_shift(IntPtr engine, short dx, short dy);

    [DllImport(DeltaLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int v2_delta_get_dimensions(IntPtr engine, out uint width, out uint height, out uint stride);

    [DllImport(DeltaLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong v2_delta_get_frame_count(IntPtr engine);

    private IntPtr _handle;
    private bool _disposed;
    private static volatile bool _nativeAvailable = true;

    public static bool IsNativeAvailable
    {
        get
        {
            if (!_nativeAvailable) return false;
            try
            {
                var h = v2_delta_engine_new(1, 1, 4);
                if (h != IntPtr.Zero) { v2_delta_engine_free(h); return true; }
            }
            catch (DllNotFoundException) { _nativeAvailable = false; }
            catch (EntryPointNotFoundException) { _nativeAvailable = false; }
            return false;
        }
    }

    public DeltaEngine(uint width, uint height, uint stride)
    {
        _handle = v2_delta_engine_new(width, height, stride);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create native delta engine");
    }

    /// <summary>Zero-alloc: process frame into pre-allocated result.</summary>
    public unsafe void ProcessFrameInto(byte* src, uint srcStride, ref DeltaResult result)
    {
        ThrowIfDisposed();
        int rc = v2_delta_process_frame(_handle, (IntPtr)src, srcStride, ref result);
        if (rc != 0)
            throw new InvalidOperationException($"Delta processing failed with code {rc}");
    }
    public unsafe DeltaResult ProcessFrame(byte* src, uint srcStride)
    {
        ThrowIfDisposed();
        var result = new DeltaResult
        {
            Rects = new DeltaRect[256],
            Motions = new MotionRegion[64],
        };
        int rc = v2_delta_process_frame(_handle, (IntPtr)src, srcStride, ref result);
        if (rc != 0)
            throw new InvalidOperationException($"Delta processing failed with code {rc}");
        return result;
    }

    public IntPtr GetFramePtr()
    {
        ThrowIfDisposed();
        return v2_delta_get_frame_ptr(_handle);
    }

    public unsafe int ApplyRect(uint x, uint y, uint width, uint height, byte* pixels, uint pixelStride)
    {
        ThrowIfDisposed();
        return v2_delta_apply_rect(_handle, x, y, width, height, (IntPtr)pixels, pixelStride);
    }

    public int ApplyShift(short dx, short dy)
    {
        ThrowIfDisposed();
        return v2_delta_apply_shift(_handle, dx, dy);
    }

    public (uint Width, uint Height, uint Stride) GetDimensions()
    {
        ThrowIfDisposed();
        v2_delta_get_dimensions(_handle, out var w, out var h, out var s);
        return (w, h, s);
    }

    public ulong FrameCount
    {
        get { ThrowIfDisposed(); return v2_delta_get_frame_count(_handle); }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            v2_delta_engine_free(_handle);
            _handle = IntPtr.Zero;
            _disposed = true;
        }
    }
}
