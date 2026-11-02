using System.Buffers.Binary;
using System.Text;

using static System.FormattableString;

namespace SweetHide.Forensics.Containers;

/// <summary>Walks RIFF chunks (WebP, WAV, AVI). The RIFF size field marks the logical end of the file.</summary>
internal static class RiffWalker
{
    private static readonly HashSet<string> WebPChunks = new(StringComparer.Ordinal) { "VP8 ", "VP8L", "VP8X", "ALPH", "ANIM", "ANMF", "ICCP", "EXIF", "XMP " };

    private static readonly HashSet<string> WaveChunks = new(StringComparer.Ordinal)
    {
        "fmt ", "data", "fact", "LIST", "cue ", "smpl", "inst", "bext", "iXML", "id3 ", "ID3 ", "PEAK", "JUNK", "junk", "acid", "plst", "labl", "note", "ltxt", "wavl", "slnt", "cart", "umid", "ds64", "FLLR", "PAD ",
    };

    public static WalkResult Walk(ReadOnlySpan<byte> data, ContainerInspectorOptions options)
    {
        var result = new WalkResult(options.MaxStructureItems);
        if (data.Length < 12 || !data.StartsWith("RIFF"u8))
        {
            result.Warn("Missing RIFF header.");
            return result;
        }

        var declared = (long)BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) + 8;
        var form = Encoding.ASCII.GetString(data.Slice(8, 4));
        result.Add(new StructureItem("RIFF", 0, 12, 8, Math.Max(0, declared - 8), Invariant($"form \"{form}\", declares {declared} bytes"), StructureTraits.None));

        var known = form switch
        {
            "WEBP" => WebPChunks,
            "WAVE" => WaveChunks,
            _ => null,
        };

        var end = (int)Math.Min(declared, data.Length);
        if (declared > data.Length)
        {
            result.Warn(Invariant($"The RIFF header declares {declared} bytes but the file has {data.Length}."));
        }

        var offset = 12;
        while (offset + 8 <= end)
        {
            var id = Encoding.ASCII.GetString(data.Slice(offset, 4));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);
            var chunkEnd = (long)offset + 8 + size;
            var flags = known is not null && !known.Contains(id) ? StructureTraits.Unknown | StructureTraits.Suspicious : StructureTraits.None;
            if (id is "EXIF" or "XMP " or "LIST" or "iXML" or "bext" or "id3 " or "ID3 ")
            {
                flags |= StructureTraits.Metadata;
            }

            if (chunkEnd > end)
            {
                result.Add(new StructureItem(id, offset, end - offset, offset + 8, Math.Max(0, end - offset - 8), Invariant($"declares {size} bytes but the RIFF data ends first"), flags | StructureTraits.Truncated | StructureTraits.Suspicious));
                result.Warn(Invariant($"Chunk \"{id}\" at 0x{offset:X} runs past the end of the RIFF data."));
                break;
            }

            var description = id == "LIST" && size >= 4
                ? Invariant($"list \"{Encoding.ASCII.GetString(data.Slice(offset + 8, 4))}\", {size} bytes")
                : Invariant($"{size} bytes");
            var padded = chunkEnd + (size & 1);
            result.Add(new StructureItem(id, offset, Math.Min(padded, end) - offset, offset + 8, size, description, flags));
            offset = (int)Math.Min(padded, end);
        }

        if (offset < end && offset + 8 > end)
        {
            result.Warn(Invariant($"{end - offset} stray byte(s) at the end of the RIFF data."));
        }

        if (declared <= data.Length)
        {
            result.End = declared;
        }

        return result;
    }

    /// <summary>Declared length of a RIFF file at the start of <paramref name="data"/>, if it fits.</summary>
    public static long? MeasureLength(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
        {
            return null;
        }

        var declared = (long)BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) + 8;
        return declared <= data.Length ? declared : null;
    }
}
