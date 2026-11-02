using SweetHide.Forensics.Detection;

using static SweetHide.Forensics.Tests.TestData;

namespace SweetHide.Forensics.Tests;

public class ContentAnalyzerTests
{
    private const long Capacity = 1 << 20;

    [Fact]
    public void Recognises_file_magic_at_the_start()
    {
        var png = EncodePng(Photo(8, 8));
        var result = ContentAnalyzer.Analyze(png, Capacity);

        Assert.Equal(ContentKind.File, result.Kind);
        Assert.Equal(FileType.Png, result.FileType);
        Assert.True(result.Score >= 0.9);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Recognises_a_u32_length_prefix(bool bigEndian)
    {
        var message = Utf8("the password is hunter2");
        var prefix = bigEndian ? U32BigEndian((uint)message.Length) : U32LittleEndian((uint)message.Length);
        var result = ContentAnalyzer.Analyze(Concat(prefix, message, RandomBytes(200, 1)), Capacity);

        Assert.Equal(ContentKind.LengthPrefixedText, result.Kind);
        Assert.Equal(4, result.PayloadOffset);
        Assert.Equal(message.Length, result.PayloadLength);
        Assert.Contains(bigEndian ? "big" : "little", result.Framing, StringComparison.Ordinal);
        Assert.Equal("the password is hunter2", result.Preview);
        Assert.True(result.Score > 0.8, $"score {result.Score}");
    }

    [Fact]
    public void Recognises_a_length_prefixed_file()
    {
        var zip = Concat([0x50, 0x4B, 0x03, 0x04], RandomBytes(60, 2));
        var result = ContentAnalyzer.Analyze(Concat(U32BigEndian((uint)zip.Length), zip, RandomBytes(100, 3)), Capacity);

        Assert.Equal(ContentKind.LengthPrefixedFile, result.Kind);
        Assert.Equal(FileType.Zip, result.FileType);
        Assert.Equal(zip.Length, result.PayloadLength);
    }

    [Fact]
    public void Recognises_stegano_decimal_length_prefix()
    {
        var result = ContentAnalyzer.Analyze(Concat(Utf8("19:meet me at midnight"), RandomBytes(64, 4)), Capacity);

        Assert.Equal(ContentKind.LengthPrefixedText, result.Kind);
        Assert.Equal(3, result.PayloadOffset);
        Assert.Equal(19, result.PayloadLength);
        Assert.StartsWith("meet me at midnigh", result.Preview, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\0", "NUL")]
    [InlineData("$t3g0", "$t3g0")]
    [InlineData("#####", "#####")]
    public void Finds_text_that_ends_in_a_delimiter(string terminator, string expectedFraming)
    {
        var data = Concat(Utf8("flag{d3l1m1t3d}" + terminator), RandomBytes(100, 5));
        var result = ContentAnalyzer.Analyze(data, Capacity);

        Assert.Equal(ContentKind.TerminatedText, result.Kind);
        Assert.Equal(15, result.PayloadLength);
        Assert.Contains(expectedFraming, result.Framing, StringComparison.Ordinal);
    }

    [Fact]
    public void Finds_the_ff_fe_bit_marker()
    {
        var data = Concat(Utf8("secret message!"), [0xFF, 0xFE], RandomBytes(50, 6));
        var result = ContentAnalyzer.Analyze(data, Capacity);

        Assert.Equal(ContentKind.TerminatedText, result.Kind);
        Assert.Equal(15, result.PayloadLength);
    }

    [Fact]
    public void Thai_text_counts_as_text()
    {
        var data = Concat(Utf8("ข้อความลับซ่อนอยู่ในรูปนี้นะ"), [0x80], RandomBytes(100, 7));
        var result = ContentAnalyzer.Analyze(data, Capacity);

        Assert.Equal(ContentKind.Text, result.Kind);
        Assert.True(result.Score > 0.8, $"score {result.Score}");
        Assert.Equal("ข้อความลับซ่อนอยู่ในรูปนี้นะ", result.Preview);
    }

    [Fact]
    public void Random_and_repetitive_data_score_low()
    {
        var random = ContentAnalyzer.Analyze(RandomBytes(4096, 8), Capacity);
        Assert.Equal(ContentKind.HighEntropy, random.Kind);
        Assert.True(random.Score < 0.1);

        // Pixel patterns can produce long runs of printable bytes; they are not messages.
        var pattern = ContentAnalyzer.Analyze(Utf8(string.Concat(Enumerable.Repeat("I$", 200))), Capacity);
        Assert.True(pattern.Score < 0.1, $"score {pattern.Score}");

        var uniform = ContentAnalyzer.Analyze(new byte[512], Capacity);
        Assert.Equal(ContentKind.Uniform, uniform.Kind);
        Assert.Equal(0, uniform.Score);
    }

    [Fact]
    public void Ignores_length_fields_larger_than_the_stream()
    {
        var data = Concat(U32BigEndian(5000), Utf8("abcdefgh"), RandomBytes(32, 9));
        var result = ContentAnalyzer.Analyze(data, streamCapacity: 100);

        Assert.NotEqual(ContentKind.LengthPrefixedText, result.Kind);
    }

    [Fact]
    public void Identifies_validated_short_magics_only()
    {
        // "MZ" without a PE header and "BM" without a sane BMP header are random coincidences.
        Assert.Null(FileSignatures.Identify(Concat(Utf8("MZ"), RandomBytes(100, 10))));
        Assert.Null(FileSignatures.Identify(Concat(Utf8("BM"), RandomBytes(100, 11))));

        var pe = new byte[0x100];
        pe[0] = (byte)'M';
        pe[1] = (byte)'Z';
        pe[0x3C] = 0x80;
        Utf8("PE\0\0").CopyTo(pe, 0x80);
        Assert.Equal(FileType.PortableExecutable, FileSignatures.Identify(pe)?.Type);
    }

    [Fact]
    public void Entropy_and_quality_behave()
    {
        Assert.Equal(0, TextHeuristics.Entropy(new byte[100]));
        Assert.Equal(8, TextHeuristics.Entropy([.. Enumerable.Range(0, 256).Select(i => (byte)i)]), 6);
        Assert.True(TextHeuristics.Quality("flag{hi}") > 0.9);
        Assert.True(TextHeuristics.Quality("UUUUUUUUUUUU") < 0.01);
    }
}
