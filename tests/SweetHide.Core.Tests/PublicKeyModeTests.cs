using System.Security.Cryptography;

using SweetHide.Core.Crypto;
using SweetHide.Core.Payloads;
using SweetHide.Core.Stego;

namespace SweetHide.Core.Tests;

public class PublicKeyModeTests
{
    [Theory]
    [InlineData("A12UEL5L", "a", "")]
    [InlineData("a12uel5l", "a", "")]
    [InlineData("an83characterlonghumanreadablepartthatcontainsthenumber1andtheexcludedcharactersbio1tt5tgs", "an83characterlonghumanreadablepartthatcontainsthenumber1andtheexcludedcharactersbio", "")]
    [InlineData("abcdef1qpzry9x8gf2tvdw0s3jn54khce6mua7lmqqqxw", "abcdef", "00443214C74254B635CF84653A56D7C675BE77DF")]
    [InlineData("11qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqc8247j", "1", "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("split1checkupstagehandshakeupstreamerranterredcaperred2y9e3w", "split", "C5F38B70305F519BF66D85FB6CF03058F3DDE463ECD7918F2DC743918F2D")]
    public void Bech32_accepts_bip173_valid_vectors(string text, string hrp, string hex)
    {
        Assert.True(Bech32.TryDecode(text, hrp, out var data));
        Assert.Equal(hex, Convert.ToHexString(data));
        Assert.Equal(text.ToLowerInvariant(), Bech32.Encode(hrp, data));
    }

    [Theory]
    [InlineData("pzry9x0s0muk", "pzry9x0s0muk")] // no separator
    [InlineData("1pzry9x0s0muk", "")]            // empty hrp
    [InlineData("x1b4n0q5v", "x")]               // invalid data character
    [InlineData("li1dgmt3", "li")]               // checksum too short
    [InlineData("A1G7SGD8", "a")]                // checksum computed with an uppercase hrp
    [InlineData("A12uEL5L", "a")]                // mixed case
    [InlineData("a12uel5m", "a")]                // one character changed
    public void Bech32_rejects_invalid_strings(string text, string hrp) => Assert.False(Bech32.TryDecode(text, hrp, out _));

    [Fact]
    public void Public_keys_roundtrip_as_text_and_reject_typos()
    {
        using var identity = Identity.Generate();
        var text = identity.Recipient.ToString();
        Assert.StartsWith("sweethide1", text, StringComparison.Ordinal);
        Assert.Equal(68, text.Length);
        Assert.Equal(identity.Recipient, Recipient.Parse(text));
        Assert.Equal(identity.Recipient, Recipient.Parse($"  {text.ToUpperInvariant()}\n"));

        for (var i = 10; i < text.Length; i++)
        {
            var typo = text[..i] + (text[i] == 'q' ? 'p' : 'q') + text[(i + 1)..];
            Assert.False(Recipient.TryParse(typo, out _), $"typo at {i} accepted");
        }

        Assert.False(Recipient.TryParse(text[..^1], out _));
        Assert.False(Recipient.TryParse(Bech32.Encode("age", identity.Recipient.Key), out _));
        Assert.False(Recipient.TryParse(Bech32.Encode("sweethide", new byte[31]), out _));
        Assert.False(Recipient.TryParse(Bech32.Encode("sweethide", new byte[32]), out _)); // low-order point
        Assert.Throws<FormatException>(() => Recipient.Parse("sweethide1nope"));
    }

    [Fact]
    public void Identity_file_roundtrips_and_needs_the_passphrase()
    {
        using var identity = Identity.Generate();
        var file = identity.Export("key file passphrase", TestImages.FastKdf);

        Assert.StartsWith("-----BEGIN SWEETHIDE PRIVATE KEY-----\nPublic-Key: sweethide1", file, StringComparison.Ordinal);
        Assert.Contains(identity.Recipient.ToString(), file, StringComparison.Ordinal);
        using (var back = Identity.Import(file.Replace("\n", "\r\n", StringComparison.Ordinal), "key file passphrase"))
        {
            Assert.Equal(identity.Recipient, back.Recipient);
        }

        Assert.Throws<CryptographicException>(() => Identity.Import(file, "wrong passphrase"));
        Assert.Throws<FormatException>(() => Identity.Import("hello", "key file passphrase"));
        Assert.Throws<ArgumentException>(() => identity.Export(string.Empty));

        // Flipping any byte of the encoded key is caught (format check, Argon2 limits or authentication).
        var lines = file.Split('\n');
        var blob = Convert.FromBase64String(string.Concat(lines[2..^2]));
        for (var i = 0; i < blob.Length; i += 7)
        {
            var tampered = (byte[])blob.Clone();
            tampered[i] ^= 0x01;
            var text = string.Join('\n', [lines[0], lines[1], Convert.ToBase64String(tampered), lines[^2]]);
            var ex = Record.Exception(() => Identity.Import(text, "key file passphrase").Dispose());
            Assert.True(ex is CryptographicException or FormatException, $"byte {i}: {ex?.GetType().Name ?? "accepted"}");
        }
    }

    [Fact]
    public void Every_recipient_and_only_they_can_reveal()
    {
        using var alice = Identity.Generate();
        using var bob = Identity.Generate();
        using var carol = Identity.Generate();
        using var mallory = Identity.Generate();
        var engine = TestImages.Engine();
        var payload = new Payload([PayloadEntry.FromText("for my friends"), PayloadEntry.FromFile("map.bin", [1, 2, 3])]);

        var stego = engine.Embed(TestImages.Photo(100, 80), payload, [alice.Recipient, bob.Recipient, carol.Recipient, bob.Recipient]);

        foreach (var friend in new[] { alice, bob, carol })
        {
            var result = engine.Reveal(stego, friend);
            Assert.Equal("for my friends", result.Payload.Entries[0].GetText());
            Assert.Equal(new HiddenDataInfo(2, HidingMode.Recipients, 1, false, null), result.Info);
        }

        Assert.Equal(StegoError.NotFoundOrWrongPassword, Assert.Throws<StegoException>(() => engine.Extract(stego, mallory)).Error);
        Assert.Equal(StegoError.NotFoundOrWrongPassword, Assert.Throws<StegoException>(() => engine.Extract(stego, "a password")).Error);
        Assert.Equal(StegoError.NotFoundOrWrongPassword, Assert.Throws<StegoException>(() => engine.Extract(TestImages.Photo(100, 80), alice)).Error);
    }

    [Fact]
    public void Works_with_adaptive_embedding_and_more_bits()
    {
        using var alice = Identity.Generate();
        var engine = TestImages.Engine();
        var stego = engine.Embed(TestImages.Photo(120, 90), Payload.FromText("adaptive for alice"), [alice.Recipient], new EmbedOptions { Adaptive = true, BitsPerChannel = 2 });
        var result = engine.Reveal(stego, alice);
        Assert.Equal("adaptive for alice", result.Payload.Entries[0].GetText());
        Assert.True(result.Info.Adaptive);
    }

    [Fact]
    public void Ephemeral_key_top_bit_is_random()
    {
        using var alice = Identity.Generate();
        var cover = TestImages.Photo(60, 50);
        var topBits = new HashSet<int>();
        for (var seed = 0; seed < 12; seed++)
        {
            var engine = new StegoEngine(KdfProfiles.Uniform(TestImages.FastKdf), new SeededRandom($"top bit {seed}"));
            var stego = engine.Embed(cover, Payload.FromText("x"), [alice.Recipient]);
            var reader = new StegoImageReader(new RgbLsbCarrier(stego), KdfProfiles.Uniform(TestImages.FastKdf));
            topBits.Add(reader.PublicBytes![^1] >> 7);
            Assert.Equal("x", engine.Extract(stego, alice).Entries[0].GetText());
        }

        Assert.Equal([0, 1], topBits.Order());
    }

    [Fact]
    public void Recipient_limits_and_capacity()
    {
        var engine = TestImages.Engine();
        var cover = TestImages.Photo(120, 100);
        var recipients = Enumerable.Range(0, StegoEngine.MaxRecipients + 1).Select(_ => Identity.Generate()).ToList();
        try
        {
            var all = recipients.Select(r => r.Recipient).ToList();
            var stego = engine.Embed(cover, Payload.FromText("crowd"), all.Take(StegoEngine.MaxRecipients).ToList());
            Assert.Equal("crowd", engine.Extract(stego, recipients[StegoEngine.MaxRecipients - 1]).Entries[0].GetText());

            Assert.Throws<ArgumentException>(() => engine.Embed(cover, Payload.FromText("x"), all));
            Assert.Throws<ArgumentException>(() => engine.Embed(cover, Payload.FromText("x"), Array.Empty<Recipient>()));

            var one = StegoEngine.GetRecipientCapacity(cover, 1).ContainerBytes;
            var two = StegoEngine.GetRecipientCapacity(cover, 2).ContainerBytes;
            Assert.Equal(RecipientStanza.Size, one - two);
            Assert.Equal(StegoEngine.GetCapacity(cover).ContainerBytes - RecipientStanza.Size, one);
        }
        finally
        {
            recipients.ForEach(r => r.Dispose());
        }
    }
}
