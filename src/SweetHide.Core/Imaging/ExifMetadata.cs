using System.Buffers.Binary;

namespace SweetHide.Core.Imaging;

/// <summary>
/// "Keep the photo's EXIF, but not where it was taken": copies a cover's EXIF into the stego PNG without GPS and
/// without the parts that could leak or no longer fit.
/// </summary>
/// <remarks>
/// <para>
/// A stego PNG without any metadata stands out next to the camera photos it was made from; with the cover's EXIF it
/// looks like any other exported photo. What is kept is rebuilt from scratch as a small little-endian TIFF:
/// IFD0 and the Exif IFD with their plain values. What is dropped: the GPS IFD, the maker note (opaque, often full
/// of serial numbers and internal offsets), the thumbnail (it shows the original picture), embedded XMP, IPTC and ICC
/// blocks (XMP can repeat the location), the interoperability IFD and any tag that points at other data.
/// </para>
/// <para>
/// Orientation is set to 1 because the cover's pixels were already turned upright when it was loaded, and the
/// pixel dimensions are set to the stego image's real size.
/// </para>
/// </remarks>
public static class ExifMetadata
{
    /// <summary>Larger EXIF blocks are ignored.</summary>
    public const int MaxSize = 64 * 1024;

    private const ushort TagExifIfd = 0x8769;
    private const ushort TagOrientation = 0x0112;
    private const ushort TagPixelXDimension = 0xA002;
    private const ushort TagPixelYDimension = 0xA003;
    private const int MaxEntriesPerIfd = 512;

    // Tags that describe the image data layout, point elsewhere, or carry the things we promise to remove.
    private static readonly HashSet<ushort> DroppedTags =
    [
        0x0100, 0x0101, 0x0102, 0x0103, 0x0106, 0x0111, 0x0115, 0x0116, 0x0117, 0x011C, // image structure
        0x014A, // SubIFDs
        0x0201, 0x0202, // JPEG thumbnail
        0x02BC, // XMP
        0x83BB, // IPTC
        0x8773, // ICC profile
        0x8825, // GPS IFD
        0x927C, // maker note
        0xA005, // interoperability IFD
        0xEA1C, // Microsoft padding
    ];

    private static ReadOnlySpan<byte> ExifHeader => "Exif\0\0"u8;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The cover's EXIF prepared for an image of <paramref name="width"/> × <paramref name="height"/>, or null if it has none.</summary>
    public static byte[]? ForOutput(ReadOnlySpan<byte> coverFile, int width, int height)
    {
        var tiff = Extract(coverFile);
        return tiff is null ? null : Sanitize(tiff, width, height);
    }

    /// <summary>The raw EXIF (a TIFF structure) of a JPEG, PNG or WebP file, or null when there is none.</summary>
    public static byte[]? Extract(ReadOnlySpan<byte> file)
    {
        var exif = file switch
        {
            [0xFF, 0xD8, ..] => FromJpeg(file),
            _ when file.StartsWith(PngSignature) => FromPng(file),
            _ when file.Length >= 12 && file.StartsWith("RIFF"u8) && file[8..12].SequenceEqual("WEBP"u8) => FromWebP(file),
            _ => default,
        };

        if (exif.StartsWith(ExifHeader))
        {
            exif = exif[ExifHeader.Length..];
        }

        return exif.IsEmpty || exif.Length > MaxSize ? null : exif.ToArray();
    }

    /// <summary>
    /// A rebuilt copy of <paramref name="tiff"/> without GPS, maker note, thumbnail or embedded blocks, with
    /// orientation 1 and the given pixel size; null if it can't be parsed or nothing worth keeping is left.
    /// </summary>
    public static byte[]? Sanitize(ReadOnlySpan<byte> tiff, int width, int height)
    {
        try
        {
            var reader = new TiffReader(tiff.ToArray());
            var ifd0 = reader.ReadIfd(reader.FirstIfdOffset);
            var exifPointer = ifd0.FirstOrDefault(e => e.Tag == TagExifIfd);
            var exif = exifPointer is not null && exifPointer.Count == 1 && exifPointer.Type is 4 or 13
                ? reader.ReadIfd(reader.ReadUInt32(exifPointer.Value, 0))
                : [];

            var kept0 = Clean(ifd0, width, height);
            var keptExif = Clean(exif, width, height);
            return kept0.Count == 0 && keptExif.Count == 0 ? null : Write(kept0, keptExif);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Returns <paramref name="png"/> with an <c>eXIf</c> chunk right after IHDR (replacing any existing one).</summary>
    public static byte[] AddToPng(ReadOnlySpan<byte> png, ReadOnlySpan<byte> tiff)
    {
        if (!png.StartsWith(PngSignature) || png.Length < 33 || !png.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            throw new InvalidImageException("Not a PNG file.");
        }

        if (tiff.IsEmpty || tiff.Length > MaxSize)
        {
            throw new ArgumentException("EXIF block is empty or too large.", nameof(tiff));
        }

        using var output = new MemoryStream(png.Length + tiff.Length + 12);
        var ihdrEnd = 8 + 12 + (int)BinaryPrimitives.ReadUInt32BigEndian(png[8..]);
        output.Write(png[..ihdrEnd]);
        IndexedPng.WriteChunk(output, "eXIf"u8, tiff);

        var offset = ihdrEnd;
        while (offset + 12 <= png.Length)
        {
            var length = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(png[offset..]), int.MaxValue - 12);
            var end = (long)offset + 12 + length;
            if (end > png.Length)
            {
                throw new InvalidImageException("PNG chunk runs past the end of the file.");
            }

            if (!png.Slice(offset + 4, 4).SequenceEqual("eXIf"u8))
            {
                output.Write(png[offset..(int)end]);
            }

            offset = (int)end;
        }

        return output.ToArray();
    }

    private static List<TiffEntry> Clean(List<TiffEntry> entries, int width, int height)
    {
        var kept = new List<TiffEntry>();
        foreach (var entry in entries)
        {
            if (DroppedTags.Contains(entry.Tag) || entry.Tag == TagExifIfd || TiffReader.TypeSize(entry.Type) == 0 || entry.Type == 13)
            {
                continue;
            }

            kept.Add(entry.Tag switch
            {
                TagOrientation => TiffEntry.Short(TagOrientation, 1),
                TagPixelXDimension => TiffEntry.Long(TagPixelXDimension, (uint)width),
                TagPixelYDimension => TiffEntry.Long(TagPixelYDimension, (uint)height),
                _ => entry,
            });
        }

        return kept;
    }

    private static byte[] Write(List<TiffEntry> ifd0, List<TiffEntry> exif)
    {
        if (exif.Count > 0)
        {
            ifd0.Add(TiffEntry.Long(TagExifIfd, 0)); // patched below
        }

        ifd0.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        exif.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        using var output = new MemoryStream();
        output.Write("II*\0"u8);
        WriteUInt32(output, 8);
        var exifPointerPosition = WriteIfd(output, ifd0, TagExifIfd);
        if (exif.Count > 0)
        {
            var exifOffset = (uint)output.Position;
            WriteIfd(output, exif, null);
            var bytes = output.GetBuffer();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)exifPointerPosition), exifOffset);
        }

        return output.ToArray();
    }

    /// <summary>Writes an IFD and its out-of-line values; returns where the value of <paramref name="markTag"/> was written.</summary>
    private static long WriteIfd(MemoryStream output, List<TiffEntry> entries, ushort? markTag)
    {
        var start = output.Position;
        var dataOffset = start + 2 + (12L * entries.Count) + 4;
        var data = new MemoryStream();
        long marked = -1;
        Span<byte> inline = stackalloc byte[4];

        WriteUInt16(output, (ushort)entries.Count);
        foreach (var entry in entries)
        {
            WriteUInt16(output, entry.Tag);
            WriteUInt16(output, entry.Type);
            WriteUInt32(output, entry.Count);
            var value = entry.LittleEndianValue();
            if (entry.Tag == markTag)
            {
                marked = output.Position;
            }

            if (value.Length <= 4)
            {
                inline.Clear();
                value.CopyTo(inline);
                output.Write(inline);
            }
            else
            {
                WriteUInt32(output, (uint)(dataOffset + data.Position));
                data.Write(value);
                if (data.Position % 2 == 1)
                {
                    data.WriteByte(0);
                }
            }
        }

        WriteUInt32(output, 0); // no next IFD (the thumbnail IFD is dropped)
        data.Position = 0;
        data.CopyTo(output);
        return marked;
    }

    private static ReadOnlySpan<byte> FromJpeg(ReadOnlySpan<byte> file)
    {
        var offset = 2;
        while (offset + 4 <= file.Length && file[offset] == 0xFF)
        {
            var marker = file[offset + 1];
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                offset += 2;
                continue;
            }

            if (marker is 0xDA or 0xD9)
            {
                break; // image data starts: EXIF always comes before it
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(file[(offset + 2)..]);
            if (length < 2 || offset + 2 + length > file.Length)
            {
                break;
            }

            var segment = file.Slice(offset + 4, length - 2);
            if (marker == 0xE1 && segment.StartsWith(ExifHeader))
            {
                return segment;
            }

            offset += 2 + length;
        }

        return default;
    }

    private static ReadOnlySpan<byte> FromPng(ReadOnlySpan<byte> file)
    {
        var offset = 8;
        while (offset + 12 <= file.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(file[offset..]);
            if (length > file.Length - offset - 12)
            {
                break;
            }

            var type = file.Slice(offset + 4, 4);
            if (type.SequenceEqual("eXIf"u8))
            {
                return file.Slice(offset + 8, (int)length);
            }

            if (type.SequenceEqual("IEND"u8))
            {
                break;
            }

            offset += 12 + (int)length;
        }

        return default;
    }

    private static ReadOnlySpan<byte> FromWebP(ReadOnlySpan<byte> file)
    {
        var offset = 12;
        while (offset + 8 <= file.Length)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(file[(offset + 4)..]);
            if (size > file.Length - offset - 8)
            {
                break;
            }

            if (file.Slice(offset, 4).SequenceEqual("EXIF"u8))
            {
                return file.Slice(offset + 8, (int)size);
            }

            offset += 8 + (int)size + (int)(size & 1);
        }

        return default;
    }

    private static void WriteUInt16(Stream output, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteUInt32(Stream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        output.Write(bytes);
    }

    /// <summary>One IFD entry with its value bytes as stored in the source (in the source's byte order).</summary>
    private sealed record TiffEntry(ushort Tag, ushort Type, uint Count, byte[] Value, bool BigEndian)
    {
        public static TiffEntry Short(ushort tag, ushort value)
        {
            var bytes = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
            return new TiffEntry(tag, 3, 1, bytes, false);
        }

        public static TiffEntry Long(ushort tag, uint value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            return new TiffEntry(tag, 4, 1, bytes, false);
        }

        /// <summary>The value with every multi-byte element turned little-endian.</summary>
        public byte[] LittleEndianValue()
        {
            var copy = (byte[])Value.Clone();
            var element = Type switch
            {
                3 or 8 => 2,
                4 or 9 or 11 or 5 or 10 => 4, // rationals are two 32-bit numbers
                12 => 8,
                _ => 1,
            };

            if (BigEndian && element > 1)
            {
                for (var i = 0; i + element <= copy.Length; i += element)
                {
                    copy.AsSpan(i, element).Reverse();
                }
            }

            return copy;
        }
    }

    private sealed class TiffReader
    {
        private readonly byte[] _data;
        private readonly bool _bigEndian;

        public TiffReader(byte[] data)
        {
            _data = data;
            if (data.Length < 8)
            {
                throw new InvalidDataException("EXIF block too short.");
            }

            _bigEndian = data[0] switch
            {
                (byte)'I' when data[1] == 'I' => false,
                (byte)'M' when data[1] == 'M' => true,
                _ => throw new InvalidDataException("Not a TIFF header."),
            };

            if (ReadUInt16(2) != 42)
            {
                throw new InvalidDataException("Not a TIFF header.");
            }

            FirstIfdOffset = ReadUInt32(4);
        }

        public uint FirstIfdOffset { get; }

        public static int TypeSize(ushort type) => type switch
        {
            1 or 2 or 6 or 7 => 1,
            3 or 8 => 2,
            4 or 9 or 11 or 13 => 4,
            5 or 10 or 12 => 8,
            _ => 0,
        };

        public List<TiffEntry> ReadIfd(uint offset)
        {
            if (offset < 8 || offset > _data.Length - 2)
            {
                throw new InvalidDataException("IFD offset out of range.");
            }

            var count = ReadUInt16((int)offset);
            if (count > MaxEntriesPerIfd || offset + 2 + (12L * count) > _data.Length)
            {
                throw new InvalidDataException("IFD runs past the end of the EXIF block.");
            }

            var entries = new List<TiffEntry>(count);
            for (var i = 0; i < count; i++)
            {
                var at = (int)offset + 2 + (12 * i);
                var tag = ReadUInt16(at);
                var type = ReadUInt16(at + 2);
                var valueCount = ReadUInt32(at + 4);
                var size = TypeSize(type);
                if (size == 0)
                {
                    continue;
                }

                var total = (long)size * valueCount;
                if (total > MaxSize)
                {
                    continue;
                }

                long valueOffset = total <= 4 ? at + 8 : ReadUInt32(at + 8);
                if (valueOffset + total > _data.Length)
                {
                    continue; // a lying entry: skip it, keep the rest
                }

                entries.Add(new TiffEntry(tag, type, valueCount, _data.AsSpan((int)valueOffset, (int)total).ToArray(), _bigEndian));
            }

            return entries;
        }

        public uint ReadUInt32(byte[] bytes, int offset) =>
            _bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset)) : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));

        private ushort ReadUInt16(int offset) =>
            _bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(offset)) : BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(offset));

        private uint ReadUInt32(int offset) => ReadUInt32(_data, offset);
    }
}
