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

    /// <summary>NMCP Merkle frame header size: 4 (magic) + 32 (merkle root) = 36 bytes.</summary>
    public const int NmcpMerkleHeaderSize = 36;

    /// <summary>SHA-256 hash size in bytes.</summary>
    public const int MerkleRootSize = 32;

    private static volatile bool _nativeAvailable = true;

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

    /// <summary>
    /// Parse an NMCP Merkle frame header (4-byte magic "NMCP" + 32-byte Merkle root).
    /// Returns false if native lib unavailable or frame is invalid.
    /// </summary>
    public static bool TryParseMerkleFrame(byte[] data, byte[] merkleRoot, out uint payloadLen)
    {
        payloadLen = 0;
        if (!_nativeAvailable || data.Length < NmcpMerkleHeaderSize || merkleRoot.Length < MerkleRootSize)
            return false;
        try
        {
            unsafe
            {
                fixed (byte* ptr = data)
                fixed (byte* root = merkleRoot)
                {
                    uint pl;
                    var result = NmcpMerkleParseFrame(ptr, (nuint)data.Length, root, &pl);
                    if (result == 0) { payloadLen = pl; return true; }
                }
            }
        }
        catch (DllNotFoundException) { _nativeAvailable = false; }
        catch (EntryPointNotFoundException) { _nativeAvailable = false; }
        return false;
    }

    /// <summary>
    /// Compute a Merkle root from an array of 32-byte leaf hashes using native Rust.
    /// Returns null if native lib unavailable (caller should use managed fallback).
    /// </summary>
    public static byte[]? ComputeMerkleRootNative(byte[][] leaves)
    {
        if (!_nativeAvailable || leaves.Length == 0) return null;
        try
        {
            var root = new byte[MerkleRootSize];
            unsafe
            {
                var leafPtrs = new IntPtr[leaves.Length];
                fixed (byte* rootPtr = root)
                {
                    for (int i = 0; i < leaves.Length; i++)
                    {
                        fixed (byte* leafPtr = leaves[i])
                        {
                            leafPtrs[i] = (IntPtr)leafPtr;
                        }
                    }
                    fixed (IntPtr* ptrs = leafPtrs)
                    {
                        var result = NmcpMerkleComputeRoot((byte**)ptrs, (nuint)leaves.Length, rootPtr);
                        if (result == 0) return root;
                    }
                }
            }
        }
        catch (DllNotFoundException) { _nativeAvailable = false; }
        catch (EntryPointNotFoundException) { _nativeAvailable = false; }
        return null;
    }

    /// <summary>
    /// Verify a Merkle proof using native Rust.
    /// Returns null if native lib unavailable (caller should use managed fallback).
    /// </summary>
    public static bool? VerifyMerkleProofNative(byte[] expectedRoot, byte[] leaf, byte[][] proof, int index)
    {
        if (!_nativeAvailable) return null;
        try
        {
            unsafe
            {
                fixed (byte* rootPtr = expectedRoot)
                fixed (byte* leafPtr = leaf)
                {
                    var proofPtrs = new IntPtr[proof.Length];
                    for (int i = 0; i < proof.Length; i++)
                    {
                        fixed (byte* p = proof[i])
                        {
                            proofPtrs[i] = (IntPtr)p;
                        }
                    }
                    fixed (IntPtr* ptrs = proofPtrs)
                    {
                        return NmcpMerkleVerifyProof(rootPtr, leafPtr, (byte**)ptrs, (nuint)proof.Length, (nuint)index) == 1;
                    }
                }
            }
        }
        catch (DllNotFoundException) { _nativeAvailable = false; }
        catch (EntryPointNotFoundException) { _nativeAvailable = false; }
        return null;
    }

    // --- P/Invoke declarations ---

    [DllImport(LibName, EntryPoint = "nmcp_parse_header")]
    private static extern unsafe int NmcpParseHeader(byte* data, nuint len, uint* outFrameType, uint* outPayloadLen, uint* outSeqId);

    [DllImport(LibName, EntryPoint = "screen_diff_rgba")]
    private static extern unsafe ScreenDiffResult ScreenDiffRgba(byte* bufA, byte* bufB, uint pixelCount);

    [DllImport(LibName, EntryPoint = "nmcp_merkle_parse_frame")]
    private static extern unsafe int NmcpMerkleParseFrame(byte* data, nuint len, byte* outMerkleRoot, uint* outPayloadLen);

    [DllImport(LibName, EntryPoint = "nmcp_merkle_compute_root")]
    private static extern unsafe int NmcpMerkleComputeRoot(byte** leaves, nuint leafCount, byte* outRoot);

    [DllImport(LibName, EntryPoint = "nmcp_merkle_verify_proof")]
    private static extern unsafe int NmcpMerkleVerifyProof(byte* expectedRoot, byte* leaf, byte** proof, nuint proofLen, nuint index);
}
