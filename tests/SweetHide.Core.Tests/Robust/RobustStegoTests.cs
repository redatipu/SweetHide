using FsCheck;
using FsCheck.Xunit;

using SkiaSharp;

using SweetHide.Core.Imaging;
using SweetHide.Core.Jpeg;
using SweetHide.Core.Robust;

namespace SweetHide.Core.Tests.Robust;

public class RobustStegoTests
{
    // Larger than both profiles' safe sizes, so every test also exercises the pre-resize.
    private static readonly PixelBuffer Cover = RobustTestImages.Photo(1600, 1200);
    private static readonly byte[] Key = RobustTestImages.Key(1);
    private static readonly byte[] Payload = RobustTestImages.Payload(200);

    // Embedding once per profile, with a payload that fills the image, keeps the suite fast; the app is then
    // simulated many times on the same JPEG.
    private static readonly Dictionary<string, Lazy<(byte[] Payload, PixelBuffer Marked, PixelBuffer Sent)>> Full = RobustProfile.All.ToDictionary(
        p => p.Name,
        p => new Lazy<(byte[], PixelBuffer, PixelBuffer)>(() =>
        {
            var payload = RobustTestImages.Payload(RobustStego.MaxPayloadBytes(Cover.Width, Cover.Height, p), seed: 9);
            var marked = RobustStego.Embed(Cover, payload, Key, p);
            return (payload, marked, SendAsJpeg(marked, p));
        }));

    public static TheoryData<string, int> AppQualities()
    {
        var data = new TheoryData<string, int>();
        foreach (var profile in RobustProfile.All)
        {
            foreach (var quality in new[] { 50, 60, 70, 80, 90 })
            {
                data.Add(profile.Name, quality);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AppQualities))]
    public void A_full_payload_survives_the_app_reencoding_our_jpeg(string profileName, int quality)
    {
        var (payload, _, sent) = Full[profileName].Value;

        var result = RobustStego.Extract(RobustTestImages.Recompress(sent, quality), Key, Profile(profileName));

        AssertFound(result, payload);
    }

    [Theory]
    [InlineData("line", 50)]
    [InlineData("line", 75)]
    [InlineData("telegram", 50)]
    [InlineData("telegram", 75)]
    public void Survives_full_resolution_chroma_too(string profileName, int quality)
    {
        var (payload, _, sent) = Full[profileName].Value;

        var result = RobustStego.Extract(RobustTestImages.Recompress(sent, quality, SKJpegEncoderDownsample.Downsample444), Key, Profile(profileName));

        AssertFound(result, payload);
    }

    [Theory]
    [InlineData("line")]
    [InlineData("telegram")]
    public void Survives_being_recompressed_twice(string profileName)
    {
        var (payload, _, sent) = Full[profileName].Value;

        var twice = RobustTestImages.Recompress(RobustTestImages.Recompress(sent, 80), 65);

        AssertFound(RobustStego.Extract(twice, Key, Profile(profileName)), payload);
    }

    [Fact]
    public void Works_with_the_builtin_jpeg_decoder_and_unencoded_pixels()
    {
        var profile = RobustProfile.Line;
        var (payload, marked, _) = Full[profile.Name].Value;
        using var file = new MemoryStream();
        RobustStego.WriteJpeg(marked, file, profile);

        AssertFound(RobustStego.Extract(JpegDecoder.Read(file.ToArray()).ToPixels(), Key, profile), payload);
        AssertFound(RobustStego.Extract(marked, Key, profile), payload);
    }

    [Fact]
    public void Searching_all_profiles_finds_the_one_used()
    {
        var (payload, _, sent) = Full["telegram"].Value;

        var result = RobustStego.Extract(RobustTestImages.Recompress(sent, 70), Key);

        AssertFound(result, payload);
        Assert.Same(RobustProfile.Telegram, result.Profile);
    }

    [Fact]
    public void Wrong_key_and_unmarked_images_are_not_found()
    {
        var (_, _, sent) = Full["line"].Value;

        var wrongKey = RobustStego.Extract(RobustTestImages.Recompress(sent, 80), RobustTestImages.Key(2));
        var unmarked = RobustStego.Extract(RobustTestImages.Recompress(RobustStego.PrepareCover(Cover, RobustProfile.Line), 80), Key);
        var original = RobustStego.Extract(Cover, Key);

        Assert.Equal(RobustStatus.NotFound, wrongKey.Status);
        Assert.Equal(RobustStatus.NotFound, unmarked.Status);
        Assert.Equal(RobustStatus.NotFound, original.Status);
        Assert.InRange(wrongKey.SyncScore, -0.1, 0.1);
        Assert.True(wrongKey.Payload.IsEmpty);
    }

    [Fact]
    public void Heavy_damage_is_reported_as_damaged_not_missing()
    {
        var profile = RobustProfile.Line;
        var marked = Full[profile.Name].Value.Marked.Clone();
        var noise = new Random(4);
        var pixels = marked.Pixels;
        var damagedRows = marked.Height * 6 / 10;
        for (var i = 0; i < damagedRows * marked.Width * 4; i++)
        {
            if (i % 4 != 3)
            {
                pixels[i] = (byte)noise.Next(256);
            }
        }

        var result = RobustStego.Extract(marked, Key, profile);

        Assert.Equal(RobustStatus.Damaged, result.Status);
        Assert.Same(profile, result.Profile);
        Assert.True(result.Payload.IsEmpty);
    }

    [Fact]
    public void Mild_rescaling_is_recovered_but_halving_is_not()
    {
        // Best effort: the extractor resamples back to the profile's size. Measured on this cover; see docs/ROBUST.md.
        var profile = RobustProfile.Telegram;
        var sent = SendAsJpeg(RobustStego.Embed(Cover, Payload, Key, profile), profile);

        var slightly = RobustStego.Extract(RobustTestImages.Recompress(RobustTestImages.Resize(sent, 0.9), 80), Key, profile);
        var halved = RobustStego.Extract(RobustTestImages.Recompress(RobustTestImages.Resize(sent, 0.5), 80), Key, profile);

        AssertFound(slightly, Payload); // 200 bytes; a full payload at 0.9x is not reliable
        Assert.Equal(RobustStatus.NotFound, halved.Status);
    }

    [Fact]
    public void Empty_payloads_roundtrip()
    {
        var result = RobustStego.Extract(RobustTestImages.Recompress(RobustStego.Embed(Cover, [], Key, RobustProfile.Line), 70), Key, RobustProfile.Line);

        AssertFound(result, []);
    }

    [Property(MaxTest = 15)]
    public void Any_payload_that_fits_roundtrips(PositiveInt seed, bool telegram)
    {
        var profile = telegram ? RobustProfile.Telegram : RobustProfile.Line;
        var random = new Random(seed.Get);
        var cover = TestImages.Photo(640, 480, seed.Get);
        var key = RobustTestImages.Key(seed.Get);
        var payload = RobustTestImages.Payload(random.Next(0, RobustStego.MaxPayloadBytes(640, 480, profile) + 1), seed.Get);

        var result = RobustStego.Extract(RobustTestImages.Recompress(RobustStego.Embed(cover, payload, key, profile), 85), key, profile);

        AssertFound(result, payload);
    }

    [Fact]
    public void Tiny_covers_hold_nothing()
    {
        var tiny = TestImages.Photo(200, 150);

        Assert.Equal(0, RobustStego.MaxPayloadBytes(200, 150, RobustProfile.Line));
        var ex = Assert.Throws<StegoException>(() => RobustStego.Embed(tiny, [], Key, RobustProfile.Line));
        Assert.Equal(StegoError.InsufficientCapacity, ex.Error);
        Assert.Equal(RobustStatus.NotFound, RobustStego.Extract(tiny, Key).Status);
    }

    [Fact]
    public void Target_size_and_capacity_are_pinned()
    {
        // These numbers are part of the format: a change means images made by older builds can't be read.
        Assert.Equal((1385, 1039), RobustStego.TargetSize(4000, 3000, RobustProfile.Line));
        Assert.Equal((1280, 960), RobustStego.TargetSize(4000, 3000, RobustProfile.Telegram));
        Assert.Equal((960, 1280), RobustStego.TargetSize(3000, 4000, RobustProfile.Telegram));
        Assert.Equal((640, 480), RobustStego.TargetSize(640, 480, RobustProfile.Line)); // never enlarged
        Assert.Equal(321, RobustStego.MaxPayloadBytes(4000, 3000, RobustProfile.Line));
        Assert.Equal(342, RobustStego.MaxPayloadBytes(4000, 3000, RobustProfile.Telegram));
        Assert.Equal(0, RobustStego.MaxPayloadBytes(64, 64, RobustProfile.Telegram));
    }

    [Fact]
    public void Refuses_payloads_that_do_not_fit_and_bad_keys()
    {
        var capacity = RobustStego.MaxPayloadBytes(Cover.Width, Cover.Height, RobustProfile.Line);

        var ex = Assert.Throws<StegoException>(() => RobustStego.Embed(Cover, new byte[capacity + 1], Key, RobustProfile.Line));

        Assert.Equal(StegoError.InsufficientCapacity, ex.Error);
        Assert.Throws<ArgumentException>(() => RobustStego.Embed(Cover, Payload, new byte[16], RobustProfile.Line));
        Assert.Throws<ArgumentException>(() => RobustStego.Extract(Cover, new byte[31]));
        Assert.Throws<ArgumentException>(() => RobustStego.Embed(Cover, Payload, Key, RobustProfile.Line with { Step = 0 }));
    }

    private static RobustProfile Profile(string name) => RobustProfile.All.Single(p => p.Name == name);

    /// <summary>The JPEG the user actually sends, as the app sees it after decoding.</summary>
    internal static PixelBuffer SendAsJpeg(PixelBuffer marked, RobustProfile profile)
    {
        using var file = new MemoryStream();
        RobustStego.WriteJpeg(marked, file, profile);
        return Jpeg.JpegTestImages.SkiaDecode(file.ToArray());
    }

    internal static void AssertFound(RobustExtraction result, byte[] payload)
    {
        Assert.True(result.Status == RobustStatus.Found, $"{result.Status}: sync {result.SyncScore:F2}, raw errors {result.RawBitErrorRate:P1}");
        Assert.Equal(payload, result.Payload.ToArray());
    }
}
