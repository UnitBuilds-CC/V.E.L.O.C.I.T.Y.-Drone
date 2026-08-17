using global::System.Buffers.Binary;
using global::System.Security.Cryptography;
using global::System.Text;

namespace Drone.Core.Protocol;

/// <summary>
/// NDA (Neural Document Architecture) payload encoder/decoder.
/// Replaces JSON with semantic triples (Subject-Predicate-Object) for zero-parse-overhead binary encoding.
/// Layout: [48-byte NDA Header][Triple Entries (6 bytes each)][String Pool (UTF-8)]
/// </summary>
public class NdaPayload
{
    public List<NdaTriple> Triples { get; } = new();
    public byte[]? RawData { get; set; }

    /// <summary>Encode this payload into bytes (header + triples + string pool).</summary>
    public byte[] Encode()
    {
        // Build string pool and resolve offsets
        var pool = new StringPool();
        var resolvedTriples = new List<(ushort s, ushort p, ushort o)>();
        foreach (var t in Triples)
        {
            resolvedTriples.Add((
                pool.Add(t.Subject),
                pool.Add(t.Predicate),
                pool.Add(t.Object)
            ));
        }

        var stringPoolBytes = pool.ToBytes();
        var tripleSectionLen = resolvedTriples.Count * 6;
        var tripleSectionOffset = NdaHeader.Size + (RawData?.Length ?? 0);
        var stringPoolOffset = (ushort)(tripleSectionOffset + tripleSectionLen);

        // Build header
        var header = new NdaHeader
        {
            Magic = NdaHeader.NdaMagic,
            Flags = (uint)(RawData != null ? 0x1 : 0x0), // flag bit 0 = has raw data
            MerkleRoot = new byte[32],
            TripleCount = (uint)resolvedTriples.Count,
            CommandCount = (ushort)(RawData?.Length ?? 0),
            StringPoolOffset = stringPoolOffset
        };
        header.ComputeMerkle();

        // Assemble: header + raw data + triples + string pool
        var totalLen = NdaHeader.Size + (RawData?.Length ?? 0) + tripleSectionLen + stringPoolBytes.Length;
        var buffer = new byte[totalLen];
        var span = buffer.AsSpan();

        header.Write(span);

        var pos = NdaHeader.Size;
        if (RawData != null)
        {
            RawData.CopyTo(buffer, pos);
            pos += RawData.Length;
        }

        foreach (var (s, p, o) in resolvedTriples)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span[pos..], s);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(pos + 2)..], p);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(pos + 4)..], o);
            pos += 6;
        }

        stringPoolBytes.CopyTo(buffer, pos);
        return buffer;
    }

    /// <summary>Decode an NDA payload from bytes.</summary>
    public static NdaPayload Decode(byte[] data)
    {
        var payload = new NdaPayload();
        if (data.Length < NdaHeader.Size) return payload;

        var header = NdaHeader.Read(data.AsSpan(0, NdaHeader.Size));
        if (!header.IsValid) return payload;

        // Extract raw data if flagged
        if ((header.Flags & 0x1) != 0 && header.CommandCount > 0)
        {
            payload.RawData = new byte[header.CommandCount];
            Buffer.BlockCopy(data, NdaHeader.Size, payload.RawData, 0, header.CommandCount);
        }

        // Parse string pool
        var poolStart = header.StringPoolOffset;
        var poolLen = data.Length - poolStart;
        var poolBytes = new byte[poolLen];
        Buffer.BlockCopy(data, poolStart, poolBytes, 0, poolLen);
        var poolStr = Encoding.UTF8.GetString(poolBytes);
        var poolEntries = poolStr.Split('\0', StringSplitOptions.None);

        // Parse triples
        var tripleStart = NdaHeader.Size + (payload.RawData?.Length ?? 0);
        var tripleCount = (int)header.TripleCount;
        for (int i = 0; i < tripleCount; i++)
        {
            var offset = tripleStart + i * 6;
            if (offset + 6 > data.Length) break;
            var sIdx = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
            var pIdx = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2));
            var oIdx = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4));

            var s = sIdx < poolEntries.Length ? poolEntries[sIdx] : "";
            var p = pIdx < poolEntries.Length ? poolEntries[pIdx] : "";
            var o = oIdx < poolEntries.Length ? poolEntries[oIdx] : "";
            payload.Triples.Add(new NdaTriple(s, p, o));
        }

        return payload;
    }

    /// <summary>Helper: create a simple payload with one triple.</summary>
    public static byte[] SingleTriple(string subject, string predicate, string obj)
    {
        var p = new NdaPayload();
        p.Triples.Add(new NdaTriple(subject, predicate, obj));
        return p.Encode();
    }

    /// <summary>Helper: find first triple with given subject.</summary>
    public NdaTriple? FindTriple(string subject) =>
        Triples.FirstOrDefault(t => t.Subject == subject);

    /// <summary>Helper: get object value for a subject.</summary>
    public string? GetValue(string subject) => FindTriple(subject)?.Object;
}

public record NdaTriple(string Subject, string Predicate, string Object);

/// <summary>
/// NDA 48-byte header. Blittable, memory-aligned.
/// </summary>
public struct NdaHeader
{
    public const uint NdaMagic = 0x3141444E; // "NDA1"
    public const int Size = 48;

    public uint Magic;              // 4 bytes [0..3]
    public uint Flags;              // 4 bytes [4..7]
    public byte[] MerkleRoot;       // 32 bytes [8..39]
    public uint TripleCount;        // 4 bytes [40..43]
    public ushort CommandCount;     // 2 bytes [44..45]
    public ushort StringPoolOffset; // 2 bytes [46..47]

    public readonly bool IsValid => Magic == NdaMagic;

    public void Write(Span<byte> buffer)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[0..4], Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..8], Flags);
        if (MerkleRoot != null && MerkleRoot.Length >= 32)
            MerkleRoot.AsSpan(0, 32).CopyTo(buffer[8..40]);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[40..44], TripleCount);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[44..46], CommandCount);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[46..48], StringPoolOffset);
    }

    public static NdaHeader Read(ReadOnlySpan<byte> buffer)
    {
        var h = new NdaHeader { MerkleRoot = new byte[32] };
        h.Magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer[0..4]);
        h.Flags = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..8]);
        buffer[8..40].CopyTo(h.MerkleRoot);
        h.TripleCount = BinaryPrimitives.ReadUInt32LittleEndian(buffer[40..44]);
        h.CommandCount = BinaryPrimitives.ReadUInt16LittleEndian(buffer[44..46]);
        h.StringPoolOffset = BinaryPrimitives.ReadUInt16LittleEndian(buffer[46..48]);
        return h;
    }

    public void ComputeMerkle()
    {
        using var sha = SHA256.Create();
        using var ms = new MemoryStream();
        ms.Write(Magic.ToUInt32Bytes());
        ms.Write(Flags.ToUInt32Bytes());
        ms.Write(TripleCount.ToUInt32Bytes());
        ms.Write(CommandCount.ToUInt16Bytes());
        ms.Write(StringPoolOffset.ToUInt16Bytes());
        var hash = sha.ComputeHash(ms.ToArray());
        MerkleRoot = new byte[32];
        hash.AsSpan(0, 32).CopyTo(MerkleRoot);
    }
}

/// <summary>Deduplicating string pool for NDA encoding.</summary>
internal class StringPool
{
    private readonly Dictionary<string, ushort> _index = new();
    private readonly List<string> _strings = new();

    public ushort Add(string s)
    {
        if (_index.TryGetValue(s, out var idx)) return idx;
        if (_strings.Count >= ushort.MaxValue) throw new InvalidOperationException("String pool overflow");
        idx = (ushort)_strings.Count;
        _strings.Add(s);
        _index[s] = idx;
        return idx;
    }

    public byte[] ToBytes()
    {
        if (_strings.Count == 0) return Array.Empty<byte>();
        var joined = string.Join('\0', _strings) + '\0';
        return Encoding.UTF8.GetBytes(joined);
    }
}

internal static class NumericExtensions
{
    public static byte[] ToUInt32Bytes(this uint v) => BitConverter.GetBytes(v);
    public static byte[] ToUInt16Bytes(this ushort v) => BitConverter.GetBytes(v);
}
