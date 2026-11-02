using System.Buffers.Binary;
using System.Text;

using static System.FormattableString;

namespace SweetHide.Forensics.Containers;

/// <summary>Walks PNG chunks with CRC verification. Never reads past the buffer, whatever the length fields say.</summary>
internal static class PngWalker
{
    public static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // Registered chunks plus a few widespread vendor ones (Apple, ImageMagick, C2PA) that are not worth flagging.
    private static readonly HashSet<string> KnownChunks = new(StringComparer.Ordinal)
    {
        "IHDR", "PLTE", "IDAT", "IEND", "tRNS", "cHRM", "gAMA", "iCCP", "sBIT", "sRGB", "cICP", "mDCV", "cLLI", "mDCv",
        "cLLi", "tEXt", "zTXt", "iTXt", "bKGD", "hIST", "pHYs", "sPLT", "eXIf", "tIME", "acTL", "fcTL", "fdAT", "oFFs",
        "pCAL", "sCAL", "sTER", "gIFg", "gIFx", "gIFt", "dSIG", "fRAc", "iDOT", "CgBI", "vpAg", "caBX",
    };

    private static readonly string[] ColourTypes = ["greyscale", "?", "RGB", "palette", "greyscale+alpha", "?", "RGBA"];

    public static WalkResult Walk(ReadOnlySpan<byte> data, ContainerInspectorOptions options)
    {
        var result = new WalkResult(options.MaxStructureItems, data.Length);
        if (!data.StartsWith(Signature))
        {
            result.Warn("Missing PNG signature.");
            return result;
        }

        result.Add(new StructureItem("Signature", 0, 8, 0, 8, "PNG signature", StructureTraits.None));
        var offset = 8;
        var index = 0;
        var idatState = 0; // 0 = none yet, 1 = in IDAT run, 2 = IDAT run ended
        while (true)
        {
            if (offset + 12 > data.Length)
            {
                result.Warn(offset < data.Length
                    ? Invariant($"Truncated chunk header at 0x{offset:X}.")
                    : "No IEND chunk: the file is truncated.");
                break;
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
            var typeBytes = data.Slice(offset + 4, 4);
            if (!IsChunkType(typeBytes))
            {
                result.Warn(Invariant($"Invalid chunk type at 0x{offset:X}; stopped parsing (corrupt file or data hidden mid-stream)."));
                break;
            }

            var type = Encoding.ASCII.GetString(typeBytes);
            var flags = TypeFlags(type);
            var end = (long)offset + 12 + length;
            if (end > data.Length)
            {
                result.Add(new StructureItem(type, offset, data.Length - offset, offset + 8, Math.Max(0, data.Length - offset - 8), Invariant($"declares {length} bytes but the file ends first"), flags | StructureTraits.Truncated | StructureTraits.Suspicious));
                result.Warn(Invariant($"Chunk {type} at 0x{offset:X} runs past the end of the file."));
                break;
            }

            var body = data.Slice(offset + 8, (int)length);
            var storedCrc = BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 8 + (int)length)..]);
            if (Crc32.Compute(data.Slice(offset + 4, 4 + (int)length)) != storedCrc)
            {
                flags |= StructureTraits.CrcMismatch | StructureTraits.Suspicious;
            }

            if (index == 0 && type != "IHDR")
            {
                result.Warn("The first chunk is not IHDR.");
            }

            if (type == "IDAT")
            {
                if (idatState == 2)
                {
                    result.Warn(Invariant($"IDAT chunks are not contiguous (another IDAT at 0x{offset:X})."));
                    flags |= StructureTraits.Suspicious;
                }

                idatState = 1;
            }
            else if (idatState == 1)
            {
                idatState = 2;
            }

            if (type == "IHDR" && length == 13 && (flags & StructureTraits.CrcMismatch) != 0)
            {
                result.CorrectedDimensions = RepairDimensions(data.Slice(offset + 4, 17), storedCrc, options.MaxRepairDimension);
            }

            result.Add(new StructureItem(type, offset, end - offset, offset + 8, length, Describe(type, body, flags), flags));
            offset = (int)end;
            index++;

            if (type == "IEND")
            {
                result.End = end;
                break;
            }
        }

        return result;
    }

    /// <summary>Data length a PNG at the start of <paramref name="data"/> occupies, if its IEND can be found.</summary>
    public static long? MeasureLength(ReadOnlySpan<byte> data) => Walk(data, new ContainerInspectorOptions { MaxStructureItems = 0, MaxRepairDimension = 0 }).End;

    private static StructureTraits TypeFlags(string type)
    {
        var flags = StructureTraits.None;
        if (char.IsAsciiLetterUpper(type[0]))
        {
            flags |= StructureTraits.Critical;
        }

        if (char.IsAsciiLetterLower(type[1]))
        {
            flags |= StructureTraits.Private;
        }

        if (!KnownChunks.Contains(type))
        {
            flags |= StructureTraits.Unknown | StructureTraits.Suspicious;
        }

        if (type is "tEXt" or "zTXt" or "iTXt" or "eXIf")
        {
            flags |= StructureTraits.Metadata;
        }

        return flags;
    }

    private static bool IsChunkType(ReadOnlySpan<byte> type)
    {
        foreach (var c in type)
        {
            if (!char.IsAsciiLetter((char)c))
            {
                return false;
            }
        }

        return true;
    }

    private static string Describe(string type, ReadOnlySpan<byte> body, StructureTraits flags)
    {
        var text = type switch
        {
            "IHDR" when body.Length == 13 => Invariant(
                $"{BinaryPrimitives.ReadUInt32BigEndian(body)}x{BinaryPrimitives.ReadUInt32BigEndian(body[4..])}, {body[8]}-bit {(body[9] < ColourTypes.Length ? ColourTypes[body[9]] : "?")}{(body[12] == 1 ? ", interlaced" : string.Empty)}"),
            "IDAT" => Invariant($"{body.Length} bytes of compressed pixel data"),
            "IEND" => body.IsEmpty ? "end of image" : Invariant($"end of image, with {body.Length} unexpected data bytes"),
            "tEXt" or "zTXt" or "iTXt" => $"text, keyword \"{Keyword(body)}\"",
            "eXIf" => Invariant($"EXIF metadata, {body.Length} bytes"),
            "iCCP" => $"ICC profile \"{Keyword(body)}\"",
            "tIME" => "last-modification time",
            _ when (flags & StructureTraits.Unknown) != 0 => Invariant($"unknown {((flags & StructureTraits.Critical) != 0 ? "critical" : "ancillary")} {((flags & StructureTraits.Private) != 0 ? "private" : "public")} chunk, {body.Length} bytes"),
            _ => Invariant($"{body.Length} bytes"),
        };

        return (flags & StructureTraits.CrcMismatch) != 0 ? text + " (CRC mismatch)" : text;
    }

    private static string Keyword(ReadOnlySpan<byte> body)
    {
        var nul = body.IndexOf((byte)0);
        var keyword = body[..(nul < 0 ? Math.Min(body.Length, 79) : Math.Min(nul, 79))];
        return Encoding.Latin1.GetString(keyword);
    }

    /// <summary>Tries every height, then every width, until the IHDR CRC matches.</summary>
    private static CorrectedDimensions? RepairDimensions(ReadOnlySpan<byte> typeAndData, uint storedCrc, int maxDimension)
    {
        Span<byte> buffer = stackalloc byte[17];
        typeAndData.CopyTo(buffer);
        var width = BinaryPrimitives.ReadUInt32BigEndian(buffer[4..]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(buffer[8..]);
        if (width > int.MaxValue || height > int.MaxValue)
        {
            return null;
        }

        for (var h = 1; h <= maxDimension; h++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer[8..], (uint)h);
            if (Crc32.Compute(buffer) == storedCrc)
            {
                return new CorrectedDimensions((int)width, h, (int)width, (int)height);
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(buffer[8..], height);
        for (var w = 1; w <= maxDimension; w++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer[4..], (uint)w);
            if (Crc32.Compute(buffer) == storedCrc)
            {
                return new CorrectedDimensions(w, (int)height, (int)width, (int)height);
            }
        }

        return null;
    }
}
