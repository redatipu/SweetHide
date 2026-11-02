using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;

namespace SweetHide.Core.Tests;

/// <summary>
/// Images written by released builds, stored as PNG fixtures. If one of these stops revealing, people can no longer
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

    internal static PixelBuffer Fixture(string name)
    {
        using var stream = typeof(FormatCompatibilityTests).Assembly.GetManifestResourceStream($"Fixtures/{name}")
            ?? throw new InvalidOperationException($"Missing fixture {name}.");
        return new SkiaImageCodec().Decode(stream, applyOrientation: false);
    }

    private static void AssertKnownPayload(Payload payload)
    {
        Assert.Equal(2, payload.Entries.Count);
        Assert.Equal("SweetHide v1 known answer: ทดสอบ 123", payload.Entries[0].GetText());
        Assert.Equal("kat.bin", payload.Entries[1].Name);
        Assert.Equal(KnownFile, payload.Entries[1].Content.ToArray());
    }
}
