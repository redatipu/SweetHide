using SweetHide.Core.Imaging;
using SweetHide.Core.Stego;

namespace SweetHide.Core.Tests;

public class RgbLsbCarrierTests
{
    [Fact]
    public void Skips_transparent_pixels_and_never_touches_alpha()
    {
        var image = TestImages.Photo(30, 20, withTransparency: true);
        var usable = 0;
        for (var i = 3; i < image.Pixels.Length; i += 4)
        {
            usable += image.Pixels[i] == 0 ? 0 : 1;
        }

        var carrier = new RgbLsbCarrier(image);
        Assert.Equal(usable, carrier.UsablePixels);
        Assert.Equal(usable * 3L, carrier.SlotCount);

        var alphaBefore = AlphaOf(image);
        var random = new Random(1);
        for (long slot = 0; slot < carrier.SlotCount; slot++)
        {
            carrier.Write(slot, random.Next(16), 4, random.Next(2) == 1);
        }

        Assert.Equal(alphaBefore, AlphaOf(image));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Reads_back_what_was_written_without_touching_other_slots(int bits)
    {
        var image = TestImages.Photo(16, 16, seed: bits);
        var carrier = new RgbLsbCarrier(image);
        var random = new Random(bits);
        for (long slot = 0; slot < carrier.SlotCount; slot++)
        {
            var before = image.Pixels.ToArray();
            var value = random.Next(1 << bits);
            carrier.Write(slot, value, bits, random.Next(2) == 1);

            Assert.Equal(value, carrier.Read(slot, bits));
            var after = image.Pixels.ToArray();
            var offset = carrier.ByteOffset(slot);
            for (var i = 0; i < after.Length; i++)
            {
                if (i != offset)
                {
                    Assert.Equal(before[i], after[i]);
                }
            }
        }
    }

    [Fact]
    public void Same_image_gives_same_slots_after_writing()
    {
        // Invariant 1 of IStegoCarrier: the slot list is recomputable from the stego output.
        var image = TestImages.Photo(40, 30, withTransparency: true);
        var before = new RgbLsbCarrier(image.Clone());
        var carrier = new RgbLsbCarrier(image);
        for (long slot = 0; slot < carrier.SlotCount; slot += 7)
        {
            carrier.Write(slot, 3, 2, coin: true);
        }

        var after = new RgbLsbCarrier(image);
        Assert.Equal(before.SlotCount, after.SlotCount);
        for (long slot = 0; slot < after.SlotCount; slot++)
        {
            Assert.Equal(before.ByteOffset(slot), after.ByteOffset(slot));
        }
    }

    [Fact]
    public void Texture_comes_only_from_reference_pixels()
    {
        var image = TestImages.Photo(41, 33, seed: 5, withTransparency: true);
        var carrier = new RgbLsbCarrier(image);
        var textures = Enumerable.Range(0, (int)carrier.SlotCount).Select(s => carrier.Texture(s)).ToArray();

        // Half the pixels (checkerboard) are references; their texture is -1 and the rest is 0..255.
        Assert.Equal(carrier.TexturedSlotCount, textures.LongCount(t => t >= 0));
        Assert.All(textures, t => Assert.InRange(t, -1, 255));

        // Writing every textured slot, at any depth, leaves every texture exactly as it was.
        var random = new Random(2);
        for (long slot = 0; slot < carrier.SlotCount; slot++)
        {
            if (textures[slot] >= 0)
            {
                carrier.Write(slot, random.Next(16), 4, random.Next(2) == 1);
            }
        }

        var reread = new RgbLsbCarrier(image);
        Assert.Equal(textures, Enumerable.Range(0, (int)reread.SlotCount).Select(s => reread.Texture(s)));
    }

    [Fact]
    public void Texture_is_high_in_noise_and_zero_in_flat_areas()
    {
        var flat = new RgbLsbCarrier(TestImages.Solid(20, 20, 90, 90, 90));
        var noise = new byte[20 * 20 * 4];
        new Random(3).NextBytes(noise);
        for (var i = 3; i < noise.Length; i += 4)
        {
            noise[i] = 255;
        }

        var busy = new RgbLsbCarrier(new PixelBuffer(20, 20, noise));
        double Mean(RgbLsbCarrier c) =>
            Enumerable.Range(0, (int)c.SlotCount).Select(s => c.Texture(s)).Where(t => t >= 0).Average();

        Assert.Equal(0, Mean(flat));
        Assert.True(Mean(busy) > 100, $"mean texture {Mean(busy)}");
    }

    private static byte[] AlphaOf(PixelBuffer image)
    {
        var pixels = image.Pixels;
        var alpha = new byte[pixels.Length / 4];
        for (var i = 0; i < alpha.Length; i++)
        {
            alpha[i] = pixels[(i * 4) + 3];
        }

        return alpha;
    }
}
