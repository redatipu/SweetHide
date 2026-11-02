using FsCheck;
using FsCheck.Xunit;

using SweetHide.Core.Crypto;
using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;
using SweetHide.Core.Stego;

namespace SweetHide.Core.Tests;

public class PaletteModeTests
{
    private const string Password = "จานสีหลบซ่อน";

    private static readonly StegoEngine Engine = new(TestImages.FastKdf);
    private static readonly Payload Message = Payload.FromText("hidden in a 256-colour picture");

    [Fact]
    public void Round_trips_through_a_palette_png_and_keeps_the_palette()
    {
        var cover = Gradient(160, 120);
        var stego = Engine.Embed(cover, Message, Password);

        using var file = new MemoryStream();
        IndexedPng.Write(stego, file);
        var back = IndexedPng.Read(file.ToArray());

        Assert.Equal(cover.Palette, back.Palette);
        Assert.Equal(Message.Entries[0].GetText(), Engine.Extract(back, Password).Entries[0].GetText());
        Assert.Equal(new HiddenDataInfo(2, HidingMode.Password, 1, false, KdfProfile.Normal), Engine.Reveal(back, Password).Info);
        Assert.Equal(cover.Indices, Gradient(160, 120).Indices); // the cover itself was not touched
    }

    [Fact]
    public void Every_changed_pixel_moves_to_a_look_alike_colour()
    {
        var cover = Gradient(160, 120);
        var stego = Engine.Embed(cover, Message, Password);

        var changed = 0;
        for (var i = 0; i < cover.Indices.Length; i++)
        {
            if (cover.Indices[i] != stego.Indices[i])
            {
                changed++;
                Assert.True(Distance(cover.Palette[cover.Indices[i]], cover.Palette[stego.Indices[i]]) <= PaletteCarrier.MaxStepDistance);
            }
        }

        Assert.InRange(changed, 1, cover.Indices.Length / 2);
    }

    [Fact]
    public void Wrong_password_key_file_and_plain_images_find_nothing()
    {
        var keyFile = KeyFile.FromBytes("palette key"u8);
        var stego = Engine.Embed(Gradient(160, 120), Message, Password, keyFile);

        Assert.Equal(StegoError.NotFoundOrWrongPassword, Assert.Throws<StegoException>(() => Engine.Reveal(stego, "wrong")).Error);
        Assert.Equal(StegoError.NotFoundOrWrongPassword, Assert.Throws<StegoException>(() => Engine.Reveal(stego, Password)).Error);
        Assert.Equal(StegoError.NotFoundOrWrongPassword, Assert.Throws<StegoException>(() => Engine.Reveal(Gradient(160, 120), Password)).Error);
        Assert.Equal(Message.Entries[0].GetText(), Engine.Extract(stego, Password, keyFile).Entries[0].GetText());
    }

    [Fact]
    public void Kdf_profile_is_honoured()
    {
        var engine = new StegoEngine(new KdfProfiles(TestImages.FastKdf, TestImages.FastKdf with { Iterations = 2 }, TestImages.FastKdf with { Iterations = 3 }));
        var stego = engine.Embed(Gradient(160, 120), Message, Password, options: new EmbedOptions { KdfProfile = KdfProfile.Paranoid });

        Assert.Equal(KdfProfile.Paranoid, engine.Reveal(stego, Password).Info.KdfProfile);
    }

    [Fact]
    public void Palettes_without_look_alike_colours_hold_nothing()
    {
        // Black, white, red and blue: every step would be a visible jump.
        var logo = new IndexedImage(64, 64, Enumerable.Range(0, 64 * 64).Select(i => (byte)(i % 4)).ToArray(), [0xFF000000u, 0xFFFFFFFFu, 0xFFFF0000u, 0xFF0000FFu]);

        Assert.Equal(0, StegoEngine.GetCapacity(logo).ContainerBytes);
        Assert.Equal(StegoError.InsufficientCapacity, Assert.Throws<StegoException>(() => Engine.Embed(logo, Message, Password)).Error);
    }

    [Fact]
    public void Translucent_entries_are_never_used()
    {
        // Two near-identical colours, one of them half transparent: only the opaque pixels count, and none can step.
        var image = new IndexedImage(10, 10, Enumerable.Range(0, 100).Select(i => (byte)(i % 2)).ToArray(), [0xFF808080u, 0x80818181u]);
        Assert.Equal(0, new PaletteCarrier(image).SlotCount);

        var cover = Gradient(160, 120, translucentEntries: 16);
        var stego = Engine.Embed(cover, Message, Password);
        for (var i = 0; i < cover.Indices.Length; i++)
        {
            if (cover.Palette[cover.Indices[i]] >> 24 != 0xFF)
            {
                Assert.Equal(cover.Indices[i], stego.Indices[i]);
            }
        }

        Assert.Equal(Message.Entries[0].GetText(), Engine.Extract(stego, Password).Entries[0].GetText());
    }

    [Fact]
    public void Capacity_is_one_bit_per_usable_pixel_and_rejects_rgb_options()
    {
        var cover = Gradient(160, 120);
        var capacity = StegoEngine.GetCapacity(cover);

        Assert.Equal(1, capacity.BitsPerChannel);
        Assert.Equal(new PaletteCarrier(cover).UsablePixels, capacity.UsablePixels);
        Assert.InRange(capacity.ContainerBytes, (capacity.UsablePixels / 8) - 200, capacity.UsablePixels / 8);
        Assert.Throws<ArgumentException>(() => Engine.Embed(cover, Message, Password, options: new EmbedOptions { BitsPerChannel = 2 }));
        Assert.Throws<ArgumentException>(() => Engine.Embed(cover, Message, Password, options: new EmbedOptions { Adaptive = true }));
    }

    [Property(MaxTest = 100)]
    public void Carrier_reads_back_what_it_writes_and_keeps_its_slots(int seed, bool coin)
    {
        var random = new Random(seed);
        var palette = Enumerable.Range(0, random.Next(2, 257)).Select(_ => 0xFF000000u | (uint)random.Next(0, 0x1000000)).ToList();
        var image = new IndexedImage(17, 13, Enumerable.Range(0, 17 * 13).Select(_ => (byte)random.Next(palette.Count)).ToArray(), palette);
        var carrier = new PaletteCarrier(image);
        if (carrier.SlotCount == 0)
        {
            return;
        }

        var slot = random.Next((int)carrier.SlotCount);
        var before = Enumerable.Range(0, (int)carrier.SlotCount).Select(s => carrier.Read(s, 1)).ToArray();
        var value = random.Next(2);
        carrier.Write(slot, value, 1, coin);

        Assert.Equal(value, carrier.Read(slot, 1));
        var again = new PaletteCarrier(image);
        Assert.Equal(carrier.SlotCount, again.SlotCount);
        for (var s = 0; s < carrier.SlotCount; s++)
        {
            Assert.Equal(s == slot ? value : before[s], again.Read(s, 1));
        }
    }

    /// <summary>A smooth photo-like picture quantised to a 256-colour palette whose colours sit close together.</summary>
    private static IndexedImage Gradient(int width, int height, int translucentEntries = 0)
    {
        var palette = new uint[256];
        for (var i = 0; i < 256; i++)
        {
            var alpha = i < translucentEntries ? 0x80u : 0xFFu;
            palette[i] = (alpha << 24) | ((uint)i << 16) | ((uint)(255 - (i / 2)) << 8) | (uint)((i * 3) % 256 / 2);
        }

        var random = new Random(11);
        var indices = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = (x * 255 / (width - 1) + (y * 40 / height) + random.Next(-6, 7)) % 256;
                indices[(y * width) + x] = (byte)Math.Clamp(v, 0, 255);
            }
        }

        return new IndexedImage(width, height, indices, palette);
    }

    private static double Distance(uint a, uint b)
    {
        var dr = (int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF);
        var dg = (int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF);
        var db = (int)(a & 0xFF) - (int)(b & 0xFF);
        return Math.Sqrt((dr * dr) + (dg * dg) + (db * db));
    }
}
