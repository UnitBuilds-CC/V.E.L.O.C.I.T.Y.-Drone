using global::System.Runtime.InteropServices;

namespace Drone.Native;

/// <summary>
/// P/Invoke bindings for the drone-native Rust library.
/// Falls back to managed implementations if the native library is not available.
/// </summary>
public static class NativeBindings
{
    private const string LibName = "drone_native";

    [StructLayout(LayoutKind.Sequential)]
    public struct NmcpFrameHeader
    {
        public uint Magic;
        public uint FrameType;
        public uint PayloadLen;
        public uint SequenceId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ScreenDiffResult
    {
        public uint ChangedPixels;
        public uint TotalPixels;
        public float ChangePercentage;
    }

    private static bool _nativeAvailable = true;

    /// <summary>Try to parse an NMCP header using the native Rust parser. Returns false if native lib unavailable.</summary>
    public static bool TryParseHeaderNative(byte[] data, out uint frameType, out uint payloadLen, out uint seqId)
    {
        frameType = 0; payloadLen = 0; seqId = 0;
        if (!_nativeAvailable || data.Length < 16) return false;
        try
        {
            unsafe
            {
                fixed (byte* ptr = data)
                {
                    uint ft, pl, sq;
                    var result = NmcpParseHeader(ptr, (nuint)data.Length, &ft, &pl, &sq);
                    if (result == 0) { frameType = ft; payloadLen = pl; seqId = sq; return true; }
                }
            }
        }
        catch (DllNotFoundException) { _nativeAvailable = false; }
        catch (EntryPointNotFoundException) { _nativeAvailable = false; }
        return false;
    }

    /// <summary>Compare two RGBA buffers using the native Rust screen diff.</summary>
    public static ScreenDiffResult? ScreenDiffNative(byte[] bufferA, byte[] bufferB, uint pixelCount)
    {
        if (!_nativeAvailable) return null;
        try
        {
            unsafe
            {
                fixed (byte* a = bufferA)
                fixed (byte* b = bufferB)
                {
                    return ScreenDiffRgba(a, b, pixelCount);
                }
            }
        }
        catch (DllNotFoundException) { _nativeAvailable = false; }
        return null;
    }

    [DllImport(LibName, EntryPoint = "nmcp_parse_header")]
    private static extern unsafe int NmcpParseHeader(byte* data, nuint len, uint* outFrameType, uint* outPayloadLen, uint* outSeqId);

    [DllImport(LibName, EntryPoint = "screen_diff_rgba")]
    private static extern unsafe ScreenDiffResult ScreenDiffRgba(byte* bufA, byte* bufB, uint pixelCount);
}
