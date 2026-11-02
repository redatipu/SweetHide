using FsCheck.Xunit;

using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;

namespace SweetHide.Core.Tests;

public class StegoEngineTests
{
    private const string Password = "correct horse battery staple";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Roundtrips_text_and_files_at_every_bit_depth(int bits)
    {
        var engine = TestImages.Engine();
        var cover = TestImages.Photo(160, 120, seed: bits);
        var file = new byte[3000];
        new Random(bits).NextBytes(file);
        var payload = new Payload(
        [
            PayloadEntry.FromText("ข้อความลับ: พบกันที่ร้านกาแฟ 10 โมง ☕"),
            PayloadEntry.FromFile("photo.raw", file),
        ]);

        var stego = engine.Embed(cover, payload, Password, bits);
        var back = engine.Extract(stego, Password);

        Assert.Equal("ข้อความลับ: พบกันที่ร้านกาแฟ 10 โมง ☕", back.Entries[0].GetText());
        Assert.Equal("photo.raw", back.Entries[1].Name);
        Assert.Equal(file, back.Entries[1].Content.ToArray());
    }

    [Property(MaxTest = 25)]
    public void Roundtrips_arbitrary_content(byte[] data, string? text, bool withTransparency, int seed)
    {
        data ??= [];
        text = MakeValidUtf16(text ?? string.Empty);
        var engine = TestImages.Engine();
        var cover = TestImages.Photo(64, 64, seed, withTransparency);
        var payload = new Payload([PayloadEntry.FromText(text), PayloadEntry.FromFile("data.bin", data)]);

        var back = engine.Extract(engine.Embed(cover, payload, Password), Password);

        Assert.Equal(text, back.Entries[0].GetText());
        Assert.Equal(data, back.Entries[1].Content.ToArray());
    }

    [Fact]
    public void Wrong_password_reports_not_found()
    {
        var engine = TestImages.Engine();
        var stego = engine.Embed(TestImages.Photo(100, 100), Payload.FromText("secret"), Password);

        var ex = Assert.Throws<StegoException>(() => engine.Extract(stego, "wrong password"));
        Assert.Equal(StegoError.NotFoundOrWrongPassword, ex.Error);
    }

    [Fact]
    public void Clean_image_reports_not_found()
    {
        var ex = Assert.Throws<StegoException>(() => TestImages.Engine().Extract(TestImages.Photo(100, 100), Password));
        Assert.Equal(StegoError.NotFoundOrWrongPassword, ex.Error);
    }

    [Fact]
    public void Different_kdf_settings_cannot_reveal()
    {
        var stego = TestImages.Engine().Embed(TestImages.Photo(100, 100), Payload.FromText("secret"), Password);
        var other = new StegoEngine(TestImages.FastKdf with { Iterations = 2 });

        var ex = Assert.Throws<StegoException>(() => other.Extract(stego, Password));
        Assert.Equal(StegoError.NotFoundOrWrongPassword, ex.Error);
    }

    [Fact]
    public void Unicode_passwords_are_normalized()
    {
        var engine = TestImages.Engine();
        const string composed = "caf\u00E9";    // é as one code point
        const string decomposed = "cafe\u0301"; // e + combining acute
        var stego = engine.Embed(TestImages.Photo(100, 100), Payload.FromText("ok"), composed);

        Assert.Equal("ok", engine.Extract(stego, decomposed).Entries[0].GetText());
    }

    [Fact]
    public void Tampering_with_embedded_bits_is_detected()
    {
        var engine = TestImages.Engine();
        var cover = TestImages.Photo(200, 200);
        var stego = engine.Embed(cover, Payload.FromText(new string('z', 2000)), Password);

        // Flip the LSB of every value the embedding changed: guaranteed to hit header or body bits.
        var tampered = stego.Clone();
        var data = tampered.Pixels;
        var changed = 0;
        for (var i = 0; i < data.Length; i++)
        {
            if (i % 4 != 3 && data[i] != cover.Pixels[i] && changed++ % 2 == 0)
            {
                data[i] ^= 1;
            }
        }

        var ex = Assert.Throws<StegoException>(() => engine.Extract(tampered, Password));
        Assert.Contains(ex.Error, new[] { StegoError.Corrupted, StegoError.NotFoundOrWrongPassword });
    }

    [Fact]
    public void Too_large_payload_is_rejected_with_capacity_error()
    {
        var engine = TestImages.Engine();
        var cover = TestImages.Photo(40, 40); // 4800 slots → ~584 bytes at 1 bpc
        var random = new byte[2000];
        new Random(1).NextBytes(random);

        var ex = Assert.Throws<StegoException>(() =>
            engine.Embed(cover, new Payload([PayloadEntry.FromFile("x", random)]), Password));
        Assert.Equal(StegoError.InsufficientCapacity, ex.Error);
    }

    [Fact]
    public void Fills_capacity_exactly()
    {
        var engine = TestImages.Engine();
        var cover = TestImages.Photo(50, 50);
        var capacity = StegoEngine.GetCapacity(cover);

        // Random bytes don't compress, so the container size is predictable.
        var overhead = StegoEngine.GetContainerSize(new Payload([PayloadEntry.FromFile("f", ReadOnlySpan<byte>.Empty)]));
        var fileBytes = new byte[capacity.ContainerBytes - overhead];
        new Random(2).NextBytes(fileBytes);
        var payload = new Payload([PayloadEntry.FromFile("f", fileBytes)]);
        Assert.Equal(capacity.ContainerBytes, StegoEngine.GetContainerSize(payload));

        var back = engine.Extract(engine.Embed(cover, payload, Password), Password);
        Assert.Equal(fileBytes, back.Entries[0].Content.ToArray());

        var oneMoreBytes = new byte[fileBytes.Length + 1];
        new Random(2).NextBytes(oneMoreBytes);
        var oneMore = new Payload([PayloadEntry.FromFile("f", oneMoreBytes)]);
        Assert.Throws<StegoException>(() => engine.Embed(cover, oneMore, Password));
    }

    [Fact]
    public void Does_not_modify_the_cover_alpha_or_transparent_pixels()
    {
        var engine = TestImages.Engine();
        var cover = TestImages.Photo(120, 120, withTransparency: true);
        var stego = engine.Embed(cover, Payload.FromText(new string('a', 500)), Password);

        var before = cover.Pixels.ToArray();
        var after = stego.Pixels;
        for (var p = 0; p < before.Length; p += 4)
        {
            Assert.Equal(before[p + 3], after[p + 3]);
            if (before[p + 3] == 0)
            {
                Assert.Equal(before.AsSpan(p, 4).ToArray(), after.Slice(p, 4).ToArray());
            }
        }
    }

    [Fact]
    public void Embedding_is_invisible_at_one_bit_per_channel()
    {
        var engine = TestImages.Engine();
        var cover = TestImages.Photo(400, 300);
        var random = new byte[20_000];
        new Random(3).NextBytes(random);
        var stego = engine.Embed(cover, new Payload([PayloadEntry.FromFile("f", random)]), Password);

        var diff = ImageMetrics.Compare(cover, stego);
        Assert.Equal(1, diff.MaxAbsoluteDelta);
        Assert.True(diff.Psnr > 50, $"PSNR {diff.Psnr:F1} dB");
        // About half the used slots already hold the right bit, so ~half of them change.
        Assert.InRange(diff.ChangedSamples, 70_000, 90_000);
    }

    [Fact]
    public void Same_input_produces_different_images_each_time()
    {
        var engine = TestImages.Engine();
        var cover = TestImages.Photo(100, 100);
        var a = engine.Embed(cover, Payload.FromText("same"), Password);
        var b = engine.Embed(cover, Payload.FromText("same"), Password);
        Assert.NotEqual(a.Pixels.ToArray(), b.Pixels.ToArray()); // fresh salt every time
    }

    [Fact]
    public void Tiny_or_fully_transparent_images_have_no_capacity()
    {
        Assert.Equal(0, StegoEngine.GetCapacity(TestImages.Photo(5, 5)).ContainerBytes);
        Assert.Equal(0, StegoEngine.GetCapacity(TestImages.Solid(100, 100, 1, 2, 3, a: 0)).ContainerBytes);
        var ex = Assert.Throws<StegoException>(() => TestImages.Engine().Extract(TestImages.Photo(5, 5), Password));
        Assert.Equal(StegoError.NotFoundOrWrongPassword, ex.Error);
    }

    [Fact]
    public void Survives_png_encode_and_decode()
    {
        var engine = TestImages.Engine();
        var codec = new SkiaImageCodec();
        foreach (var transparent in new[] { false, true })
        {
            var stego = engine.Embed(TestImages.Photo(150, 100, withTransparency: transparent), Payload.FromText("via png"), Password);
            using var png = new MemoryStream();
            codec.EncodePng(stego, png);
            png.Position = 0;

            var decoded = codec.Decode(png, applyOrientation: false);
            Assert.Equal("via png", engine.Extract(decoded, Password).Entries[0].GetText());
        }
    }

    [Fact]
    public void Rejects_bad_arguments()
    {
        var engine = TestImages.Engine();
        var cover = TestImages.Photo(50, 50);
        Assert.Throws<ArgumentException>(() => engine.Embed(cover, Payload.FromText("x"), string.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Embed(cover, Payload.FromText("x"), Password, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Embed(cover, Payload.FromText("x"), Password, 0));
        Assert.Throws<ArgumentException>(() => new Payload([]));
    }

    private static string MakeValidUtf16(string s)
    {
        var chars = s.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsHighSurrogate(chars[i]) && i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(chars[i]))
            {
                chars[i] = '?';
            }
        }

        return new string(chars);
    }
}
