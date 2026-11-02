using System.Text;

using FsCheck;
using FsCheck.Fluent;

using SweetHide.Forensics.Detection;
using SweetHide.Forensics.Text;

using static SweetHide.Forensics.Tests.TestData;

namespace SweetHide.Forensics.Tests;

public class StringsExtractorTests
{
    [Fact]
    public void Finds_ascii_utf8_and_utf16_strings_with_offsets()
    {
        var data = Concat(
            new byte[10],
            Utf8("hello world"),
            [1, 2],
            Utf8("ab"),
            [0xFF],
            Utf8("ข้อความลับ"),
            [0],
            Encoding.Unicode.GetBytes("Wide string"),
            [0, 0, 0],
            Encoding.Unicode.GetBytes("ภาษาไทย"));

        var strings = StringsExtractor.Extract(data, cancellationToken: Ct);

        Assert.Equal(
            [
                new ExtractedString(10, 11, StringEncoding.Ascii, "hello world"),
                new ExtractedString(26, 30, StringEncoding.Utf8, "ข้อความลับ"),
                new ExtractedString(57, 22, StringEncoding.Utf16LE, "Wide string"),
                new ExtractedString(82, 14, StringEncoding.Utf16LE, "ภาษาไทย"),
            ],
            strings);
    }

    [Fact]
    public void Honours_minimum_length_line_breaks_and_limits()
    {
        var data = Utf8("abc\ndefgh\r\nijklmnop");

        Assert.Equal(["defgh", "ijklmnop"], StringsExtractor.Extract(data, cancellationToken: Ct).Select(s => s.Value));
        Assert.Equal(["ijklmnop"], StringsExtractor.Extract(data, new StringsOptions { MinLength = 6 }, Ct).Select(s => s.Value));
        Assert.Single(StringsExtractor.Extract(data, new StringsOptions { MaxResults = 1 }, Ct));

        var cut = Assert.Single(StringsExtractor.Extract(Utf8(new string('x', 100)), new StringsOptions { MaxStringChars = 10 }, Ct));
        Assert.Equal(10, cut.Value.Length);
        Assert.Equal(100, cut.ByteLength);
    }

    [Fact]
    public void Encodings_can_be_switched_off()
    {
        var data = Concat(Utf8("plain ascii"), [0], Utf8("ไทยล้วน"), [0], Encoding.Unicode.GetBytes("wide one"));

        var asciiOnly = StringsExtractor.Extract(data, new StringsOptions { IncludeUtf8 = false, IncludeUtf16LE = false }, Ct);

        Assert.Equal(["plain ascii"], asciiOnly.Select(s => s.Value));
    }

    [Fact]
    public void Arbitrary_bytes_never_throw()
    {
        Prop.ForAll<byte[]>(bytes => StringsExtractor.Extract(bytes, cancellationToken: Ct).All(s => s.Offset + s.ByteLength <= bytes.Length))
            .QuickCheckThrowOnFailure();
    }
}

public class DecoderChainTests
{
    [Theory]
    [InlineData("ZmxhZ3tiYXNlNjRfd29ya3N9", "Base64", "flag{base64_works}")]
    [InlineData("ZmxhZ3t1cmw_c2FmZX0", "Base64url", "flag{url?safe}")]
    [InlineData("MZWGCZ33MJQXGZJTGJ6Q====", "Base32", "flag{base32}")]
    [InlineData("66 6c 61 67 7b 68 65 78 7d", "Hex", "flag{hex}")]
    [InlineData("0x66,0x6c,0x61,0x67,0x7b,0x68,0x7d", "Hex", "flag{h}")]
    [InlineData("01100110 01101100 01100001 01100111 01111011 01100010 01111101", "Binary", "flag{b}")]
    [InlineData("102, 108, 97, 103, 123, 100, 101, 99, 125", "Decimal bytes", "flag{dec}")]
    [InlineData("flag%7Burl_encoded%21%7D", "URL decode", "flag{url_encoded!}")]
    [InlineData("synt{ebg13_vf_rnfl}", "ROT13", "flag{rot13_is_easy}")]
    [InlineData("7=28LC@EcfN", "ROT47", "flag{rot47}")]
    [InlineData(".... . .-.. .-.. --- / .-- --- .-. .-.. -..", "Morse", "HELLO WORLD")]
    public void Decodes_single_steps(string input, string decoder, string expected)
    {
        var best = DecoderChain.Decode(input, cancellationToken: Ct)[0];

        Assert.Equal(decoder, best.Steps[0].Decoder);
        Assert.Equal(expected, best.Text);
    }

    [Fact]
    public void Follows_a_chain_of_encodings()
    {
        var base64 = Convert.ToBase64String(Utf8("flag{deep_chain}"));
        var hex = Convert.ToHexStringLower(Utf8(base64));
        var input = Base32(Utf8(hex));

        var best = DecoderChain.Decode(input, cancellationToken: Ct)[0];

        Assert.Equal(["Base32", "Hex", "Base64"], best.Steps.Select(s => s.Decoder));
        Assert.Equal("flag{deep_chain}", best.Text);
        Assert.Equal(1.0, best.Score);
        Assert.Equal("Base32 → Hex → Base64", best.Chain);
    }

    [Fact]
    public void Respects_the_depth_limit()
    {
        var input = Convert.ToBase64String(Utf8(Convert.ToBase64String(Utf8("flag{two_levels}"))));

        Assert.Contains(DecoderChain.Decode(input, cancellationToken: Ct), r => r.Text == "flag{two_levels}");
        Assert.DoesNotContain(DecoderChain.Decode(input, new DecoderOptions { MaxDepth = 1 }, Ct), r => r.Text == "flag{two_levels}");
    }

    [Fact]
    public void Ends_in_a_file_when_the_bytes_are_one()
    {
        var png = EncodePng(Photo(4, 4));

        var result = Assert.Single(DecoderChain.Decode(Convert.ToBase64String(png), cancellationToken: Ct));

        Assert.Equal(FileType.Png, result.FileType?.Type);
        Assert.Equal(png, result.FileBytes);
        Assert.Null(result.Text);
    }

    [Theory]
    [InlineData("The quick brown fox jumps over the lazy dog")]
    [InlineData("hello")]
    [InlineData("deadbeef")]
    [InlineData("สวัสดีครับ")]
    [InlineData("1 2 3")]
    [InlineData("")]
    public void Plain_text_does_not_decode_into_noise(string input)
    {
        Assert.Empty(DecoderChain.Decode(input, cancellationToken: Ct));
    }

    [Fact]
    public void Decodes_thai_hidden_in_base64()
    {
        var input = Convert.ToBase64String(Utf8("รหัสลับคือ ส้มตำ"));

        Assert.Equal("รหัสลับคือ ส้มตำ", DecoderChain.Decode(input, cancellationToken: Ct)[0].Text);
    }

    [Theory]
    [InlineData("the flag is flag{abc}", true, true)]
    [InlineData("picoCTF{l0ok_cl0s3r}", true, true)]
    [InlineData("HTB{x}", false, true)]
    [InlineData("synt{nop}", true, false)]
    [InlineData("no braces here", false, false)]
    [InlineData("flag{has space}", false, false)]
    public void Recognises_flag_shapes(string text, bool shaped, bool likely)
    {
        Assert.Equal(shaped, FlagPattern.IsMatch(text));
        Assert.Equal(likely, FlagPattern.IsLikelyFlag(text));
    }

    [Fact]
    public void Language_score_prefers_english()
    {
        Assert.True(DecoderChain.Language("the password for the archive is in the comment") > 0.8);
        Assert.True(DecoderChain.Language("qxz jjv kkq zzx wvq") < 0.5);
        Assert.Equal(0, DecoderChain.Language("!@#$%^&*"));
    }

    [Fact]
    public void Oversized_and_arbitrary_input_is_safe()
    {
        Assert.Empty(DecoderChain.Decode(new string('A', 1000), new DecoderOptions { MaxInputChars = 100 }, Ct));
        Prop.ForAll<string>(text => DecoderChain.Decode(text ?? string.Empty, cancellationToken: Ct) is not null).QuickCheckThrowOnFailure();
        Prop.ForAll<byte[]>(bytes => DecoderChain.Decode(Encoding.Latin1.GetString(bytes), cancellationToken: Ct) is not null).QuickCheckThrowOnFailure();
    }

    private static string Base32(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new StringBuilder();
        var buffer = 0;
        var bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                output.Append(alphabet[(buffer >> bits) & 31]);
            }
        }

        if (bits > 0)
        {
            output.Append(alphabet[(buffer << (5 - bits)) & 31]);
        }

        while (output.Length % 8 != 0)
        {
            output.Append('=');
        }

        return output.ToString();
    }
}
