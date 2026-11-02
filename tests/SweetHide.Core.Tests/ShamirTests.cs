using System.Security.Cryptography;

using FsCheck.Xunit;

using SweetHide.Core.Crypto;
using SweetHide.Core.Payloads;
using SweetHide.Core.Stego;

namespace SweetHide.Core.Tests;

public class ShamirTests
{
    private const string Password = "split password";

    [Fact]
    public void Field_arithmetic_matches_the_aes_field()
    {
        Assert.Equal(0xC1, Shamir.Multiply(0x57, 0x83)); // FIPS-197 section 4.2
        Assert.Equal(0xFE, Shamir.Multiply(0x57, 0x13));
        for (var a = 0; a < 256; a++)
        {
            for (var b = 0; b < 256; b++)
            {
                Assert.Equal(ReferenceMultiply((byte)a, (byte)b), Shamir.Multiply((byte)a, (byte)b));
            }

            if (a != 0)
            {
                Assert.Equal(1, Shamir.Multiply((byte)a, Shamir.Inverse((byte)a)));
            }
        }
    }

    [Property(MaxTest = 50)]
    public void Any_threshold_shares_rebuild_the_secret(byte[] secret, byte k, byte n, int seed)
    {
        secret ??= [];
        var count = 2 + (n % 9);
        var threshold = 2 + (k % (count - 1));
        var shares = Shamir.Split(secret, threshold, count, new SeededRandom($"split {seed}"));

        var random = new Random(seed);
        var subset = Enumerable.Range(0, count).OrderBy(_ => random.Next()).Take(threshold)
            .Select(i => ((byte)(i + 1), shares[i])).ToList();
        Assert.Equal(secret, Shamir.Combine(subset));
    }

    [Fact]
    public void Fewer_than_threshold_shares_leave_every_secret_possible()
    {
        // With k - 1 real shares, choosing the missing share appropriately yields any secret byte at all:
        // the k - 1 shares do not constrain the secret.
        var shares = Shamir.Split([0x42], threshold: 3, count: 5, new SeededRandom("perfect secrecy"));
        var reachable = new HashSet<byte>();
        for (var guess = 0; guess < 256; guess++)
        {
            reachable.Add(Shamir.Combine([(1, shares[0]), (2, shares[1]), (5, [(byte)guess])])[0]);
        }

        Assert.Equal(256, reachable.Count);
    }

    [Fact]
    public void Shares_rebuild_through_images_with_any_three_of_five()
    {
        var engine = TestImages.Engine();
        var covers = Enumerable.Range(1, 5).Select(i => TestImages.Photo(120, 100, seed: i)).ToList();
        var file = new byte[1500];
        new Random(5).NextBytes(file);
        var payload = new Payload([PayloadEntry.FromText("split secret ลับ"), PayloadEntry.FromFile("plan.bin", file)]);

        var images = engine.EmbedShares(covers, payload, threshold: 3, Password, options: new EmbedOptions { Adaptive = true });
        var shares = images.Select(image => engine.Reveal(image, Password)).ToList();

        Assert.All(shares, result => Assert.Null(result.Payload));
        Assert.Equal([1, 2, 3, 4, 5], shares.Select(r => r.Share!.Index));
        Assert.All(shares, r => Assert.Equal((3, 5), (r.Share!.Threshold, r.Share.ShareCount)));
        Assert.Single(shares.Select(r => r.Share!.SplitId).Distinct());

        foreach (var subset in new[] { new[] { 0, 1, 2 }, [2, 3, 4], [0, 2, 4], [4, 1, 3, 0] })
        {
            var back = StegoEngine.CombineShares(subset.Select(i => shares[i].Share!));
            Assert.Equal("split secret ลับ", back.Entries[0].GetText());
            Assert.Equal(file, back.Entries[1].Content.ToArray());
        }

        var ex = Assert.Throws<StegoException>(() => StegoEngine.CombineShares([shares[0].Share!, shares[3].Share!, shares[3].Share!]));
        Assert.Equal(StegoError.NotEnoughShares, ex.Error);

        var split = Assert.Throws<StegoException>(() => engine.Extract(images[0], Password));
        Assert.Equal(StegoError.SplitShare, split.Error);
        Assert.Contains("share 1 of a 3-of-5 split", split.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Shares_of_different_splits_do_not_mix()
    {
        var a = ShamirShare.Split([1, 2, 3, 4, 5, 6], 2, 3, new SeededRandom("a")).Select(e => ShamirShare.Decode(e)).ToList();
        var b = ShamirShare.Split([1, 2, 3, 4, 5, 6], 2, 3, new SeededRandom("b")).Select(e => ShamirShare.Decode(e)).ToList();

        Assert.Equal(StegoError.SharesDoNotMatch, Assert.Throws<StegoException>(() => ShamirShare.Combine([a[0], b[1]])).Error);
        Assert.Equal(StegoError.NotEnoughShares, Assert.Throws<StegoException>(() => ShamirShare.Combine([])).Error);
    }

    [Fact]
    public void A_forged_share_is_detected_after_combining()
    {
        var payloadBytes = PayloadSerializer.Serialize(Payload.FromText("pay alice 10"));
        var encoded = ShamirShare.Split(payloadBytes, 2, 3, new SeededRandom("forgery"));

        // Shamir is malleable: flipping bits of one share flips predictable bits of the secret. The MAC catches it.
        var forged = (byte[])encoded[1].Clone();
        forged[^1] ^= 0x01;
        var ex = Assert.Throws<StegoException>(() => ShamirShare.Combine([ShamirShare.Decode(encoded[0]), ShamirShare.Decode(forged)]));
        Assert.Equal(StegoError.Corrupted, ex.Error);

        Assert.Equal("pay alice 10", ShamirShare.Combine([ShamirShare.Decode(encoded[0]), ShamirShare.Decode(encoded[1])]).Entries[0].GetText());
    }

    [Fact]
    public void Malformed_share_encodings_are_rejected()
    {
        var valid = ShamirShare.Split([9, 9, 9], 2, 2, new SeededRandom("malformed"))[0];
        Assert.Equal(StegoError.Corrupted, Assert.Throws<StegoException>(() => ShamirShare.Decode(valid.AsSpan(0, 40))).Error);

        var version = (byte[])valid.Clone();
        version[0] = 2;
        Assert.Equal(StegoError.UnsupportedFormat, Assert.Throws<StegoException>(() => ShamirShare.Decode(version)).Error);

        foreach (var (offset, value) in new[] { (1, 1), (2, 1), (3, 0), (3, 3) })
        {
            var bad = (byte[])valid.Clone();
            bad[offset] = (byte)value;
            Assert.Equal(StegoError.Corrupted, Assert.Throws<StegoException>(() => ShamirShare.Decode(bad)).Error);
        }
    }

    [Fact]
    public void Arguments_and_capacity_are_checked_before_any_work()
    {
        var engine = TestImages.Engine();
        var payload = Payload.FromText(new string('x', 10));
        var big = TestImages.Photo(80, 60);
        var small = TestImages.Photo(20, 20);

        Assert.Throws<ArgumentException>(() => engine.EmbedShares([big], payload, 2, Password));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.EmbedShares([big, big], payload, 1, Password));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.EmbedShares([big, big], payload, 3, Password));
        var ex = Assert.Throws<StegoException>(() => engine.EmbedShares([big, small, big], payload, 2, Password));
        Assert.Equal(StegoError.InsufficientCapacity, ex.Error);

        var size = StegoEngine.GetShareContainerSize(payload);
        Assert.Equal(StegoEngine.GetContainerSize(payload) + 52 + ShamirShare.MacKeySize, size);
    }

    private static byte ReferenceMultiply(byte a, byte b)
    {
        // Schoolbook carry-less multiplication, then reduction by x^8 + x^4 + x^3 + x + 1.
        var product = 0;
        for (var i = 0; i < 8; i++)
        {
            if (((b >> i) & 1) != 0)
            {
                product ^= a << i;
            }
        }

        for (var bit = 14; bit >= 8; bit--)
        {
            if (((product >> bit) & 1) != 0)
            {
                product ^= 0x11B << (bit - 8);
            }
        }

        return (byte)product;
    }

    [Fact]
    public void Split_uses_fresh_randomness_each_time()
    {
        var secret = RandomNumberGenerator.GetBytes(64);
        var first = Shamir.Split(secret, 2, 2, SystemRandomSource.Instance);
        var second = Shamir.Split(secret, 2, 2, SystemRandomSource.Instance);
        Assert.NotEqual(first[0], second[0]);
    }
}
