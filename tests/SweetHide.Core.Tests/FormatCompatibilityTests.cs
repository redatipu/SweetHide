using SweetHide.Core.Crypto;
using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;

namespace SweetHide.Core.Tests;

/// <summary>
/// Images written by released builds (v1) and by this format version (v2), stored as PNG fixtures. If one of these stops revealing, people can no longer
/// open images they already sent each other, so treat a failure here as a format break, not a flaky test.
/// </summary>
public class FormatCompatibilityTests
{
    private static readonly byte[] KnownFile = [.. Enumerable.Range(0, 100).Select(i => (byte)i)];

    [Fact]
    public void Reveals_v1_image_made_by_v0_1_0_with_default_settings()
    {
        // 64x48, 1 bit per channel, the real v0.1.0 Argon2 settings (64 MiB, t=3, p=4).
        var payload = new StegoEngine().Extract(Fixture("v1-default-kdf.png"), "v1 known answer");
        AssertKnownPayload(payload);
    }

    [Fact]
    public void Reveals_v1_image_with_transparency_and_three_bits()
    {
        var payload = new StegoEngine(TestImages.FastKdf).Extract(Fixture("v1-fast-kdf-bits3-alpha.png"), "v1 fast ทดสอบ");
        AssertKnownPayload(payload);
    }

    [Fact]
    public void V1_image_does_not_reveal_with_a_wrong_password()
    {
        var ex = Assert.Throws<StegoException>(() =>
            new StegoEngine(TestImages.FastKdf).Extract(Fixture("v1-fast-kdf-bits3-alpha.png"), "v1 fast"));
        Assert.Equal(StegoError.NotFoundOrWrongPassword, ex.Error);
    }

    [Fact]
    public void Reveals_v2_password_image()
    {
        var result = new StegoEngine(TestImages.FastKdf).Reveal(Fixture("v2-password.png"), "v2 known answer");
        AssertKnownPayload(result.Payload, "SweetHide v2 known answer: รหัสผ่าน");
        Assert.Equal(new HiddenDataInfo(2, HidingMode.Password, 1, false, KdfProfile.Normal), result.Info);
    }

    [Fact]
    public void Reveals_v2_image_with_key_file_two_bits_and_paranoid_profile()
    {
        var engine = new StegoEngine(TestImages.FastKdf);
        var image = Fixture("v2-keyfile-bits2-paranoid.png");
        var result = engine.Reveal(image, "v2 key file ทดสอบ", KeyFile.FromBytes("SweetHide key file KAT"u8));
        AssertKnownPayload(result.Payload, "v2 key file");
        Assert.Equal(new HiddenDataInfo(2, HidingMode.Password, 2, false, KdfProfile.Paranoid), result.Info);
        Assert.Throws<StegoException>(() => engine.Extract(image, "v2 key file ทดสอบ"));
    }

    [Fact]
    public void Reveals_v2_adaptive_image_with_transparency()
    {
        var result = new StegoEngine(TestImages.FastKdf).Reveal(Fixture("v2-adaptive-alpha.png"), "v2 adaptive");
        AssertKnownPayload(result.Payload, "v2 adaptive");
        Assert.Equal(new HiddenDataInfo(2, HidingMode.Password, 3, true, KdfProfile.Normal), result.Info);
    }

    [Fact]
    public void Reveals_v2_image_with_the_profile_its_salt_selects()
    {
        var profiles = new KdfProfiles(new KdfParameters(64, 1, 1), new KdfParameters(64, 2, 1), new KdfParameters(128, 1, 2));
        var result = new StegoEngine(profiles).Reveal(Fixture("v2-distinct-profiles-fast.png"), "v2 profiles");
        AssertKnownPayload(result.Payload, "v2 fast profile");
        Assert.Equal(KdfProfile.Fast, result.Info.KdfProfile);
    }

    [Fact]
    public void Reveals_v2_image_made_with_the_real_default_settings()
    {
        var result = new StegoEngine().Reveal(Fixture("v2-default-normal.png"), "v2 default");
        AssertKnownPayload(result.Payload, "v2 default settings");
        Assert.Equal(KdfProfile.Normal, result.Info.KdfProfile);
    }

    internal static PixelBuffer Fixture(string name)
    {
        using var stream = typeof(FormatCompatibilityTests).Assembly.GetManifestResourceStream($"Fixtures/{name}")
            ?? throw new InvalidOperationException($"Missing fixture {name}.");
        return new SkiaImageCodec().Decode(stream, applyOrientation: false);
    }

    private static void AssertKnownPayload(Payload payload, string text = "SweetHide v1 known answer: ทดสอบ 123")
    {
        Assert.Equal(2, payload.Entries.Count);
        Assert.Equal(text, payload.Entries[0].GetText());
        Assert.Equal("kat.bin", payload.Entries[1].Name);
        Assert.Equal(KnownFile, payload.Entries[1].Content.ToArray());
    }
}
