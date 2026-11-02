using System.Buffers.Binary;
using System.Text;

using static System.FormattableString;

namespace SweetHide.Forensics.Containers;

/// <summary>Walks JPEG markers and segments, skipping entropy-coded scan data, up to EOI.</summary>
internal static class JpegWalker
{
    private const int MaxIdentifierLength = 40;

    public static WalkResult Walk(ReadOnlySpan<byte> data, ContainerInspectorOptions options)
    {
        var result = new WalkResult(options.MaxStructureItems);
        if (data.Length < 2 || data[0] != 0xFF || data[1] != 0xD8)
        {
            result.Warn("Missing JPEG SOI marker.");
            return result;
        }

        result.Add(new StructureItem("SOI", 0, 2, 2, 0, "start of image", StructureTraits.None));
        var offset = 2;
        var sawMpf = false;
        while (true)
        {
            if (offset + 2 > data.Length)
            {
                result.Warn("No EOI marker: the file is truncated.");
                break;
            }

            if (data[offset] != 0xFF)
            {
                result.Warn(Invariant($"Expected a marker at 0x{offset:X}; stopped parsing."));
                break;
            }

            // Any number of 0xFF fill bytes may precede a marker.
            var markerAt = offset;
            while (markerAt + 1 < data.Length && data[markerAt + 1] == 0xFF)
            {
                markerAt++;
            }

            if (markerAt + 1 >= data.Length)
            {
                result.Warn("No EOI marker: the file is truncated.");
                break;
            }

            var marker = data[markerAt + 1];
            var name = MarkerName(marker);
            if (marker == 0xD9)
            {
                result.Add(new StructureItem(name, markerAt, 2, markerAt + 2, 0, "end of image", StructureTraits.None));
                result.End = markerAt + 2;
                break;
            }

            if (marker is (>= 0xD0 and <= 0xD7) or 0x01 or 0xD8)
            {
                var stray = marker == 0xD8 ? StructureTraits.Suspicious : StructureTraits.None;
                result.Add(new StructureItem(name, markerAt, 2, markerAt + 2, 0, marker == 0xD8 ? "unexpected second SOI" : "standalone marker", stray));
                offset = markerAt + 2;
                continue;
            }

            if (markerAt + 4 > data.Length)
            {
                result.Warn(Invariant($"Truncated {name} segment at 0x{markerAt:X}."));
                break;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data[(markerAt + 2)..]);
            if (segmentLength < 2)
            {
                result.Warn(Invariant($"Invalid {name} segment length {segmentLength} at 0x{markerAt:X}; stopped parsing."));
                break;
            }

            var dataOffset = markerAt + 4;
            var dataLength = segmentLength - 2;
            var flags = Flags(marker);
            if (dataOffset + dataLength > data.Length)
            {
                result.Add(new StructureItem(name, markerAt, data.Length - markerAt, dataOffset, data.Length - dataOffset, Invariant($"declares {dataLength} bytes but the file ends first"), flags | StructureTraits.Truncated | StructureTraits.Suspicious));
                result.Warn(Invariant($"Segment {name} at 0x{markerAt:X} runs past the end of the file."));
                break;
            }

            var body = data.Slice(dataOffset, dataLength);
            var identifier = marker is >= 0xE0 and <= 0xEF ? Identifier(body) : null;
            sawMpf |= marker == 0xE2 && identifier == "MPF";
            result.Add(new StructureItem(name, markerAt, segmentLength + 2, dataOffset, dataLength, Describe(marker, body, identifier), flags));
            offset = dataOffset + dataLength;

            if (marker == 0xDA)
            {
                var scanEnd = SkipEntropyCodedData(data, offset);
                result.Add(new StructureItem("Scan data", offset, scanEnd - offset, offset, scanEnd - offset, Invariant($"{scanEnd - offset} bytes of entropy-coded data"), StructureTraits.None));
                offset = scanEnd;
            }
        }

        if (sawMpf)
        {
            result.Warn("Multi-Picture Format (MPF) segment present: extra JPEG images after EOI are expected here.");
        }

        return result;
    }

    /// <summary>Data length of a JPEG at the start of <paramref name="data"/>, if its EOI can be found.</summary>
    public static long? MeasureLength(ReadOnlySpan<byte> data) => Walk(data, new ContainerInspectorOptions { MaxStructureItems = 0 }).End;

    /// <summary>The identifier string at the start of an APPn segment ("JFIF", "Exif", "http://ns.adobe.com/xap/1.0/").</summary>
    internal static string? Identifier(ReadOnlySpan<byte> body)
    {
        var nul = body[..Math.Min(body.Length, MaxIdentifierLength)].IndexOf((byte)0);
        var id = nul < 0 ? body[..Math.Min(body.Length, MaxIdentifierLength)] : body[..nul];
        foreach (var c in id)
        {
            if (c is < 0x20 or > 0x7E)
            {
                return null;
            }
        }

        return id.IsEmpty ? null : Encoding.ASCII.GetString(id);
    }

    private static int SkipEntropyCodedData(ReadOnlySpan<byte> data, int start)
    {
        var i = start;
        while (i < data.Length)
        {
            var ff = data[i..].IndexOf((byte)0xFF);
            if (ff < 0 || i + ff + 1 >= data.Length)
            {
                return data.Length;
            }

            i += ff;
            var next = data[i + 1];
            if (next == 0x00 || next is >= 0xD0 and <= 0xD7)
            {
                i += 2; // stuffed byte or restart marker: still inside the scan
            }
            else if (next == 0xFF)
            {
                i++;
            }
            else
            {
                return i;
            }
        }

        return data.Length;
    }

    private static StructureTraits Flags(byte marker) => marker switch
    {
        0xFE => StructureTraits.Metadata,
        >= 0xE0 and <= 0xEF => StructureTraits.Metadata,
        >= 0xF0 and <= 0xFD => StructureTraits.Unknown | StructureTraits.Suspicious,
        < 0xC0 => StructureTraits.Unknown | StructureTraits.Suspicious,
        _ => StructureTraits.None,
    };

    private static string MarkerName(byte marker) => marker switch
    {
        0xD8 => "SOI",
        0xD9 => "EOI",
        0xDA => "SOS",
        0xDB => "DQT",
        0xC4 => "DHT",
        0xC8 => "JPG",
        0xCC => "DAC",
        0xDD => "DRI",
        0xDC => "DNL",
        0xDE => "DHP",
        0xDF => "EXP",
        0xFE => "COM",
        0x01 => "TEM",
        >= 0xC0 and <= 0xCF => Invariant($"SOF{marker - 0xC0}"),
        >= 0xD0 and <= 0xD7 => Invariant($"RST{marker - 0xD0}"),
        >= 0xE0 and <= 0xEF => Invariant($"APP{marker - 0xE0}"),
        >= 0xF0 and <= 0xFD => Invariant($"JPG{marker - 0xF0}"),
        _ => Invariant($"0x{marker:X2}"),
    };

    private static string Describe(byte marker, ReadOnlySpan<byte> body, string? identifier)
    {
        switch (marker)
        {
            case >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC when body.Length >= 6:
                return Invariant($"frame {BinaryPrimitives.ReadUInt16BigEndian(body[3..])}x{BinaryPrimitives.ReadUInt16BigEndian(body[1..])}, {body[5]} component(s), {body[0]}-bit");
            case 0xFE:
                return Invariant($"comment, {body.Length} bytes");
            case 0xDA:
                return "start of scan";
            case >= 0xE0 and <= 0xEF:
                var kind = identifier switch
                {
                    "Exif" => "EXIF",
                    "http://ns.adobe.com/xap/1.0/" => "XMP",
                    "http://ns.adobe.com/xmp/extension/" => "extended XMP",
                    null => "no identifier",
                    _ => identifier,
                };
                return Invariant($"{kind}, {body.Length} bytes");
            default:
                return Invariant($"{body.Length} bytes");
        }
    }
}
