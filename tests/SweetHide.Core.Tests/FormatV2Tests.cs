using System.Security.Cryptography;

using FsCheck.Xunit;

using SweetHide.Core.Crypto;
using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;
using SweetHide.Core.Stego;

namespace SweetHide.Core.Tests;

public class FormatV2Tests
{
    private const string Password = "correct horse battery staple";

    // Distinct parameters per profile, so a reader that runs the wrong profile cannot succeed.
    private static readonly KdfProfiles DistinctProfiles = new(
        Fast: new KdfParameters(64, 1, 1),
        Normal: new KdfParameters(64, 2, 1),
        Paranoid: new KdfParameters(128, 1, 2));

    [Property(MaxTest = 10)]
    public void Still_reveals_v1_images(byte[] data, bool withTransparency, int seed)
    {
        data ??= [];
        var bits = 1 + (Math.Abs(seed % 4));
        var cover = TestImages.Photo(64, 64, seed, withTransparency);
        var payload = new Payload([PayloadEntry.FromFile("data.bin", data)]);
        var stego = V1Writer.Embed(cover, payload, Password, bits, TestImages.FastKdf);

        var result = TestImages.Engine().Reveal(stego, Password);

        Assert.Equal(data, result.Payload.Entries[0].Content.ToArray());
        Assert.Equal(new HiddenDataInfo(1, HidingMode.Password, bits, false, KdfProfile.Normal), result.Info);
    }

    [Theory]
    [InlineData(KdfProfile.Fast)]
    [InlineData(KdfProfile.Normal)]
    [InlineData(KdfProfile.Paranoid)]
    public void Reader_learns_the_profile_from_the_salt(KdfProfile profile)
    {
        var engine = new StegoEngine(DistinctProfiles);
        var options = EmbedOptions.Default with { KdfProfile = profile };
        var stego = engine.Embed(TestImages.Photo(80, 60), Payload.FromText("profiles"), Password, null, options);

        var result = engine.Reveal(stego, Password);
        Assert.Equal("profiles", result.Payload.Entries[0].GetText());
        Assert.Equal(profile, result.Info.KdfProfile);

        // Proof that the profile's own parameters were used: an engine where only that profile differs fails.
        var other = profile switch
        {
            KdfProfile.Fast => DistinctProfiles with { Fast = new KdfParameters(64, 3, 1) },
            KdfProfile.Normal => DistinctProfiles with { Normal = new KdfParameters(64, 3, 1) },
            _ => DistinctProfiles with { Paranoid = new KdfParameters(64, 3, 1) },
        };
        var ex = Assert.Throws<StegoException>(() => new StegoEngine(other).Extract(stego, Password));
        Assert.Equal(StegoError.NotFoundOrWrongPassword, ex.Error);
    }

    [Fact]
    public void Profile_code_is_uniform_over_random_salts_and_every_code_is_valid()
    {
        var counts = new Dictionary<KdfProfile, int>();
        var salt = new byte[16];
        for (var i = 0; i < 20_000; i++)
        {
            RandomNumberGenerator.Fill(salt);
            var profile = ProfileCode.FromSalt(salt);
            counts[profile] = counts.GetValueOrDefault(profile) + 1;
        }

        // Codes 0..3 map to Fast, Normal, Paranoid, Normal.
        Assert.InRange(counts[KdfProfile.Fast], 4_500, 5_500);
        Assert.InRange(counts[KdfProfile.Normal], 9_400, 10_600);
        Assert.InRange(counts[KdfProfile.Paranoid], 4_500, 5_500);
    }

    [Fact]
    public void Key_file_is_a_required_second_factor()
    {
        var engine = TestImages.Engine();
        var keyFile = KeyFile.FromBytes("the bytes of some holiday photo"u8);
        var stego = engine.Embed(TestImages.Photo(80, 60), Payload.FromText("two factors"), Password, keyFile);

        Assert.Equal("two factors", engine.Extract(stego, Password, KeyFile.FromStream(new MemoryStream("the bytes of some holiday photo"u8.ToArray()))).Entries[0].GetText());
        AssertNotFound(() => engine.Extract(stego, Password));
        AssertNotFound(() => engine.Extract(stego, Password, KeyFile.FromBytes("the bytes of some holiday photo!"u8)));
        AssertNotFound(() => engine.Extract(stego, "wrong", keyFile));
    }

    [Fact]
    public void Key_file_from_disk_matches_bytes_and_empty_files_are_rejected()
    {
        var path = Path.GetTempFileName();
        try
        {
            var content = new byte[200_000];
            new Random(9).NextBytes(content);
            File.WriteAllBytes(path, content);
            Assert.True(KeyFile.FromFile(path).Digest.SequenceEqual(KeyFile.FromBytes(content).Digest));

            File.WriteAllBytes(path, []);
            Assert.Throws<ArgumentException>(() => KeyFile.FromFile(path));
            Assert.Throws<ArgumentException>(() => KeyFile.FromBytes([]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void Adaptive_embedding_roundtrips_and_only_touches_textured_pixels(int bits)
    {
        var engine = TestImages.Engine();
        var cover = TestImages.Photo(120, 100, seed: bits);
        var text = new string('x', 300) + "adaptive";
        var options = new EmbedOptions { BitsPerChannel = bits, Adaptive = true };
        var stego = engine.Embed(cover, Payload.FromText(text), Password, null, options);

        var result = engine.Reveal(stego, Password);
        Assert.Equal(text, result.Payload.Entries[0].GetText());
        Assert.True(result.Info.Adaptive);
        Assert.Equal(bits, result.Info.BitsPerChannel);

        // Outside the 256 public slots, only pixels with x + y even (the textured half of the checkerboard) change.
        var publicSlots = PublicSlots(cover);
        var changedReference = 0;
        for (var p = 0; p < cover.PixelCount; p++)
        {
            var x = p % cover.Width;
            var y = p / cover.Width;
            for (var c = 0; c < 3; c++)
            {
                if (cover.Pixels[(p * 4) + c] != stego.Pixels[(p * 4) + c] && (x + y) % 2 == 1 && !publicSlots.Contains((p * 3) + c))
                {
                    changedReference++;
                }
            }
        }

        Assert.Equal(0, changedReference);
    }

    [Fact]
    public void Adaptive_embedding_prefers_busy_regions()
    {
        // Left half flat, right half noisy: nearly every body change should land on the right.
        var width = 160;
        var height = 120;
        var data = new byte[width * height * 4];
        var random = new Random(4);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                var noisy = x >= width / 2;
                data[i] = (byte)(noisy ? random.Next(256) : 100);
                data[i + 1] = (byte)(noisy ? random.Next(256) : 120);
                data[i + 2] = (byte)(noisy ? random.Next(256) : 140);
                data[i + 3] = 255;
            }
        }

        var cover = new PixelBuffer(width, height, data);
        var engine = TestImages.Engine();
        var stego = engine.Embed(cover, Payload.FromText(RandomText(900)), Password, null, new EmbedOptions { Adaptive = true });

        int left = 0, right = 0;
        for (var p = 0; p < cover.PixelCount; p++)
        {
            for (var c = 0; c < 3; c++)
            {
                if (cover.Pixels[(p * 4) + c] != stego.Pixels[(p * 4) + c])
                {
                    _ = (p % width) < width / 2 ? left++ : right++;
                }
            }
        }

        Assert.True(right > 2_000, $"right {right}");
        Assert.True(left < 200, $"left {left} (public region and header only)");

        var plain = engine.Embed(cover, Payload.FromText(RandomText(900)), Password);
        var plainLeft = 0;
        for (var p = 0; p < cover.PixelCount; p++)
        {
            for (var c = 0; c < 3; c++)
            {
                plainLeft += (p % width) < width / 2 && cover.Pixels[(p * 4) + c] != plain.Pixels[(p * 4) + c] ? 1 : 0;
            }
        }

        Assert.True(plainLeft > 1_000, $"non-adaptive left {plainLeft}");
    }

    [Fact]
    public void Adaptive_capacity_is_reported_and_enforced()
    {
        var cover = TestImages.Photo(60, 50);
        var adaptive = new EmbedOptions { Adaptive = true };
        var capacity = StegoEngine.GetCapacity(cover, adaptive);
        var plain = StegoEngine.GetCapacity(cover, 1);
        Assert.InRange(capacity.ContainerBytes, (plain.ContainerBytes / 2) - 64, plain.ContainerBytes / 2);

        var fits = new byte[capacity.PayloadBytes - 50];
        RandomNumberGenerator.Fill(fits);
        var engine = TestImages.Engine();
        var stego = engine.Embed(cover, new Payload([PayloadEntry.FromFile("f", fits)]), Password, null, adaptive);
        Assert.Equal(fits, engine.Extract(stego, Password).Entries[0].Content.ToArray());

        var tooBig = new byte[capacity.PayloadBytes + 10];
        RandomNumberGenerator.Fill(tooBig);
        var ex = Assert.Throws<StegoException>(() => engine.Embed(cover, new Payload([PayloadEntry.FromFile("f", tooBig)]), Password, null, adaptive));
        Assert.Equal(StegoError.InsufficientCapacity, ex.Error);
    }

    [Fact]
    public void Threshold_aims_for_half_load_without_dropping_into_much_smoother_slots()
    {
        var histogram = new long[256];
        histogram[0] = 1000;
        histogram[10] = 100;
        histogram[200] = 50;

        Assert.Equal((200, 50L), V2Container.PickThreshold(histogram, 25));   // fits half full in the busiest slots
        Assert.Equal((10, 150L), V2Container.PickThreshold(histogram, 60));   // half full needs threshold 10
        Assert.Equal((5, 150L), V2Container.PickThreshold(histogram, 140));   // half full would need the flat slots; stay >= 10 / 2
        Assert.Equal((0, 1150L), V2Container.PickThreshold(histogram, 500));  // only fits with the flat slots
        Assert.Equal((0, 1150L), V2Container.PickThreshold(histogram, 5000)); // does not fit; the caller reports capacity
        Assert.Equal((0, 1150L), V2Container.PickThreshold(histogram, 0));    // fill mode
    }

    [Fact]
    public void Header_rejects_reserved_flags_and_unknown_kinds_as_unsupported()
    {
        Span<byte> header = stackalloc byte[ContainerHeader.PlainSize];
        new ContainerHeader(2, true, 17, ContentKind.Payload, 100, 90).Encode(header);
        Assert.Equal(new ContainerHeader(2, true, 17, ContentKind.Payload, 100, 90), ContainerHeader.Decode(header));

        var reserved = header.ToArray();
        reserved[0] |= 0x80;
        Assert.Equal(StegoError.UnsupportedFormat, Assert.Throws<StegoException>(() => ContainerHeader.Decode(reserved)).Error);

        var kind = header.ToArray();
        kind[2] = 9;
        Assert.Equal(StegoError.UnsupportedFormat, Assert.Throws<StegoException>(() => ContainerHeader.Decode(kind)).Error);

        var inconsistent = header.ToArray();
        new ContainerHeader(1, false, 0, ContentKind.Payload, 10, 11).Encode(inconsistent);
        Assert.Equal(StegoError.Corrupted, Assert.Throws<StegoException>(() => ContainerHeader.Decode(inconsistent)).Error);
    }

    [Fact]
    public void Public_region_shares_the_v1_salt_slots()
    {
        var carrier = new RgbLsbCarrier(TestImages.Photo(50, 50));
        var v1 = new SlotLayout(carrier);
        var v2 = new V2Layout(carrier);
        Assert.Equal(v1.SaltSlots, Enumerable.Range(0, 128).Select(r => v2.SlotAtRank(r)));
    }

    [Fact]
    public void Writer_output_is_pinned_for_a_fixed_seed()
    {
        // Pins the v2 writer end to end (public region, profile salt, header and body placement, LSB matching).
        // If this hash changes, images written by released builds may no longer reveal: treat it as a format break.
        var engine = new StegoEngine(KdfProfiles.Uniform(TestImages.FastKdf), new SeededRandom("v2 writer KAT"));
        var cover = TestImages.Photo(64, 48, seed: 3);
        var payload = new Payload([PayloadEntry.FromText("pinned"), PayloadEntry.FromFile("a.bin", [1, 2, 3])]);

        var plain = engine.Embed(cover, payload, Password, KeyFile.FromBytes("key"u8), new EmbedOptions { BitsPerChannel = 2 });
        var adaptive = engine.Embed(cover, payload, Password, null, new EmbedOptions { Adaptive = true, KdfProfile = KdfProfile.Paranoid });

        Assert.Equal("C190DCD13CA8F31C", Convert.ToHexString(SHA256.HashData(plain.Pixels))[..16]);
        Assert.Equal("F213403CF971CDC4", Convert.ToHexString(SHA256.HashData(adaptive.Pixels))[..16]);
    }

    private static HashSet<long> PublicSlots(PixelBuffer image)
    {
        var layout = new V2Layout(new RgbLsbCarrier(image));
        return [.. Enumerable.Range(0, V2Layout.PublicSlots).Select(r => layout.SlotAtRank(r))];
    }

    private static string RandomText(int length)
    {
        var random = new Random(length);
        return new string([.. Enumerable.Range(0, length).Select(_ => (char)random.Next('!', '~'))]);
    }

    private static void AssertNotFound(Action action) =>
        Assert.Equal(StegoError.NotFoundOrWrongPassword, Assert.Throws<StegoException>(action).Error);
}
