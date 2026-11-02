using System.IO.Compression;

using FsCheck;
using FsCheck.Fluent;

using SweetHide.Forensics.Metadata;

using static SweetHide.Forensics.Tests.FileBuilders;
using static SweetHide.Forensics.Tests.TestData;

namespace SweetHide.Forensics.Tests;

public class MetadataReaderTests
{
    private const string Xmp = """
        <?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
        <x:xmpmeta xmlns:x="adobe:ns:meta/">
          <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            <rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmp:CreatorTool="StegTool 1.0">
              <dc:description><rdf:Alt><rdf:li xml:lang="x-default">flag{xmp_description}</rdf:li></rdf:Alt></dc:description>
              <!-- look closer -->
            </rdf:Description>
          </rdf:RDF>
        </x:xmpmeta>
        <?xpacket end="w"?>
        """;

    private static byte[] SampleExif(bool littleEndian) => new TiffBuilder(littleEndian)
        .Ascii("IFD0", 0x010E, "flag{exif_description}")
        .Ascii("IFD0", 0x013B, "นักสืบ")
        .AsciiBytes("IFD0", 0x8298, [.. "visible"u8, 0, .. "hidden-part"u8, 0])
        .Utf16(0x9C9C, "สวัสดี comment")
        .Undefined("Exif", 0x9286, [.. "ASCII\0\0\0"u8, .. "secret user comment"u8])
        .Ascii("Exif", 0x9003, "2026:11:02 10:00:00")
        .Ascii("GPS", 0x0001, "N")
        .Rationals("GPS", 0x0002, (13, 1), (45, 1), (2268, 100))
        .Ascii("GPS", 0x0003, "E")
        .Rationals("GPS", 0x0004, (100, 1), (30, 1), (648, 100))
        .Build();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reads_exif_text_tags_from_jpeg_app1(bool littleEndian)
    {
        var jpeg = Jpeg((0xE1, Concat(Utf8("Exif\0\0"), SampleExif(littleEndian))));

        var fields = MetadataReader.Read(jpeg, cancellationToken: Ct);

        AssertSampleExif(fields);
    }

    [Fact]
    public void Reads_exif_from_a_png_exif_chunk()
    {
        var fields = MetadataReader.Read(Png(8, 8, ("eXIf", SampleExif(littleEndian: false))), cancellationToken: Ct);

        AssertSampleExif(fields);
    }

    [Fact]
    public void Reads_png_text_chunks_including_compressed_and_thai()
    {
        var png = Png(
            8,
            8,
            ("tEXt", TextChunk("Comment", "plain comment")),
            ("zTXt", [.. "Author"u8, 0, 0, .. Deflate(Utf8("compressed author"))]),
            ("iTXt", [.. "Title"u8, 0, 0, 0, .. "th"u8, 0, .. Utf8("ชื่อเรื่อง"), 0, .. Utf8("ข้อความภาษาไทย")]),
            ("iTXt", [.. "Description"u8, 0, 1, 0, 0, 0, .. Deflate(Utf8("compressed international text"))]));

        var fields = MetadataReader.Read(png, cancellationToken: Ct);

        Assert.Contains(fields, f => f is { Source: "PNG tEXt", Name: "Comment", Value: "plain comment", IsUnusual: false });
        Assert.Contains(fields, f => f is { Source: "PNG zTXt", Name: "Author", Value: "compressed author" });
        Assert.Contains(fields, f => f is { Source: "PNG iTXt", Name: "Title (ชื่อเรื่อง) [th]", Value: "ข้อความภาษาไทย" });
        Assert.Contains(fields, f => f is { Source: "PNG iTXt", Name: "Description", Value: "compressed international text" });
    }

    [Fact]
    public void Flags_unusually_long_fields_and_bad_crcs()
    {
        var png = Png(8, 8, ("tEXt", TextChunk("Comment", new string('A', 500))));
        var fields = MetadataReader.Read(png, cancellationToken: Ct);
        var comment = Assert.Single(fields);
        Assert.True(comment.IsUnusual);
        Assert.Contains("unusually long", comment.Note, StringComparison.Ordinal);

        var tampered = (byte[])png.Clone();
        tampered[^30]++; // inside the text, CRC no longer matches
        var edited = Assert.Single(MetadataReader.Read(tampered, cancellationToken: Ct));
        Assert.Contains("CRC mismatch", edited.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_jpeg_comment_and_xmp_including_xml_comments()
    {
        var jpeg = Jpeg((0xFE, Utf8("JPEG comment ไทย")), (0xE1, Concat(Utf8("http://ns.adobe.com/xap/1.0/\0"), Utf8(Xmp))));

        var fields = MetadataReader.Read(jpeg, cancellationToken: Ct);

        Assert.Contains(fields, f => f is { Source: "JPEG COM", Value: "JPEG comment ไทย" });
        Assert.Contains(fields, f => f is { Source: "XMP", Name: "XMP packet" });
        Assert.Contains(fields, f => f is { Source: "XMP", Name: "dc:description", Value: "flag{xmp_description}" });
        Assert.Contains(fields, f => f is { Source: "XMP", Name: "xmp:CreatorTool", Value: "StegTool 1.0" });
        Assert.Contains(fields, f => f is { Name: "comment", Value: "look closer", IsUnusual: true });
    }

    [Fact]
    public void Reads_text_hidden_in_an_unknown_jpeg_app_segment()
    {
        var jpeg = Jpeg((0xEF, Concat(Utf8("MyTool\0"), Utf8("the key is in APP15"))));

        var field = Assert.Single(MetadataReader.Read(jpeg, cancellationToken: Ct));

        Assert.Equal("JPEG APP15", field.Source);
        Assert.Equal("MyTool", field.Name);
        Assert.Equal("the key is in APP15", field.Value);
        Assert.True(field.IsUnusual);
    }

    [Fact]
    public void Reads_gif_comments_application_blocks_and_xmp()
    {
        var xmpBlock = Concat(Utf8(Xmp), [1, .. Enumerable.Range(0, 256).Select(i => (byte)(255 - i)), 0]);
        var gif = Gif(
            ["first comment", new string('x', 300)],
            [("NETSCAPE2.0", [1, 0, 0]), ("SECRETAPP1.0", Utf8("text inside an app block"))]);
        var withXmp = InsertBeforeImage(gif, Concat([0x21, 0xFF, 11], Utf8("XMP DataXMP"), xmpBlock));

        var fields = MetadataReader.Read(withXmp, cancellationToken: Ct);

        Assert.Contains(fields, f => f is { Source: "GIF comment", Value: "first comment" });
        Assert.Contains(fields, f => f.Source == "GIF comment" && f.Value.Length == 300 && f.IsUnusual);
        Assert.Contains(fields, f => f is { Source: "GIF application", Name: "SECRETAPP1.", IsUnusual: true });
        Assert.DoesNotContain(fields, f => f.Name == "NETSCAPE2.0");
        Assert.Contains(fields, f => f is { Name: "dc:description", Value: "flag{xmp_description}" });
    }

    [Fact]
    public void Reads_webp_exif_and_xmp_chunks()
    {
        var webp = Riff("WEBP", ("VP8X", new byte[10]), ("EXIF", Concat(Utf8("Exif\0\0"), SampleExif(true))), ("XMP ", Utf8(Xmp)));

        var fields = MetadataReader.Read(webp, cancellationToken: Ct);

        AssertSampleExif(fields);
        Assert.Contains(fields, f => f is { Name: "dc:description", Value: "flag{xmp_description}" });
    }

    [Fact]
    public void Reads_wav_info_list()
    {
        var info = Concat(Utf8("INFO"), Utf8("ICMT"), [11, 0, 0, 0], Utf8("hello there"), [0], Utf8("IART"), [4, 0, 0, 0], Utf8("ร"), [0]);
        var wav = Riff("WAVE", ("fmt ", [1, 0, 1, 0, 0x40, 0x1F, 0, 0, 0x40, 0x1F, 0, 0, 1, 0, 8, 0]), ("LIST", info), ("data", new byte[8]));

        var fields = MetadataReader.Read(wav, cancellationToken: Ct);

        Assert.Contains(fields, f => f is { Source: "RIFF INFO", Name: "Comment", Value: "hello there" });
        Assert.Contains(fields, f => f is { Source: "RIFF INFO", Name: "Artist", Value: "ร" });
    }

    [Fact]
    public void Caps_zlib_bombs()
    {
        var bomb = Deflate(new byte[50 * 1024 * 1024]);
        var png = Png(8, 8, ("zTXt", [.. "Bomb"u8, 0, 0, .. bomb]));

        var allocated = HostileInputTests.Allocated(() => MetadataReader.Read(png, new MetadataOptions { MaxDecompressedBytes = 64 * 1024 }, Ct), out var fields);

        var field = Assert.Single(fields);
        Assert.True(field.IsUnusual);
        Assert.Contains("capped", field.Note, StringComparison.Ordinal);
        Assert.InRange(allocated, 0, 8 * 1024 * 1024);
    }

    [Fact]
    public void Survives_exif_loops_and_absurd_counts()
    {
        var tiff = new TiffBuilder()
            .Ascii("IFD0", 0x010E, "ok")
            .Raw("IFD0", 0x013B, 2, [0, 0, 0, 0x10], count: uint.MaxValue)
            .Build();

        // IFD0's "next IFD" pointer and an Exif IFD pointer both lead back to IFD0.
        var looped = (byte[])tiff.Clone();
        var count = looped[8];
        BitConverter.GetBytes(8u).CopyTo(looped, 8 + 2 + (12 * count));
        var withPointer = new TiffBuilder().Ascii("IFD0", 0x010E, "ok").Raw("IFD0", 0x8769, 4, BitConverter.GetBytes(8u)).Build();

        foreach (var exif in new[] { tiff, looped, withPointer })
        {
            var fields = MetadataReader.Read(Jpeg((0xE1, Concat(Utf8("Exif\0\0"), exif))), cancellationToken: Ct);
            Assert.Equal("ok", Assert.Single(fields).Value);
        }

        var manyEntries = new TiffBuilder().Ascii("IFD0", 0x010E, "x").Build();
        manyEntries[8] = 0xFF;
        manyEntries[9] = 0xFF;
        var capped = MetadataReader.Read(Png(8, 8, ("eXIf", manyEntries)), cancellationToken: Ct);
        Assert.Contains(capped, f => f.Name == "Entries" && f.IsUnusual);
    }

    [Fact]
    public void Xml_entities_and_dtds_are_not_processed()
    {
        const string laughs = """
            <?xml version="1.0"?>
            <!DOCTYPE lolz [<!ENTITY lol "lol"><!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">]>
            <x:xmpmeta xmlns:x="adobe:ns:meta/">&lol2;</x:xmpmeta>
            """;
        var jpeg = Jpeg((0xE1, Concat(Utf8("http://ns.adobe.com/xap/1.0/\0"), Utf8(laughs))));

        var fields = MetadataReader.Read(jpeg, cancellationToken: Ct);

        Assert.Contains(fields, f => f.Name == "XMP parse error");
        Assert.DoesNotContain(fields, f => f.Value.Contains("lollol", StringComparison.Ordinal));
    }

    [Fact]
    public void Random_mutations_never_throw()
    {
        var seeds = HostileInputTests.Seeds().Append(Jpeg((0xE1, Concat(Utf8("Exif\0\0"), SampleExif(true))))).ToArray();
        var gen =
            from index in Gen.Choose(0, seeds.Length - 1)
            from flips in Gen.ListOf(Gen.Zip(Gen.Choose(0, 4096), Gen.Choose(0, 255)))
            from cut in Gen.Choose(0, 4096)
            select HostileInputTests.Mutate(seeds[index], flips, cut);

        Prop.ForAll(gen.ToArbitrary(), file => MetadataReader.Read(file, cancellationToken: Ct) is not null).QuickCheckThrowOnFailure();
    }

    private static void AssertSampleExif(IReadOnlyList<MetadataField> fields)
    {
        Assert.Contains(fields, f => f is { Source: "EXIF IFD0", Name: "ImageDescription", Value: "flag{exif_description}" });
        Assert.Contains(fields, f => f is { Source: "EXIF IFD0", Name: "Artist", Value: "นักสืบ" });
        Assert.Contains(fields, f => f is { Source: "EXIF IFD0", Name: "XPComment", Value: "สวัสดี comment" });
        Assert.Contains(fields, f => f is { Source: "EXIF Exif IFD", Name: "UserComment", Value: "secret user comment" });
        Assert.Contains(fields, f => f is { Source: "EXIF Exif IFD", Name: "DateTimeOriginal" });
        Assert.Contains(fields, f => f is { Source: "EXIF GPS", Name: "Position", Value: "13.756300, 100.501800" });

        var copyright = Assert.Single(fields, f => f.Name == "Copyright");
        Assert.Equal("visible", copyright.Value);
        Assert.True(copyright.IsUnusual);
        Assert.Contains("hidden-part", copyright.Note, StringComparison.Ordinal);
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] InsertBeforeImage(byte[] gif, byte[] block)
    {
        var image = Array.LastIndexOf(gif, (byte)0x2C);
        return Concat(gif[..image], block, gif[image..]);
    }
}
