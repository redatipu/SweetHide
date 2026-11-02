using System.Buffers.Binary;
using System.Globalization;
using System.Text;

using SweetHide.Forensics.Detection;

using static System.FormattableString;

namespace SweetHide.Forensics.Metadata;

/// <summary>
/// Reads text tags from a TIFF/EXIF block: IFD0, the Exif IFD, the GPS IFD and IFD1. Every offset and count is
/// checked against the block, IFD loops are detected and entry counts are capped.
/// </summary>
internal static class ExifReader
{
    private const int MaxEntriesPerIfd = 1000;
    private const int MaxIfds = 16;
    private const ushort ExifIfdPointer = 0x8769;
    private const ushort GpsIfdPointer = 0x8825;
    private const ushort InteropIfdPointer = 0xA005;
    private const ushort UserComment = 0x9286;
    private const ushort ThumbnailOffset = 0x0201;
    private const ushort ThumbnailLength = 0x0202;

    private static readonly Dictionary<ushort, string> MainTags = new()
    {
        [0x010D] = "DocumentName",
        [0x010E] = "ImageDescription",
        [0x010F] = "Make",
        [0x0110] = "Model",
        [0x0131] = "Software",
        [0x0132] = "DateTime",
        [0x013B] = "Artist",
        [0x013C] = "HostComputer",
        [0x8298] = "Copyright",
        [0x9003] = "DateTimeOriginal",
        [0x9004] = "DateTimeDigitized",
        [0x9010] = "OffsetTime",
        [0x9011] = "OffsetTimeOriginal",
        [0x9012] = "OffsetTimeDigitized",
        [0x9286] = "UserComment",
        [0x927C] = "MakerNote",
        [0xA420] = "ImageUniqueID",
        [0xA430] = "CameraOwnerName",
        [0xA431] = "BodySerialNumber",
        [0xA433] = "LensMake",
        [0xA434] = "LensModel",
        [0xA435] = "LensSerialNumber",
        [0x9C9B] = "XPTitle",
        [0x9C9C] = "XPComment",
        [0x9C9D] = "XPAuthor",
        [0x9C9E] = "XPKeywords",
        [0x9C9F] = "XPSubject",
    };

    private static readonly Dictionary<ushort, string> GpsTags = new()
    {
        [0x0001] = "GPSLatitudeRef",
        [0x0003] = "GPSLongitudeRef",
        [0x0008] = "GPSSatellites",
        [0x0012] = "GPSMapDatum",
        [0x001B] = "GPSProcessingMethod",
        [0x001C] = "GPSAreaInformation",
        [0x001D] = "GPSDateStamp",
    };

    /// <param name="tiff">The TIFF block ("II*\0" or "MM\0*" first).</param>
    /// <param name="fileOffset">File offset of <paramref name="tiff"/>[0], for reporting.</param>
    /// <param name="collector">Receives the fields.</param>
    public static void Read(ReadOnlySpan<byte> tiff, long fileOffset, FieldCollector collector)
    {
        if (tiff.Length < 8)
        {
            return;
        }

        bool littleEndian;
        if (tiff[0] == 'I' && tiff[1] == 'I')
        {
            littleEndian = true;
        }
        else if (tiff[0] == 'M' && tiff[1] == 'M')
        {
            littleEndian = false;
        }
        else
        {
            collector.AddText("EXIF", "Header", "not a TIFF header", fileOffset, tiff.Length, "EXIF block without a valid byte-order mark", unusual: true);
            return;
        }

        var reader = new TiffReader(tiff, littleEndian);
        if (reader.U16(2) != 42)
        {
            return;
        }

        var visited = new HashSet<uint>();
        var queue = new Queue<(uint Offset, string Name)>();
        queue.Enqueue((reader.U32(4), "IFD0"));
        var gps = new GpsPosition();
        (uint Offset, uint Length) thumbnail = default;
        while (queue.Count > 0 && visited.Count < MaxIfds && !collector.IsFull)
        {
            var (ifdOffset, ifdName) = queue.Dequeue();
            if (ifdOffset == 0 || !visited.Add(ifdOffset) || ifdOffset > tiff.Length - 2)
            {
                continue;
            }

            var count = reader.U16((int)ifdOffset);
            if (count > MaxEntriesPerIfd)
            {
                collector.AddText("EXIF " + ifdName, "Entries", Invariant($"{count} entries"), fileOffset + ifdOffset, 2, Invariant($"implausible entry count, only {MaxEntriesPerIfd} read"), unusual: true);
                count = MaxEntriesPerIfd;
            }

            var tags = ifdName == "GPS" ? GpsTags : MainTags;
            for (var i = 0; i < count; i++)
            {
                var entry = (long)ifdOffset + 2 + (12L * i);
                if (entry + 12 > tiff.Length)
                {
                    break;
                }

                var e = (int)entry;
                var tag = reader.U16(e);
                var type = reader.U16(e + 2);
                var itemCount = reader.U32(e + 4);
                var size = TypeSize(type) * (long)itemCount;
                long valueOffset = size <= 4 ? e + 8 : reader.U32(e + 8);
                if (size == 0 || valueOffset + size > tiff.Length)
                {
                    continue; // unknown type, or the value lies outside the block
                }

                var value = tiff.Slice((int)valueOffset, (int)size);
                var name = tags.TryGetValue(tag, out var known) ? known : Invariant($"Tag 0x{tag:X4}");
                var source = "EXIF " + ifdName;
                switch (tag)
                {
                    case ExifIfdPointer when ifdName == "IFD0":
                        queue.Enqueue((reader.U32(e + 8), "Exif IFD"));
                        continue;
                    case GpsIfdPointer when ifdName == "IFD0":
                        queue.Enqueue((reader.U32(e + 8), "GPS"));
                        continue;
                    case InteropIfdPointer:
                        continue;
                    case ThumbnailOffset when ifdName == "IFD1":
                        thumbnail.Offset = reader.U32(e + 8);
                        continue;
                    case ThumbnailLength when ifdName == "IFD1":
                        thumbnail.Length = reader.U32(e + 8);
                        continue;
                }

                if (ifdName == "GPS")
                {
                    gps.Collect(tag, type, value, reader);
                }

                var absolute = fileOffset + valueOffset;
                if (type == 2)
                {
                    AddAscii(collector, source, name, value, absolute);
                }
                else if (tag is >= 0x9C9B and <= 0x9C9F && ifdName == "IFD0")
                {
                    var text = Encoding.Unicode.GetString(value[..(value.Length & ~1)]).TrimEnd('\0');
                    collector.AddText(source, name, text, absolute, value.Length);
                }
                else if (tag == UserComment || (type == 7 && ifdName == "GPS"))
                {
                    AddCharsetText(collector, source, name, value, absolute, littleEndian);
                }
                else if (type is 1 or 7 && value.Length >= 4 && TextHeuristics.TextRatio(value.TrimEnd((byte)0)) >= 0.9)
                {
                    collector.AddBytes(source, name, value.TrimEnd((byte)0), absolute, "text stored in a binary tag", unusual: tag != 0x927C);
                }
            }

            if (ifdName == "IFD0")
            {
                var nextAt = (long)ifdOffset + 2 + (12L * count);
                if (nextAt + 4 <= tiff.Length)
                {
                    queue.Enqueue((reader.U32((int)nextAt), "IFD1"));
                }
            }
        }

        if (gps.ToText() is { } position)
        {
            collector.AddText("EXIF GPS", "Position", position, fileOffset, 0, "decimal degrees (latitude, longitude)");
        }

        if (thumbnail.Length > 0)
        {
            var inside = (long)thumbnail.Offset + thumbnail.Length <= tiff.Length;
            collector.AddText("EXIF IFD1", "Thumbnail", Invariant($"JPEG thumbnail, {thumbnail.Length} bytes at 0x{fileOffset + thumbnail.Offset:X}"), fileOffset + thumbnail.Offset, (int)Math.Min(thumbnail.Length, int.MaxValue), inside ? null : "points outside the EXIF block", unusual: !inside);
        }
    }

    private static void AddAscii(FieldCollector collector, string source, string name, ReadOnlySpan<byte> value, long offset)
    {
        var nul = value.IndexOf((byte)0);
        var text = nul < 0 ? value : value[..nul];
        var after = nul < 0 ? [] : value[(nul + 1)..].TrimEnd((byte)0);
        if (after.IsEmpty)
        {
            collector.AddBytes(source, name, text, offset);
            return;
        }

        // Text after the terminator is invisible to every viewer: a classic hiding spot.
        collector.AddBytes(source, name, text, offset, Invariant($"{after.Length} more bytes after the NUL terminator: \"{FieldCollector.Decode(after)}\""), unusual: true);
    }

    private static void AddCharsetText(FieldCollector collector, string source, string name, ReadOnlySpan<byte> value, long offset, bool littleEndian)
    {
        if (value.Length < 8)
        {
            collector.AddBytes(source, name, value.TrimEnd((byte)0), offset);
            return;
        }

        var charset = value[..8];
        var body = value[8..];
        string text;
        if (charset.StartsWith("UNICODE"u8))
        {
            var even = body[..(body.Length & ~1)];
            text = (littleEndian ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(even);
        }
        else
        {
            text = FieldCollector.Decode(body);
        }

        text = text.TrimEnd('\0', ' ');
        if (text.Length > 0)
        {
            collector.AddText(source, name, text, offset + 8, body.Length, binary: !charset.StartsWith("UNICODE"u8) && TextHeuristics.TextRatio(body.TrimEnd((byte)0)) < 0.9);
        }
    }

    /// <summary>Bytes per item for each TIFF type; 0 for unknown types.</summary>
    private static int TypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 or 13 => 4,
        5 or 10 or 12 => 8,
        _ => 0,
    };

    private readonly ref struct TiffReader(ReadOnlySpan<byte> data, bool littleEndian)
    {
        private readonly ReadOnlySpan<byte> _data = data;

        public ushort U16(int at) => littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(_data[at..]) : BinaryPrimitives.ReadUInt16BigEndian(_data[at..]);

        public uint U32(int at) => littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(_data[at..]) : BinaryPrimitives.ReadUInt32BigEndian(_data[at..]);

        public double Rational(ReadOnlySpan<byte> value, int index)
        {
            var at = index * 8;
            var numerator = littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(value[at..]) : BinaryPrimitives.ReadUInt32BigEndian(value[at..]);
            var denominator = littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(value[(at + 4)..]) : BinaryPrimitives.ReadUInt32BigEndian(value[(at + 4)..]);
            return denominator == 0 ? double.NaN : (double)numerator / denominator;
        }
    }

    private sealed class GpsPosition
    {
        private char _latitudeRef;
        private char _longitudeRef;
        private double? _latitude;
        private double? _longitude;

        public void Collect(ushort tag, ushort type, ReadOnlySpan<byte> value, TiffReader reader)
        {
            switch (tag)
            {
                case 1 when type == 2 && value.Length >= 1:
                    _latitudeRef = (char)value[0];
                    break;
                case 3 when type == 2 && value.Length >= 1:
                    _longitudeRef = (char)value[0];
                    break;
                case 2 when type == 5 && value.Length >= 24:
                    _latitude = reader.Rational(value, 0) + (reader.Rational(value, 1) / 60) + (reader.Rational(value, 2) / 3600);
                    break;
                case 4 when type == 5 && value.Length >= 24:
                    _longitude = reader.Rational(value, 0) + (reader.Rational(value, 1) / 60) + (reader.Rational(value, 2) / 3600);
                    break;
            }
        }

        public string? ToText()
        {
            if (_latitude is not { } lat || _longitude is not { } lon || double.IsNaN(lat) || double.IsNaN(lon))
            {
                return null;
            }

            lat = _latitudeRef == 'S' ? -lat : lat;
            lon = _longitudeRef == 'W' ? -lon : lon;
            return string.Create(CultureInfo.InvariantCulture, $"{lat:0.000000}, {lon:0.000000}");
        }
    }
}
