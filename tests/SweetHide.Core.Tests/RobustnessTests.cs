using System.Security.Cryptography;

using FsCheck.Xunit;

using SweetHide.Core.Crypto;
using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;
using SweetHide.Core.Stego;

namespace SweetHide.Core.Tests;

/// <summary>
/// Mutation and fuzz-style tests: whatever happens to an image, revealing either returns exactly the original
/// payload or throws a <see cref="StegoException"/> with a documented error, and never allocates more than the
/// image could hold.
/// </summary>
public class RobustnessTests
{
    private const string Password = "robust password";

    /// <summary>What reveal may report for damaged or foreign input (UnsupportedFormat needs a forged header).</summary>
    private static readonly StegoError[] DamageErrors = [StegoError.NotFoundOrWrongPassword, StegoError.Corrupted];

    private static readonly Payload Original = new([PayloadEntry.FromText("original"), PayloadEntry.FromFile("f.bin", Enumerable.Range(0, 700).Select(i => (byte)i).ToArray())]);

    private static readonly Lazy<Identity> Friend = new(Identity.Generate);

    private static readonly Lazy<Dictionary<string, PixelBuffer>> Stegos = new(() =>
    {
        var engine = TestImages.Engine();
        var cover = TestImages.Photo(90, 70, seed: 21, withTransparency: true);
        return new Dictionary<string, PixelBuffer>
        {
            ["v1"] = V1Writer.Embed(cover, Original, Password, 2, TestImages.FastKdf),
            ["v2"] = engine.Embed(cover, Original, Password, null, new EmbedOptions { BitsPerChannel = 3 }),
            ["adaptive"] = engine.Embed(cover, Original, Password, null, new EmbedOptions { Adaptive = true, BitsPerChannel = 2 }),
            ["deniable"] = engine.EmbedDeniable(cover, new DeniablePart(Original, Password), null, new EmbedOptions { BitsPerChannel = 2 }),
            ["recipients"] = engine.Embed(cover, Original, [Friend.Value.Recipient], new EmbedOptions { BitsPerChannel = 2 }),
        };
    });

    public static TheoryData<string> Modes => ["v1", "v2", "adaptive", "deniable", "recipients"];

    [Theory]
    [MemberData(nameof(Modes))]
    public void Random_bit_flips_give_the_original_or_a_documented_error(string mode)
    {
        var random = new Random(mode.GetHashCode(StringComparison.Ordinal));
        var stego = Stegos.Value[mode];
        var outcomes = new Dictionary<string, int>();
        for (var round = 0; round < 40; round++)
        {
            var mutated = stego.Clone();
            var pixels = mutated.Pixels;
            var flips = 1 << random.Next(0, 9); // 1 to 256 flips
            for (var f = 0; f < flips; f++)
            {
                var i = random.Next(pixels.Length);
                pixels[i] ^= (byte)(1 << (i % 4 == 3 ? random.Next(8) : random.Next(3))); // low bits of colour, any bit of alpha
            }

            var outcome = RevealOutcome(mutated, mode);
            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
        }

        Assert.All(outcomes.Keys, o => Assert.Contains(o, new[] { "original", nameof(StegoError.NotFoundOrWrongPassword), nameof(StegoError.Corrupted) }));
        Assert.True(outcomes.Count >= 2, string.Join(", ", outcomes)); // the mutations did reach the data
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Cropped_images_are_reported_as_not_found_or_damaged(string mode)
    {
        var stego = Stegos.Value[mode];
        foreach (var (width, height) in new[] { (stego.Width, stego.Height - 1), (stego.Width - 1, stego.Height), (stego.Width / 2, stego.Height / 2), (1, 1) })
        {
            var cropped = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                stego.Pixels.Slice(y * stego.Width * 4, width * 4).CopyTo(cropped.AsSpan(y * width * 4));
            }

            var outcome = RevealOutcome(new PixelBuffer(width, height, cropped), mode);
            if (width == stego.Width)
            {
                // Slots are chosen by cycle-walking a permutation over the slot count, so dropping only the bottom
                // row leaves most slots where they were. Now and then (it depends on the random salt; about one
                // time in eight for v1, far less for v2's larger public region and header) the salt and header
                // survive while the body doesn't, which is rightly reported as damaged rather than not found.
                Assert.Contains(outcome, new[] { nameof(StegoError.NotFoundOrWrongPassword), nameof(StegoError.Corrupted) });
            }
            else
            {
                Assert.Equal(nameof(StegoError.NotFoundOrWrongPassword), outcome);
            }
        }
    }

    [Property(MaxTest = 60)]
    public void Random_images_never_reveal_anything(byte width, byte height, int seed)
    {
        var w = 1 + (width % 120);
        var h = 1 + (height % 120);
        var data = new byte[w * h * 4];
        new Random(seed).NextBytes(data);
        var image = new PixelBuffer(w, h, data);
        var engine = TestImages.Engine();

        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(StegoError.NotFoundOrWrongPassword, Assert.Throws<StegoException>(() => engine.Extract(image, Password)).Error);
        Assert.Equal(StegoError.NotFoundOrWrongPassword, Assert.Throws<StegoException>(() => engine.Extract(image, Friend.Value)).Error);
        Assert.False(engine.CreatePasswordProbe(image).TryPassword(Password));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, (data.Length * 4L) + (4 << 20));
    }

    [Fact]
    public void Truncated_png_files_fail_as_invalid_images_or_decode_unchanged()
    {
        using var png = new MemoryStream();
        var codec = new SkiaImageCodec();
        var stego = Stegos.Value["v2"];
        codec.EncodePng(stego, png);
        var bytes = png.ToArray();
        for (var length = 0; length < bytes.Length; length += Math.Max(1, length / 3))
        {
            PixelBuffer decoded;
            try
            {
                decoded = codec.Decode(new MemoryStream(bytes, 0, length), applyOrientation: false);
            }
            catch (InvalidImageException)
            {
                continue;
            }

            // Only the trailing IEND chunk (and CRC) can go missing without losing pixels; then nothing changed.
            Assert.True(length >= bytes.Length - 16, $"decoded a PNG cut to {length} of {bytes.Length} bytes");
            Assert.Equal(stego.Pixels.ToArray(), decoded.Pixels.ToArray());
        }

        foreach (var cut in new[] { 1, 4, 12, 13, 16 })
        {
            var outcome = Record.Exception(() => codec.Decode(new MemoryStream(bytes, 0, bytes.Length - cut), applyOrientation: false));
            Assert.True(outcome is null or InvalidImageException, outcome?.ToString());
        }
    }

    [Fact]
    public void A_damaged_length_is_bounded_by_the_image_before_allocating()
    {
        // Simulates an authenticated header that claims a huge body (only possible with the key, or a bug).
        var image = TestImages.Photo(60, 50);
        var layout = new V2Layout(new RgbLsbCarrier(image));
        var slots = ContainerSlots.Locate(layout.Carrier, layout.Sequence(V2Layout.PublicSlots, ContainerPlacement.Whole, new byte[64]))!;
        foreach (var adaptive in new[] { false, true })
        {
            var header = new ContainerHeader(1, adaptive, 0, ContentKind.Payload, int.MaxValue - 100, 0);
            using var container = new OpenedContainer(ContainerPlacement.Whole, header, slots, new byte[32]);

            var before = GC.GetAllocatedBytesForCurrentThread();
            var ex = Assert.Throws<StegoException>(() => V2Container.ReadContent(layout.Carrier, container));
            Assert.Equal(StegoError.Corrupted, ex.Error);
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 1 << 20);
        }
    }

    [Property(MaxTest = 300)]
    public void Authenticated_garbage_is_rejected_with_documented_errors(byte[] bytes)
    {
        bytes ??= [];

        // Header plaintext: either a valid header or Corrupted/UnsupportedFormat.
        var header = new byte[ContainerHeader.PlainSize];
        bytes.AsSpan(0, Math.Min(bytes.Length, header.Length)).CopyTo(header);
        AssertOnly(() => ContainerHeader.Decode(header), StegoError.Corrupted, StegoError.UnsupportedFormat);

        // Share content.
        AssertOnly(() => ShamirShare.Decode(bytes), StegoError.Corrupted, StegoError.UnsupportedFormat);

        // Payload body.
        var ex = Record.Exception(() => PayloadSerializer.Deserialize(bytes));
        Assert.True(ex is null or InvalidDataException, ex?.ToString());
    }

    [Property(MaxTest = 300)]
    public void Text_parsers_never_throw_unexpected_exceptions(string? text, byte[] bytes)
    {
        Assert.Null(Record.Exception(() => Recipient.TryParse(text, out _)));
        Assert.Null(Record.Exception(() => Bech32.TryDecode(text, "sweethide", out _)));

        var garbage = $"-----BEGIN SWEETHIDE PRIVATE KEY-----\n{Convert.ToBase64String(bytes ?? [])}\n-----END SWEETHIDE PRIVATE KEY-----";
        foreach (var candidate in new[] { text ?? string.Empty, garbage })
        {
            var ex = Record.Exception(() => Identity.Import(candidate, "passphrase").Dispose());
            Assert.True(ex is FormatException or CryptographicException, ex?.GetType().Name ?? "accepted garbage");
        }
    }

    [Fact]
    public void Identity_file_with_absurd_kdf_settings_is_refused_without_running_them()
    {
        using var identity = Identity.Generate();
        var lines = identity.Export("pass", TestImages.FastKdf).Split('\n');
        var blob = Convert.FromBase64String(string.Concat(lines[2..^2]));
        BitConverter.GetBytes(int.MaxValue).CopyTo(blob, 5); // memory KiB
        var text = string.Join('\n', [lines[0], lines[1], Convert.ToBase64String(blob), lines[^2]]);
        Assert.Throws<FormatException>(() => Identity.Import(text, "pass"));
    }

    private static string RevealOutcome(PixelBuffer image, string mode)
    {
        var engine = TestImages.Engine();
        try
        {
            var payload = mode == "recipients" ? engine.Extract(image, Friend.Value) : engine.Extract(image, Password);
            Assert.Equal("original", payload.Entries[0].GetText());
            Assert.Equal(Original.Entries[1].Content.ToArray(), payload.Entries[1].Content.ToArray());
            return "original";
        }
        catch (StegoException ex)
        {
            Assert.Contains(ex.Error, DamageErrors);
            return ex.Error.ToString();
        }
    }

    private static void AssertOnly(Action action, params StegoError[] allowed)
    {
        var ex = Record.Exception(action);
        if (ex is not null)
        {
            var stego = Assert.IsType<StegoException>(ex);
            Assert.Contains(stego.Error, allowed);
        }
    }

    [Fact]
    public void Error_messages_never_contain_the_password()
    {
        var engine = TestImages.Engine();
        var secret = "hunter2-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
        var ex = Assert.Throws<StegoException>(() => engine.Extract(Stegos.Value["v2"], secret));
        Assert.DoesNotContain(secret, ex.ToString(), StringComparison.Ordinal);
    }
}
