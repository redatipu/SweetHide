using System.Buffers.Binary;
using System.IO.Compression;

using SweetHide.Forensics.Containers;
using SweetHide.Forensics.Detection;

using static SweetHide.Forensics.Tests.FileBuilders;
using static SweetHide.Forensics.Tests.TestData;

namespace SweetHide.Forensics.Tests;

public class ContainerInspectorTests
{
    [Fact]
    public void Crc32_matches_the_standard_check_value()
    {
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
    }

    [Fact]
    public void Finds_a_zip_appended_after_png_iend_and_carves_it()
    {
        var png = Png();
        var zip = Zip(("flag.txt", "flag{appended_zip}"), ("other.txt", "hello"));
        var file = Concat(png, zip);

        var report = ContainerInspector.Inspect(file, cancellationToken: Ct);

        Assert.Equal(FileType.Png, report.Format?.Type);
        Assert.Equal("IEND", report.Structure[^1].Type);
        Assert.NotNull(report.Trailing);
        Assert.Equal(png.Length, report.Trailing.Offset);
        Assert.Equal(zip.Length, report.Trailing.Length);
        Assert.Equal(FileType.Zip, report.Trailing.Content.FileType);

        var embedded = Assert.Single(report.Embedded, e => !e.IsWholeFile);
        Assert.Equal(FileType.Zip, embedded.Type.Type);
        Assert.Equal(png.Length, embedded.Offset);
        Assert.Equal(zip.Length, embedded.Length);
        Assert.Equal("trailing data", embedded.Location);

        var carved = Carver.Carve(file, embedded).ToArray();
        Assert.Equal(zip, carved);
        using var archive = new ZipArchive(new MemoryStream(carved));
        Assert.Equal(2, archive.Entries.Count);
        Assert.Equal("carved_0x" + png.Length.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + ".zip", Carver.SuggestFileName(embedded.Offset, embedded.Type));
    }

    [Fact]
    public void Flags_unknown_private_chunks_and_crc_mismatches()
    {
        var file = Png(8, 8, ("stEg", Utf8("hidden in a private chunk")), ("tEXt", TextChunk("Comment", "hi")));
        var tampered = (byte[])file.Clone();
        var text = ContainerInspector.Inspect(tampered, cancellationToken: Ct).Structure.Single(s => s.Type == "tEXt");
        tampered[text.DataOffset + 8]++; // edit the text without fixing the CRC

        var report = ContainerInspector.Inspect(tampered, cancellationToken: Ct);

        var steg = report.Structure.Single(s => s.Type == "stEg");
        Assert.True(steg.Traits.HasFlag(StructureTraits.Unknown));
        Assert.True(steg.Traits.HasFlag(StructureTraits.Private));
        Assert.False(steg.Traits.HasFlag(StructureTraits.Critical));
        Assert.False(steg.Traits.HasFlag(StructureTraits.CrcMismatch));

        var edited = report.Structure.Single(s => s.Type == "tEXt");
        Assert.True(edited.Traits.HasFlag(StructureTraits.CrcMismatch));
        Assert.True(edited.Traits.HasFlag(StructureTraits.Metadata));
        Assert.Contains("Comment", edited.Description, StringComparison.Ordinal);
        Assert.Null(report.Trailing);
    }

    [Fact]
    public void Recovers_the_real_png_height_from_the_ihdr_crc()
    {
        var png = Png(width: 12, height: 9);
        var lied = (byte[])png.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(lied.AsSpan(8 + 8 + 4), 5); // IHDR height 9 -> 5, CRC left alone

        var report = ContainerInspector.Inspect(lied, cancellationToken: Ct);

        Assert.True(report.Structure.Single(s => s.Type == "IHDR").Traits.HasFlag(StructureTraits.CrcMismatch));
        Assert.Equal(new CorrectedDimensions(12, 9, 12, 5), report.CorrectedDimensions);
    }

    [Fact]
    public void Walks_jpeg_segments_and_locates_an_exif_thumbnail()
    {
        var thumbnail = Jpeg();
        var app1 = Concat(Utf8("Exif\0\0"), new byte[8], thumbnail);
        var file = Concat(Jpeg((0xE1, app1), (0xFE, Utf8("a comment"))), Utf8("trailing secret"));

        var report = ContainerInspector.Inspect(file, cancellationToken: Ct);

        Assert.Equal(FileType.Jpeg, report.Format?.Type);
        Assert.Equal(["SOI", "APP1", "COM"], report.Structure.Take(3).Select(s => s.Type));
        Assert.Contains(report.Structure, s => s.Type == "SOS");
        Assert.Contains(report.Structure, s => s.Type == "Scan data");
        Assert.Equal("EOI", report.Structure[^1].Type);
        Assert.Equal("trailing secret", report.Trailing?.Content.Preview);

        var embedded = Assert.Single(report.Embedded, e => !e.IsWholeFile);
        Assert.Equal("APP1 at 0x2", embedded.Location);
        Assert.Equal(thumbnail.Length, embedded.Length);
    }

    [Fact]
    public void Walks_gif_blocks_and_finds_trailing_data()
    {
        var gif = Gif(["made with love"], [("NETSCAPE2.0", [1, 0, 0])]);
        var report = ContainerInspector.Inspect(Concat(gif, Utf8("PK not really")), cancellationToken: Ct);

        Assert.Equal(["Header", "Global colour table", "Comment", "Application", "Image", "Trailer"], report.Structure.Select(s => s.Type));
        Assert.Contains("NETSCAPE2.0", report.Structure[3].Description, StringComparison.Ordinal);
        Assert.Equal(gif.Length, report.Trailing?.Offset);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void Uses_the_riff_size_to_find_data_after_a_webp()
    {
        var webp = Riff("WEBP", ("VP8L", RandomBytes(21, 1)), ("EXIF", Utf8("II*\0")), ("ZZZZ", Utf8("odd")));
        var report = ContainerInspector.Inspect(Concat(webp, Utf8("after the riff")), cancellationToken: Ct);

        Assert.Equal(FileType.WebP, report.Format?.Type);
        Assert.Equal(["RIFF", "VP8L", "EXIF", "ZZZZ"], report.Structure.Select(s => s.Type));
        Assert.Equal(22 + 8, report.Structure[1].Length); // odd size is padded
        Assert.True(report.Structure[3].Traits.HasFlag(StructureTraits.Unknown));
        Assert.Equal(webp.Length, report.Trailing?.Offset);
    }

    [Fact]
    public void Flags_a_bmp_gap_and_ignores_an_inflated_size_field()
    {
        var bmp = Bmp(3, 2, gap: 40);
        var file = Concat(bmp, Utf8("appended"));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(2), (uint)file.Length); // size field covers the extra bytes

        var report = ContainerInspector.Inspect(file, cancellationToken: Ct);

        var gap = report.Structure.Single(s => s.Type == "Gap");
        Assert.Equal(40, gap.Length);
        Assert.True(gap.Traits.HasFlag(StructureTraits.Suspicious));
        Assert.Equal(bmp.Length, report.Trailing?.Offset);
        Assert.Contains(report.Warnings, w => w.Contains("declares", StringComparison.Ordinal));
    }

    [Fact]
    public void Signature_scan_reports_each_file_once_with_lengths()
    {
        var zip = Zip(("a.txt", "a"), ("b.txt", "b"), ("c.txt", "c"));
        var png = Png();
        var gif = Gif();
        var file = Concat(RandomBytes(100, 2), zip, RandomBytes(33, 3), png, gif, Utf8("%PDF-1.7 tiny %%EOF\n"));

        var found = SignatureScanner.Scan(file, cancellationToken: Ct);

        Assert.Equal([FileType.Zip, FileType.Png, FileType.Gif, FileType.Pdf], found.Select(f => f.Type.Type));
        Assert.Equal([100L, 100 + zip.Length + 33, 133 + zip.Length + png.Length, 133 + zip.Length + png.Length + gif.Length], found.Select(f => f.Offset));
        Assert.Equal([(long?)zip.Length, png.Length, gif.Length, 20], found.Select(f => f.Length));
    }

    [Fact]
    public void Carver_validates_ranges()
    {
        var file = new byte[10];

        Assert.Equal(4, Carver.Carve(file, 6).Length);
        Assert.Equal(2, Carver.Carve(file, 3, 2).Length);
        Assert.Equal(0, Carver.Carve(file, 10).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => Carver.Carve(file, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Carver.Carve(file, 11));
        Assert.Throws<ArgumentOutOfRangeException>(() => Carver.Carve(file, 5, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => Carver.Carve(file, 5, -1));
    }
}
