using System.Buffers.Binary;

using SweetHide.Forensics.Detection;

namespace SweetHide.Forensics.Containers;

/// <summary>
/// Works out how long an embedded file is from its own headers, so it can be carved without the bytes that follow.
/// One instance serves one scan over one buffer and caches the searches that would otherwise repeat per hit.
/// </summary>
internal sealed class EmbeddedLength
{
    private const int ZipEndRecordSize = 22;
    private const long BaseWorkBudget = 16 * 1024 * 1024;

    private readonly ReadOnlyMemory<byte> _file;
    private readonly long _workBudget;
    private long _work;
    private int _eocdSearchedFrom = -1;
    private int _eocdFoundAt = -1;
    private int? _lastPdfEnd;

    public EmbeddedLength(ReadOnlyMemory<byte> file)
    {
        _file = file;

        // Walking formats read up to the end of the file per hit. A hostile file with hundreds of fake headers would
        // make that quadratic, so the walks of one scan share a budget of a few passes over the file.
        _workBudget = (4L * file.Length) + BaseWorkBudget;
    }

    /// <summary>Length of the file of <paramref name="type"/> starting at <paramref name="data"/>[0], when knowable.</summary>
    public static long? Estimate(FileType type, ReadOnlyMemory<byte> data) => new EmbeddedLength(data).EstimateAt(type, 0);

    /// <returns>The length, or null when the format has no reliable end marker or the end lies outside the buffer.</returns>
    public long? EstimateAt(FileType type, int offset)
    {
        var data = _file.Span[offset..];
        var walks = type is FileType.Png or FileType.Jpeg or FileType.Gif or FileType.Bmp or FileType.Pem;
        if (walks && _work > _workBudget)
        {
            return null; // budget spent: the hit is still reported, it just carves to the end
        }

        var length = type switch
        {
            FileType.Png => PngWalker.MeasureLength(data),
            FileType.Jpeg => JpegWalker.MeasureLength(data),
            FileType.Gif => GifWalker.MeasureLength(data),
            FileType.WebP or FileType.Wav or FileType.Avi or FileType.Riff => RiffWalker.MeasureLength(data),
            FileType.Bmp => BmpWalker.MeasureLength(data),
            FileType.Zip => Zip(offset),
            FileType.Pdf => Pdf(offset),
            FileType.SevenZip => SevenZip(data),
            FileType.Sqlite => Sqlite(data),
            FileType.Elf => Elf(data),
            FileType.Pem => Pem(data),
            _ => null,
        };

        if (walks)
        {
            _work += length ?? data.Length;
        }

        return length is > 0 && length <= data.Length ? length : null;
    }

    private long? Zip(int offset)
    {
        var span = _file.Span;

        // The first end-of-central-directory record at or after `offset` is the same for every offset between the
        // last search start and the record it found, so a file full of local headers costs one pass, not one each.
        if (_eocdSearchedFrom < 0 || offset < _eocdSearchedFrom || (_eocdFoundAt >= 0 && offset > _eocdFoundAt))
        {
            var found = span[offset..].IndexOf("PK\x05\x06"u8);
            _eocdSearchedFrom = offset;
            _eocdFoundAt = found < 0 ? -1 : offset + found;
        }

        if (_eocdFoundAt < 0 || _eocdFoundAt + ZipEndRecordSize > span.Length)
        {
            return null;
        }

        var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(span[(_eocdFoundAt + 20)..]);
        return (long)_eocdFoundAt + ZipEndRecordSize + commentLength - offset;
    }

    private long? Pdf(int offset)
    {
        var span = _file.Span;
        if (_lastPdfEnd is null)
        {
            var eof = span.LastIndexOf("%%EOF"u8);
            var end = eof < 0 ? 0 : eof + 5;
            while (end > 0 && end < span.Length && span[end] is (byte)'\r' or (byte)'\n' && end - eof < 7)
            {
                end++;
            }

            _lastPdfEnd = end;
        }

        return _lastPdfEnd > offset ? _lastPdfEnd - offset : null;
    }

    private static long? SevenZip(ReadOnlySpan<byte> data)
    {
        if (data.Length < 32)
        {
            return null;
        }

        var nextHeaderOffset = BinaryPrimitives.ReadUInt64LittleEndian(data[12..]);
        var nextHeaderSize = BinaryPrimitives.ReadUInt64LittleEndian(data[20..]);
        return nextHeaderOffset > int.MaxValue || nextHeaderSize > int.MaxValue ? null : 32 + (long)nextHeaderOffset + (long)nextHeaderSize;
    }

    private static long? Sqlite(ReadOnlySpan<byte> data)
    {
        if (data.Length < 32)
        {
            return null;
        }

        var pageSize = BinaryPrimitives.ReadUInt16BigEndian(data[16..]);
        var pages = BinaryPrimitives.ReadUInt32BigEndian(data[28..]);
        return (pageSize == 1 ? 65536L : pageSize) * pages;
    }

    private static long? Elf(ReadOnlySpan<byte> data)
    {
        var is64 = data.Length > 4 && data[4] == 2;
        if (data.Length < (is64 ? 0x40 : 0x34))
        {
            return null;
        }

        var be = data[5] == 2;
        var word = is64 ? 8 : 4;
        var phOffset = Read(data, be, is64 ? 0x20 : 0x1C, word);
        var shOffset = Read(data, be, is64 ? 0x28 : 0x20, word);
        if (phOffset > int.MaxValue || shOffset > int.MaxValue)
        {
            return null;
        }

        // Offsets below 2^31 and 16-bit counts and sizes: these sums cannot overflow.
        var phEnd = phOffset + (Read(data, be, is64 ? 0x36 : 0x2A, 2) * Read(data, be, is64 ? 0x38 : 0x2C, 2));
        var shEnd = shOffset + (Read(data, be, is64 ? 0x3A : 0x2E, 2) * Read(data, be, is64 ? 0x3C : 0x30, 2));
        var end = Math.Max(phEnd, shEnd);
        return end > int.MaxValue ? null : (long)end;
    }

    private static ulong Read(ReadOnlySpan<byte> data, bool bigEndian, int at, int size) => size switch
    {
        2 => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(data[at..]) : BinaryPrimitives.ReadUInt16LittleEndian(data[at..]),
        4 => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data[at..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[at..]),
        _ => bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(data[at..]) : BinaryPrimitives.ReadUInt64LittleEndian(data[at..]),
    };

    private static long? Pem(ReadOnlySpan<byte> data)
    {
        var endLine = data.IndexOf("-----END "u8);
        if (endLine < 0)
        {
            return null;
        }

        var close = data[(endLine + 9)..].IndexOf("-----"u8);
        if (close < 0)
        {
            return null;
        }

        var end = endLine + 9 + close + 5;
        while (end < data.Length && data[end] is (byte)'\r' or (byte)'\n' && end - (endLine + 9 + close + 5) < 2)
        {
            end++;
        }

        return end;
    }
}
