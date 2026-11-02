using System.Buffers.Binary;
using System.Text;

using static System.FormattableString;

namespace SweetHide.Forensics.Containers;

/// <summary>Walks GIF blocks (extensions, images) up to the trailer.</summary>
internal static class GifWalker
{
    public static WalkResult Walk(ReadOnlySpan<byte> data, ContainerInspectorOptions options)
    {
        var result = new WalkResult(options.MaxStructureItems, data.Length);
        if (data.Length < 13 || !(data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)))
        {
            result.Warn("Missing GIF header.");
            return result;
        }

        var width = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        var packed = data[10];
        result.Add(new StructureItem("Header", 0, 13, 0, 13, Invariant($"{Encoding.ASCII.GetString(data[..6])}, screen {width}x{height}"), StructureTraits.None));

        var offset = 13;
        if ((packed & 0x80) != 0)
        {
            var tableSize = 3 << ((packed & 7) + 1);
            result.Add(new StructureItem("Global colour table", offset, tableSize, offset, tableSize, Invariant($"{tableSize / 3} colours"), StructureTraits.None));
            offset += tableSize;
        }

        var frames = 0;
        while (true)
        {
            if (offset >= data.Length)
            {
                result.Warn("No trailer (0x3B): the file is truncated.");
                break;
            }

            var introducer = data[offset];
            if (introducer == 0x3B)
            {
                result.Add(new StructureItem("Trailer", offset, 1, offset + 1, 0, "end of GIF", StructureTraits.None));
                result.End = offset + 1;
                break;
            }

            if (introducer == 0x21 && offset + 1 < data.Length)
            {
                var label = data[offset + 1];
                var (end, payload, truncated) = SubBlocks(data, offset + 2);
                var (name, flags) = label switch
                {
                    0xF9 => ("Graphic control", StructureTraits.None),
                    0xFE => ("Comment", StructureTraits.Metadata),
                    0x01 => ("Plain text", StructureTraits.Metadata),
                    0xFF => ("Application", StructureTraits.Metadata),
                    _ => (Invariant($"Extension 0x{label:X2}"), StructureTraits.Unknown | StructureTraits.Suspicious),
                };
                var description = label == 0xFF && offset + 14 <= data.Length && data[offset + 2] == 11
                    ? Invariant($"{Printable(data.Slice(offset + 3, 11))}, {payload} data bytes")
                    : Invariant($"{payload} data bytes");
                AddBlock(result, name, offset, end, offset + 2, description, flags, truncated);
                if (truncated)
                {
                    break;
                }

                offset = end;
                continue;
            }

            if (introducer == 0x2C && offset + 10 <= data.Length)
            {
                var descriptor = data.Slice(offset, 10);
                var imagePacked = descriptor[9];
                var dataStart = offset + 10;
                if ((imagePacked & 0x80) != 0)
                {
                    dataStart += 3 << ((imagePacked & 7) + 1);
                }

                dataStart += 1; // LZW minimum code size
                var (end, payload, truncated) = dataStart <= data.Length ? SubBlocks(data, dataStart) : (data.Length, 0, true);
                frames++;
                var description = Invariant($"frame {frames}: {BinaryPrimitives.ReadUInt16LittleEndian(descriptor[5..])}x{BinaryPrimitives.ReadUInt16LittleEndian(descriptor[7..])} at ({BinaryPrimitives.ReadUInt16LittleEndian(descriptor[1..])},{BinaryPrimitives.ReadUInt16LittleEndian(descriptor[3..])}), {payload} bytes of LZW data");
                AddBlock(result, "Image", offset, end, Math.Min(dataStart, data.Length), description, StructureTraits.None, truncated);
                if (truncated)
                {
                    break;
                }

                offset = end;
                continue;
            }

            result.Warn(Invariant($"Unknown block 0x{introducer:X2} at 0x{offset:X}; stopped parsing."));
            break;
        }

        return result;
    }

    /// <summary>Data length of a GIF at the start of <paramref name="data"/>, if its trailer can be found.</summary>
    public static long? MeasureLength(ReadOnlySpan<byte> data) => Walk(data, new ContainerInspectorOptions { MaxStructureItems = 0 }).End;

    /// <summary>Follows a chain of length-prefixed sub-blocks. Returns the offset after the terminator.</summary>
    internal static (int End, int PayloadBytes, bool Truncated) SubBlocks(ReadOnlySpan<byte> data, int offset)
    {
        var payload = 0;
        while (offset < data.Length)
        {
            var size = data[offset];
            offset++;
            if (size == 0)
            {
                return (offset, payload, false);
            }

            payload += size;
            offset += size;
        }

        return (data.Length, payload, true);
    }

    /// <summary>Concatenates sub-block payloads, up to <paramref name="maxBytes"/>.</summary>
    internal static byte[] ReadSubBlocks(ReadOnlySpan<byte> data, int offset, int maxBytes)
    {
        using var buffer = new MemoryStream();
        while (offset < data.Length && buffer.Length < maxBytes)
        {
            var size = data[offset];
            offset++;
            if (size == 0)
            {
                break;
            }

            var available = Math.Min(size, data.Length - offset);
            buffer.Write(data.Slice(offset, (int)Math.Min(available, maxBytes - buffer.Length)));
            offset += size;
        }

        return buffer.ToArray();
    }

    private static void AddBlock(WalkResult result, string name, int offset, int end, int dataOffset, string description, StructureTraits flags, bool truncated)
    {
        if (truncated)
        {
            flags |= StructureTraits.Truncated | StructureTraits.Suspicious;
            result.Warn(Invariant($"{name} block at 0x{offset:X} runs past the end of the file."));
        }

        result.Add(new StructureItem(name, offset, end - offset, dataOffset, end - dataOffset, description, flags));
    }

    private static string Printable(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            builder.Append(b is >= 0x20 and <= 0x7E ? (char)b : '.');
        }

        return builder.ToString();
    }
}
