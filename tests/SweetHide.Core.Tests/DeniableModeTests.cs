using SweetHide.Core.Crypto;
using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;
using SweetHide.Core.Stego;

namespace SweetHide.Core.Tests;

public class DeniableModeTests
{
    private static readonly DeniablePart Decoy = new(Payload.FromText("shopping list: rice, eggs"), "decoy password");
    private static readonly DeniablePart Secret = new(Payload.FromText("the real secret"), "real password ลับมาก");

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Each_password_reveals_only_its_own_payload(int bits)
    {
        var engine = TestImages.Engine();
        var stego = engine.EmbedDeniable(TestImages.Photo(90, 70, seed: bits), Decoy, Secret, new EmbedOptions { BitsPerChannel = bits });

        var decoy = engine.Reveal(stego, Decoy.Password);
        var secret = engine.Reveal(stego, Secret.Password);

        Assert.Equal("shopping list: rice, eggs", Assert.Single(decoy.Payload!.Entries).GetText());
        Assert.Equal("the real secret", Assert.Single(secret.Payload!.Entries).GetText());
        Assert.Equal(new HiddenDataInfo(2, HidingMode.Deniable, bits, false, KdfProfile.Normal), decoy.Info);
        Assert.Equal(decoy.Info, secret.Info);
        Assert.Equal(StegoError.NotFoundOrWrongPassword, Assert.Throws<StegoException>(() => engine.Extract(stego, "neither")).Error);
    }

    [Fact]
    public void A_single_payload_with_chaff_looks_the_same_as_two_payloads()
    {
        var engine = TestImages.Engine();
        var cover = TestImages.Photo(120, 90, seed: 7);
        var withSecret = engine.EmbedDeniable(cover, Decoy, Secret);
        var withChaff = engine.EmbedDeniable(cover, Decoy, second: null);

        // What the decoy password shows is identical in both cases...
        Assert.Equal(engine.Reveal(withSecret, Decoy.Password).Info, engine.Reveal(withChaff, Decoy.Password).Info);

        // ...and both halves are completely filled either way (about half the values change at 1 bit per channel).
        foreach (var stego in new[] { withSecret, withChaff })
        {
            var (first, second) = ChangedRatioPerHalf(cover, stego);
            Assert.InRange(first, 0.47, 0.53);
            Assert.InRange(second, 0.47, 0.53);
        }
    }

    [Fact]
    public void Which_half_holds_which_part_is_random()
    {
        var cover = TestImages.Photo(60, 50);
        var placements = new HashSet<ContainerPlacement>();
        for (var seed = 0; seed < 12; seed++)
        {
            var engine = new StegoEngine(KdfProfiles.Uniform(TestImages.FastKdf), new SeededRandom($"halves {seed}"));
            var stego = engine.EmbedDeniable(cover, Decoy, Secret);
            using var match = new StegoImageReader(new RgbLsbCarrier(stego), KdfProfiles.Uniform(TestImages.FastKdf)).TryPassword(Decoy.Password, null);
            placements.Add(match!.V2!.Placement);
        }

        Assert.Equal([ContainerPlacement.FirstHalf, ContainerPlacement.SecondHalf], placements.Order());
    }

    [Fact]
    public void Parts_need_different_keys_and_adaptive_is_refused()
    {
        var engine = TestImages.Engine();
        var cover = TestImages.Photo(60, 50);
        var samePassword = Secret with { Password = Decoy.Password };
        Assert.Throws<ArgumentException>(() => engine.EmbedDeniable(cover, Decoy, samePassword));

        // Same password but a key file on one side gives different keys, which is fine.
        var withKeyFile = samePassword with { KeyFile = KeyFile.FromBytes("usb stick"u8) };
        var stego = engine.EmbedDeniable(cover, Decoy, withKeyFile);
        Assert.Equal("the real secret", engine.Extract(stego, Decoy.Password, withKeyFile.KeyFile).Entries[0].GetText());
        Assert.Equal("shopping list: rice, eggs", engine.Extract(stego, Decoy.Password).Entries[0].GetText());

        Assert.Throws<ArgumentException>(() => engine.EmbedDeniable(cover, Decoy, Secret, new EmbedOptions { Adaptive = true }));
    }

    [Fact]
    public void Each_part_gets_half_the_capacity()
    {
        var engine = TestImages.Engine();
        var cover = TestImages.Photo(70, 50);
        var capacity = StegoEngine.GetDeniableCapacity(cover);
        Assert.InRange(capacity.ContainerBytes, (StegoEngine.GetCapacity(cover).ContainerBytes / 2) - 30, StegoEngine.GetCapacity(cover).ContainerBytes / 2);

        var full = FilePayload(capacity.ContainerBytes - StegoEngine.GetContainerSize(FilePayload(0, seed: 0)), seed: 1);
        Assert.Equal(capacity.ContainerBytes, StegoEngine.GetContainerSize(full));
        var stego = engine.EmbedDeniable(cover, new DeniablePart(full, "a"), new DeniablePart(full, "b"));
        Assert.Equal(full.Entries[0].Content.ToArray(), engine.Extract(stego, "b").Entries[0].Content.ToArray());

        var tooBig = FilePayload(full.Entries[0].Length + 1, seed: 2);
        var ex = Assert.Throws<StegoException>(() => engine.EmbedDeniable(cover, new DeniablePart(Payload.FromText("small"), "a"), new DeniablePart(tooBig, "b")));
        Assert.Equal(StegoError.InsufficientCapacity, ex.Error);
    }

    private static Payload FilePayload(long size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return new Payload([PayloadEntry.FromFile("f", bytes)]);
    }

    /// <summary>Share of R/G/B values changed in each half of the data region.</summary>
    private static (double First, double Second) ChangedRatioPerHalf(PixelBuffer cover, PixelBuffer stego)
    {
        var carrier = new RgbLsbCarrier(cover);
        var layout = new V2Layout(carrier);
        var data = layout.SlotCount - V2Layout.PublicSlots;
        long changedFirst = 0, changedSecond = 0;
        for (var rank = V2Layout.PublicSlots; rank < layout.SlotCount; rank++)
        {
            var offset = carrier.ByteOffset(layout.SlotAtRank(rank));
            if (cover.Pixels[offset] != stego.Pixels[offset])
            {
                _ = rank < V2Layout.PublicSlots + (data / 2) ? changedFirst++ : changedSecond++;
            }
        }

        return ((double)changedFirst / (data / 2), (double)changedSecond / (data - (data / 2)));
    }
}
