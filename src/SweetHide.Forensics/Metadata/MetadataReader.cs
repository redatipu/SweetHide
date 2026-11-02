using System.IO.Compression;
using System.Text;

using SweetHide.Forensics.Containers;
using SweetHide.Forensics.Detection;

using static System.FormattableString;

namespace SweetHide.Forensics.Metadata;

/// <summary>
/// Reads text-bearing metadata: PNG tEXt/zTXt/iTXt/eXIf, JPEG COM/EXIF/XMP (and text in unknown APPn segments),
/// GIF comment, plain-text and application blocks, WebP EXIF/XMP and WAV LIST/INFO. Hand-written parsers with
/// strict bounds checks; nothing here trusts a length field.
/// </summary>
public static class MetadataReader
{
    private const int MaxStructureItems = 100_000;
    private const string XmpIdentifier = "http://ns.adobe.com/xap/1.0/";
    private const string ExtendedXmpIdentifier = "http://ns.adobe.com/xmp/extension/";

    private static readonly HashSet<string> RoutineJpegApps = new(StringComparer.Ordinal)
    {
        "JFIF", "JFXX", "Exif", "ICC_PROFILE", "Adobe", "Ducky", "MPF", "Photoshop 3.0", "FPXR", XmpIdentifier, ExtendedXmpIdentifier,
    };

    private static readonly HashSet<string> RoutineGifApps = new(StringComparer.Ordinal) { "NETSCAPE2.0", "ANIMEXTS1.0", "ICCRGBG1012" };

    private static readonly Dictionary<string, string> RiffInfoNames = new(StringComparer.Ordinal)
    {
        ["INAM"] = "Title",
        ["IART"] = "Artist",
        ["ICMT"] = "Comment",
        ["ICOP"] = "Copyright",
        ["ICRD"] = "Date",
        ["IGNR"] = "Genre",
        ["ISFT"] = "Software",
        ["IPRD"] = "Product",
        ["IKEY"] = "Keywords",
        ["ISBJ"] = "Subject",
        ["IENG"] = "Engineer",
        ["ISRC"] = "Source",
    };

    public static IReadOnlyList<MetadataField> Read(ReadOnlyMemory<byte> file, MetadataOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= MetadataOptions.Default;
        var collector = new FieldCollector(options);
        var data = file.Span;
        var type = FileSignatures.Identify(data)?.Type ?? FileType.Unknown;
        var walk = ContainerInspector.Walk(data, type, new ContainerInspectorOptions { MaxStructureItems = MaxStructureItems, MaxRepairDimension = 0 });

        using var extendedXmp = new MemoryStream();
        long extendedXmpOffset = -1;
        var checks = 0;
        foreach (var item in walk.Items)
        {
            if (collector.IsFull)
            {
                break;
            }

            if (++checks % 1024 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var body = data.Slice((int)item.DataOffset, (int)item.DataLength);
            switch (type)
            {
                case FileType.Png:
                    ReadPngChunk(item, body, collector);
                    break;
                case FileType.Jpeg when item.Type == "COM":
                    collector.AddBytes("JPEG COM", "Comment", body, item.DataOffset);
                    break;
                case FileType.Jpeg when item.Type.StartsWith("APP", StringComparison.Ordinal):
                    ReadJpegApp(item, body, collector, extendedXmp, ref extendedXmpOffset);
                    break;
                case FileType.Gif when (item.Traits & StructureTraits.Metadata) != 0:
                    ReadGifExtension(item, data, collector);
                    break;
                case FileType.WebP or FileType.Wav or FileType.Avi or FileType.Riff:
                    ReadRiffChunk(item, body, collector);
                    break;
            }
        }

        if (extendedXmp.Length > 0 && !collector.IsFull)
        {
            var packet = extendedXmp.ToArray();
            XmpReader.Read(FieldCollector.Decode(packet), extendedXmpOffset, packet.Length, "XMP (extended)", collector);
        }

        return collector.Fields;
    }

    private static void ReadPngChunk(StructureItem item, ReadOnlySpan<byte> body, FieldCollector collector)
    {
        switch (item.Type)
        {
            case "tEXt":
                ReadText(item, body, collector);
                break;
            case "zTXt":
                ReadCompressedText(item, body, collector);
                break;
            case "iTXt":
                ReadInternationalText(item, body, collector);
                break;
            case "eXIf":
                ExifReader.Read(body, item.DataOffset, collector);
                break;
        }
    }

    /// <summary>tEXt: keyword, NUL, text.</summary>
    private static void ReadText(StructureItem item, ReadOnlySpan<byte> body, FieldCollector collector)
    {
        var keyword = SplitAtNul(body, out var text);
        collector.AddBytes("PNG tEXt", Latin1(keyword), text, item.DataOffset + keyword.Length + 1, CrcNote(item), unusual: HasBadCrc(item));
    }

    /// <summary>zTXt: keyword, NUL, compression method, zlib data.</summary>
    private static void ReadCompressedText(StructureItem item, ReadOnlySpan<byte> body, FieldCollector collector)
    {
        var keyword = SplitAtNul(body, out var rest);
        if (!rest.IsEmpty)
        {
            AddInflated(collector, "PNG zTXt", Latin1(keyword), rest[1..], item.DataOffset + keyword.Length + 2, item);
        }
    }

    /// <summary>iTXt: keyword, NUL, compression flag, method, language, NUL, translated keyword, NUL, UTF-8 text.</summary>
    private static void ReadInternationalText(StructureItem item, ReadOnlySpan<byte> body, FieldCollector collector)
    {
        var keywordBytes = SplitAtNul(body, out var rest);
        if (rest.Length < 2)
        {
            return;
        }

        var compressed = rest[0] == 1;
        var language = SplitAtNul(rest[2..], out var afterLanguage);
        var translated = SplitAtNul(afterLanguage, out var text);
        var keyword = Latin1(keywordBytes);
        var name = translated.IsEmpty ? keyword : $"{keyword} ({Encoding.UTF8.GetString(translated)})";
        var offset = item.DataOffset + (body.Length - text.Length);
        if (!language.IsEmpty)
        {
            name += $" [{Latin1(language)}]";
        }

        if (compressed)
        {
            AddInflated(collector, "PNG iTXt", name, text, offset, item, isXmp: keyword == "XML:com.adobe.xmp");
        }
        else if (keyword == "XML:com.adobe.xmp")
        {
            XmpReader.Read(FieldCollector.Decode(text), offset, text.Length, "PNG iTXt", collector);
        }
        else
        {
            collector.AddBytes("PNG iTXt", name, text, offset, CrcNote(item), unusual: HasBadCrc(item));
        }
    }

    private static void AddInflated(FieldCollector collector, string source, string name, ReadOnlySpan<byte> compressed, long offset, StructureItem item, bool isXmp = false)
    {
        var (inflated, capped, failed) = Inflate(compressed, collector.Options.MaxDecompressedBytes);
        if (failed)
        {
            collector.AddText(source, name, string.Empty, offset, compressed.Length, "compressed text is corrupt", unusual: true);
        }
        else if (capped)
        {
            collector.AddBytes(source, name, inflated, offset, Invariant($"decompressed output capped at {collector.Options.MaxDecompressedBytes} bytes (possible zlib bomb)"), unusual: true);
        }
        else if (isXmp)
        {
            XmpReader.Read(FieldCollector.Decode(inflated), offset, compressed.Length, source, collector);
        }
        else
        {
            collector.AddBytes(source, name, inflated, offset, CrcNote(item), unusual: HasBadCrc(item));
        }
    }

    private static void ReadJpegApp(StructureItem item, ReadOnlySpan<byte> body, FieldCollector collector, MemoryStream extendedXmp, ref long extendedXmpOffset)
    {
        var identifier = JpegWalker.Identifier(body);
        var payloadStart = identifier is null ? 0 : Math.Min(body.Length, identifier.Length + 1);
        switch (identifier)
        {
            case "Exif" when body.Length > 6:
                ExifReader.Read(body[6..], item.DataOffset + 6, collector);
                return;
            case XmpIdentifier:
                var packet = body[payloadStart..];
                XmpReader.Read(FieldCollector.Decode(packet), item.DataOffset + payloadStart, packet.Length, "XMP", collector);
                return;
            case ExtendedXmpIdentifier when body.Length > payloadStart + 40:
                // GUID (32) + full length (4) + offset (4), then this portion of the packet.
                if (extendedXmpOffset < 0)
                {
                    extendedXmpOffset = item.DataOffset + payloadStart + 40;
                }

                if (extendedXmp.Length < collector.Options.MaxDecompressedBytes)
                {
                    extendedXmp.Write(body[(payloadStart + 40)..]);
                }

                return;
        }

        if (identifier is not null && RoutineJpegApps.Contains(identifier))
        {
            return;
        }

        var payload = body[payloadStart..].TrimEnd((byte)0);
        if (payload.Length >= 4 && TextHeuristics.TextRatio(payload) >= 0.9)
        {
            collector.AddBytes("JPEG " + item.Type, identifier ?? "(no identifier)", payload, item.DataOffset + payloadStart, "text in an unusual APP segment", unusual: true);
        }
    }

    private static void ReadGifExtension(StructureItem item, ReadOnlySpan<byte> data, FieldCollector collector)
    {
        var maxBytes = collector.Options.MaxDecompressedBytes;
        var start = (int)item.DataOffset;
        switch (item.Type)
        {
            case "Comment":
                collector.AddBytes("GIF comment", "Comment", GifWalker.ReadSubBlocks(data, start, maxBytes), item.DataOffset);
                break;
            case "Plain text" when start < data.Length && data[start] == 12:
                collector.AddBytes("GIF plain text", "Text", GifWalker.ReadSubBlocks(data, start + 13, maxBytes), item.DataOffset + 13);
                break;
            case "Application" when start + 12 <= data.Length && data[start] == 11:
                var appId = Latin1(data.Slice(start + 1, 11));
                if (appId == "XMP DataXMP")
                {
                    // XMP in GIF is stored raw (the "sub-block sizes" are packet bytes), followed by a magic trailer.
                    var raw = data.Slice(start + 12, (int)(item.Offset + item.Length - start - 12));
                    var packet = XmpPacket(raw);
                    XmpReader.Read(FieldCollector.Decode(packet), start + 12, packet.Length, "GIF XMP", collector);
                }
                else if (!RoutineGifApps.Contains(appId))
                {
                    var payload = GifWalker.ReadSubBlocks(data, start + 12, maxBytes);
                    var text = TextHeuristics.TextRatio(payload) >= 0.9;
                    collector.AddText("GIF application", appId, text ? FieldCollector.Decode(payload) : TextHeuristics.HexPreview(payload, 64), start + 12, payload.Length, text ? "text in an unknown application block" : Invariant($"{payload.Length} bytes of binary data in an unknown application block"), unusual: true);
                }

                break;
        }
    }

    private static void ReadRiffChunk(StructureItem item, ReadOnlySpan<byte> body, FieldCollector collector)
    {
        switch (item.Type)
        {
            case "EXIF":
                var skip = body.StartsWith("Exif\0\0"u8) ? 6 : 0;
                ExifReader.Read(body[skip..], item.DataOffset + skip, collector);
                break;
            case "XMP ":
                XmpReader.Read(FieldCollector.Decode(body), item.DataOffset, body.Length, "XMP", collector);
                break;
            case "LIST" when body.Length >= 4 && body.StartsWith("INFO"u8):
                ReadRiffInfo(body[4..], item.DataOffset + 4, collector);
                break;
        }
    }

    private static void ReadRiffInfo(ReadOnlySpan<byte> list, long offset, FieldCollector collector)
    {
        var at = 0;
        while (at + 8 <= list.Length && !collector.IsFull)
        {
            var id = Latin1(list.Slice(at, 4));
            var size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(list[(at + 4)..]);
            var available = (int)Math.Min(size, (uint)(list.Length - at - 8));
            var value = list.Slice(at + 8, available).TrimEnd((byte)0);
            collector.AddBytes("RIFF INFO", RiffInfoNames.GetValueOrDefault(id, id), value, offset + at + 8, available < size ? "declared size runs past the LIST chunk" : null, unusual: available < size);
            at += 8 + available + (available & 1);
        }
    }

    /// <summary>The XMP packet inside raw bytes: from "&lt;?xpacket begin" (or "&lt;x:xmpmeta") to the end marker.</summary>
    private static ReadOnlySpan<byte> XmpPacket(ReadOnlySpan<byte> raw)
    {
        var start = raw.IndexOf("<?xpacket begin"u8);
        if (start < 0)
        {
            start = Math.Max(0, raw.IndexOf("<x:xmpmeta"u8));
        }

        var rest = raw[start..];
        var end = rest.IndexOf("<?xpacket end"u8);
        if (end >= 0)
        {
            var close = rest[end..].IndexOf("?>"u8);
            return close < 0 ? rest : rest[..(end + close + 2)];
        }

        var meta = rest.IndexOf("</x:xmpmeta>"u8);
        return meta < 0 ? rest : rest[..(meta + 12)];
    }

    private static (byte[] Data, bool Capped, bool Failed) Inflate(ReadOnlySpan<byte> compressed, int maxBytes)
    {
        try
        {
            using var input = new MemoryStream(compressed.ToArray(), writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = zlib.Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, maxBytes + 1L - output.Length)));
                if (read == 0)
                {
                    return (output.ToArray(), false, false);
                }

                output.Write(buffer, 0, read);
                if (output.Length > maxBytes)
                {
                    return (output.GetBuffer().AsSpan(0, maxBytes).ToArray(), true, false);
                }
            }
        }
        catch (InvalidDataException)
        {
            return ([], false, true);
        }
    }

    /// <summary>Bytes before the first NUL; <paramref name="after"/> gets the bytes after it (empty if there is none).</summary>
    private static ReadOnlySpan<byte> SplitAtNul(ReadOnlySpan<byte> data, out ReadOnlySpan<byte> after)
    {
        var nul = data.IndexOf((byte)0);
        after = nul < 0 ? [] : data[(nul + 1)..];
        return nul < 0 ? data : data[..nul];
    }

    private static string Latin1(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);

    private static bool HasBadCrc(StructureItem item) => (item.Traits & StructureTraits.CrcMismatch) != 0;

    private static string? CrcNote(StructureItem item) => HasBadCrc(item) ? "chunk CRC mismatch: edited after the file was written" : null;
}
