using System.Runtime.InteropServices;

namespace Drone.Native;

/// <summary>
/// WebP compression via velocity_v2_ffi.dll Rust FFI.
/// Zero-alloc: writes into caller-provided output buffer.
/// </summary>
public static class WebpCompressor
{
    private const string WebpLib = "velocity_v2_ffi";

    [DllImport(WebpLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe byte* v2_encode_webp(byte* rgbaPtr, uint width, uint height, uint stride, float quality, nuint* outLen);

    [DllImport(WebpLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe void v2_free_webp(byte* ptr, nuint len);

    private static volatile bool _available = true;

    public static bool IsAvailable
    {
        get
        {
            if (!_available) return false;
            try
            {
                unsafe
                {
                    byte* probe = stackalloc byte[4] { 0, 0, 0, 255 };
                    nuint len = 0;
                    var result = v2_encode_webp(probe, 1, 1, 4, 50f, &len);
                        if (result != null && len > 0) { v2_free_webp(result, len); return true; }
                }
            }
            catch (DllNotFoundException) { _available = false; }
            catch (EntryPointNotFoundException) { _available = false; }
            return false;
        }
    }

    /// <summary>
    /// Encode BGRA pixels as WebP into a pre-allocated output buffer.
    /// Returns the number of bytes written, or 0 if encoding fails or buffer too small.
    /// Zero-alloc: no new byte[] created.
    /// </summary>
    public static unsafe int EncodeInto(byte* bgraPtr, uint width, uint height, uint stride, float quality, byte* output, int outputCapacity)
    {
        if (!_available) return 0;
        try
        {
            nuint outLen = 0;
            byte* webpPtr = v2_encode_webp(bgraPtr, width, height, stride, quality, &outLen);
            if (webpPtr == null || outLen == 0) return 0;
            if ((int)outLen > outputCapacity) { v2_free_webp(webpPtr, outLen); return 0; }

            Buffer.MemoryCopy(webpPtr, output, outputCapacity, (long)outLen);
            v2_free_webp(webpPtr, outLen);
            return (int)outLen;
        }
        catch { return 0; }
    }
}