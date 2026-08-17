using global::System.Buffers.Binary;
using global::System.IO.Compression;

namespace Drone.Native;

/// <summary>
/// Zero-alloc delta frame serializer.
/// Writes into caller-provided output buffer. No per-frame allocations.
/// Compression: 0=none, 1=deflate, 2=webp
/// </summary>
public static class DeltaFrameSerializer
{
    public const byte DeltaFrameType = 0x05;
    public const byte CompressNone = 0;
    public const byte CompressDeflate = 1;
    public const byte CompressWebP = 2;

    /// <summary>
    /// Serialize delta frame directly into pre-allocated output buffer.
    /// Returns total bytes written. Zero-alloc: no new byte[] created.
    /// Uses scratchBuf for deflate temporary storage (reused across calls).
    /// </summary>
    public static unsafe int SerializeInto(
        uint frameSeq, long timestamp,
        ushort screenWidth, ushort screenHeight,
        short globalShiftDx, short globalShiftDy,
        DeltaEngine.DeltaRect[] rects, int rectCount,
        byte* framePtr, uint stride,
        byte* output, int outputCapacity,
        byte* scratchBuf, int scratchCapacity)
    {
        // Calculate total pixel bytes
        int totalPixelBytes = 0;
        for (int i = 0; i < rectCount; i++)
            totalPixelBytes += rects[i].Width * rects[i].Height * 4;

        // Extract rect pixels into scratch buffer (pre-allocated, reused)
        int rawOffset = 0;
        for (int i = 0; i < rectCount; i++)
        {
            int rx = rects[i].X, ry = rects[i].Y;
            int rw = rects[i].Width, rh = rects[i].Height;
            int rowBytes = rw * 4;
            for (int row = 0; row < rh; row++)
            {
                byte* srcRow = framePtr + (ry + row) * stride + rx * 4;
                Buffer.MemoryCopy(srcRow, scratchBuf + rawOffset, scratchCapacity - rawOffset, rowBytes);
                rawOffset += rowBytes;
            }
        }

        // Write header into output
        int headerSize = 28 + rectCount * 8;
        if (headerSize > outputCapacity) return 0;

        output[0] = DeltaFrameType;
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(output + 1, 4), frameSeq);
        BinaryPrimitives.WriteInt64LittleEndian(new Span<byte>(output + 5, 8), timestamp);
        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(output + 13, 2), screenWidth);
        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(output + 15, 2), screenHeight);
        BinaryPrimitives.WriteInt16LittleEndian(new Span<byte>(output + 17, 2), globalShiftDx);
        BinaryPrimitives.WriteInt16LittleEndian(new Span<byte>(output + 19, 2), globalShiftDy);
        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(output + 21, 2), (ushort)rectCount);
        BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(output + 24, 4), totalPixelBytes);

        int offset = 28;
        for (int i = 0; i < rectCount; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(output + offset, 2), rects[i].X);
            BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(output + offset + 2, 2), rects[i].Y);
            BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(output + offset + 4, 2), rects[i].Width);
            BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(output + offset + 6, 2), rects[i].Height);
            offset += 8;
        }

        int payloadCapacity = outputCapacity - headerSize;
        int payloadLen;
        byte compressed;

        // Try WebP first (best compression for BGRA)
        if (totalPixelBytes > 1024 && WebpCompressor.IsAvailable)
        {
            // WebP has max dimension limit of 16383 pixels
            // Calculate proper width/height that fit within limits
            uint totalPixels = (uint)(totalPixelBytes / 4);
            uint stripWidth = totalPixels;
            uint stripHeight = 1;
            
            // If width exceeds WebP limit, reshape into multiple rows
            if (stripWidth > 16000)
            {
                stripWidth = 16000;
                stripHeight = (totalPixels + stripWidth - 1) / stripWidth;
            }
            
            payloadLen = WebpCompressor.EncodeInto(scratchBuf, stripWidth, stripHeight, stripWidth * 4, 60f,
                output + headerSize, payloadCapacity);
            if (payloadLen > 0 && payloadLen < totalPixelBytes * 0.8)
            {
                compressed = CompressWebP;
            }
            else
            {
                // Fall through to deflate
                payloadLen = 0;
                compressed = 0;
            }
        }
        else
        {
            payloadLen = 0;
            compressed = 0;
        }

        // Try deflate if WebP didn't work
        if (payloadLen == 0 && totalPixelBytes > 256)
        {
            using var ms = new UnmanagedMemoryStream(scratchBuf, totalPixelBytes, totalPixelBytes, FileAccess.Read);
            using var compMs = new UnmanagedMemoryStream(output + headerSize, payloadCapacity, payloadCapacity, FileAccess.Write);
            using (var deflate = new DeflateStream(compMs, CompressionLevel.Fastest, leaveOpen: true))
            {
                ms.CopyTo(deflate);
            }
            payloadLen = (int)compMs.Position;
            if (payloadLen < totalPixelBytes * 0.8)
            {
                compressed = CompressDeflate;
            }
            else
            {
                payloadLen = 0;
                compressed = 0;
            }
        }

        // No compression: copy raw
        if (payloadLen == 0)
        {
            if (totalPixelBytes > payloadCapacity) return 0;
            Buffer.MemoryCopy(scratchBuf, output + headerSize, payloadCapacity, totalPixelBytes);
            payloadLen = totalPixelBytes;
            compressed = CompressNone;
        }

        output[23] = compressed;
        return headerSize + payloadLen;
    }

    /// <summary>
    /// Serialize into a new byte[] (convenience wrapper, allocates).
    /// For zero-alloc path, use SerializeInto directly.
    /// </summary>
    public static unsafe byte[] Serialize(
        uint frameSeq, long timestamp,
        ushort screenWidth, ushort screenHeight,
        short globalShiftDx, short globalShiftDy,
        DeltaEngine.DeltaRect[] rects, int rectCount,
        byte* framePtr, uint stride)
    {
        int totalPixelBytes = 0;
        for (int i = 0; i < rectCount; i++)
            totalPixelBytes += rects[i].Width * rects[i].Height * 4;

        int headerSize = 28 + rectCount * 8;
        int worstCase = headerSize + totalPixelBytes + 1024; // extra for compression overhead
        var output = new byte[worstCase];
        var scratch = new byte[totalPixelBytes];

        fixed (byte* pOut = output)
        fixed (byte* pScratch = scratch)
        {
            int len = SerializeInto(frameSeq, timestamp, screenWidth, screenHeight,
                globalShiftDx, globalShiftDy, rects, rectCount, framePtr, stride,
                pOut, worstCase, pScratch, totalPixelBytes);
            if (len < output.Length)
                Array.Resize(ref output, len);
            return output;
        }
    }
}