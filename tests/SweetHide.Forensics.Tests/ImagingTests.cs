using SweetHide.Core.Analysis;
using SweetHide.Core.Imaging;
using SweetHide.Forensics.Detection;
using SweetHide.Forensics.Imaging;
using SweetHide.Forensics.Lsb;

using static SweetHide.Forensics.Tests.TestData;

namespace SweetHide.Forensics.Tests;

public class ImageFiltersTests
{
    private static PixelBuffer Pixels(params (byte R, byte G, byte B, byte A)[] pixels) =>
        new(pixels.Length, 1, [.. pixels.SelectMany(p => new[] { p.R, p.G, p.B, p.A })]);

    private static (byte R, byte G, byte B, byte A) At(PixelBuffer image, int index)
    {
        var p = image.Pixels.Slice(index * 4, 4);
        return (p[0], p[1], p[2], p[3]);
    }

    [Fact]
    public void Bit_plane_shows_one_bit_of_one_channel()
    {
        var image = Pixels((0b101, 0, 0, 0), (0b010, 0, 0, 255));

        Assert.Equal((255, 255, 255, 255), At(ImageFilters.BitPlane(image, ColorChannel.Red, 0), 0));
        Assert.Equal((0, 0, 0, 255), At(ImageFilters.BitPlane(image, ColorChannel.Red, 1), 0));
        Assert.Equal((255, 255, 255, 255), At(ImageFilters.BitPlane(image, ColorChannel.Red, 1), 1));
        Assert.Equal((0, 0, 0, 255), At(ImageFilters.BitPlane(image, ColorChannel.Alpha, 0), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageFilters.BitPlane(image, ColorChannel.Red, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageFilters.BitPlane(image, (ColorChannel)7, 0));
    }

    [Fact]
    public void Combined_plane_and_channel_views()
    {
        var photo = Photo(16, 16);

        Assert.Equal(LsbAnalyzer.RenderBitPlane(photo, 2).Pixels.ToArray(), ImageFilters.CombinedBitPlane(photo, 2).Pixels.ToArray());
        Assert.Equal((77, 77, 77, 255), At(ImageFilters.ChannelGray(Pixels((1, 77, 3, 0)), ColorChannel.Green), 0));
    }

    [Fact]
    public void Combines_two_images()
    {
        var a = Pixels((5, 200, 0xF0, 10));
        var b = Pixels((10, 100, 0x0F, 20));

        Assert.Equal((15, 172, 0xFF, 255), At(ImageFilters.Combine(a, b, CombineMode.Xor), 0));
        Assert.Equal((251, 100, 0xE1, 255), At(ImageFilters.Combine(a, b, CombineMode.Subtract), 0));
        Assert.Equal((5, 100, 0xE1, 255), At(ImageFilters.Combine(a, b, CombineMode.AbsoluteDifference), 0));
        Assert.Equal((15, 44, 0xFF, 255), At(ImageFilters.Combine(a, b, CombineMode.Add), 0));
        Assert.Equal((0, 64, 0, 255), At(ImageFilters.Combine(a, b, CombineMode.And), 0));
        Assert.Equal((15, 236, 0xFF, 255), At(ImageFilters.Combine(a, b, CombineMode.Or), 0));
        Assert.All(ImageFilters.Combine(Photo(8, 8), Photo(8, 8), CombineMode.Xor).Pixels.ToArray().Where((_, i) => i % 4 != 3), v => Assert.Equal(0, v));
        Assert.Throws<ArgumentException>(() => ImageFilters.Combine(Photo(8, 8), Photo(8, 9), CombineMode.Xor));
    }

    [Fact]
    public void Contrast_stretch_and_equalisation_use_the_full_range()
    {
        var narrow = Pixels((100, 50, 7, 255), (101, 50, 8, 255), (104, 50, 9, 255));

        var stretched = ImageFilters.ContrastStretch(narrow);
        Assert.Equal(0, At(stretched, 0).R);
        Assert.Equal(63, At(stretched, 1).R);
        Assert.Equal(255, At(stretched, 2).R);
        Assert.Equal(50, At(stretched, 0).G); // flat channel left alone

        var equalised = ImageFilters.EqualizeHistogram(narrow);
        Assert.Equal(0, At(equalised, 0).B);
        Assert.Equal(255, At(equalised, 2).B);
        Assert.True(At(equalised, 1).B is > 0 and < 255);
    }

    [Fact]
    public void Random_colour_map_is_deterministic_and_separates_neighbouring_values()
    {
        var image = Pixels((100, 100, 100, 255), (101, 101, 101, 255), (100, 100, 100, 255));

        var mapped = ImageFilters.RandomColorMap(image, seed: 3);

        Assert.Equal(At(mapped, 0), At(mapped, 2));
        Assert.NotEqual(At(mapped, 0), At(mapped, 1));
        Assert.Equal(mapped.Pixels.ToArray(), ImageFilters.RandomColorMap(image, seed: 3).Pixels.ToArray());
    }

    [Fact]
    public void Drop_alpha_gray_bits_and_invert()
    {
        var image = Pixels((10, 20, 30, 0), (5, 5, 5, 255), (5, 5, 6, 128));
        var before = image.Pixels.ToArray();

        Assert.Equal((10, 20, 30, 255), At(ImageFilters.DropAlpha(image), 0));
        var gray = ImageFilters.GrayBits(image);
        Assert.Equal((255, 255, 255, 255), At(gray, 1));
        Assert.Equal((0, 0, 0, 255), At(gray, 2));
        Assert.Equal((245, 235, 225, 255), At(ImageFilters.Invert(image), 0));
        Assert.Equal(before, image.Pixels.ToArray()); // inputs are never modified
    }
}

public class ImageDiffTests
{
    [Fact]
    public void Identical_images_have_no_difference()
    {
        var image = Photo(20, 20);

        var diff = ImageDiff.Compare(image, image.Clone(), cancellationToken: Ct);

        Assert.Equal(0, diff.ChangedPixels);
        Assert.Null(diff.ChangedRegion);
        Assert.True(double.IsPositiveInfinity(diff.Rgb.Psnr));
        Assert.Empty(diff.ChangedLsbs);
    }

    [Fact]
    public void Reads_the_message_from_the_changed_samples()
    {
        // The embedder touched exactly the samples that carry the message: +1 for a 1 bit, -2 for a 0 bit.
        var original = Solid(64, 64, 100, 100, 100);
        var message = Utf8("diff me if you can\0");
        var modified = original.Clone();
        var pixels = modified.Pixels;
        var sample = 0;
        foreach (var b in message)
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                var index = ((sample / 3) * 4) + (sample % 3);
                pixels[index] = (byte)(((b >> bit) & 1) == 1 ? 101 : 98);
                sample++;
            }
        }

        var diff = ImageDiff.Compare(original, modified, cancellationToken: Ct);

        Assert.Equal(message, diff.ChangedLsbs);
        Assert.Equal(ContentKind.TerminatedText, diff.LsbContent.Kind);
        Assert.Equal("diff me if you can", diff.LsbContent.Preview);
        Assert.Equal(51, diff.ChangedPixels); // 152 samples over 3 channels
        Assert.Equal(new PixelRegion(0, 0, 51, 1), diff.ChangedRegion);
        Assert.Equal(message.Length * 8, diff.Rgb.ChangedSamples);
        Assert.Equal(2, diff.Rgb.MaxAbsoluteDelta);

        var heat = diff.Heatmap.Pixels;
        Assert.Equal(255, heat[0]);                    // changed: red
        Assert.Equal(heat[(60 * 4) + 1], heat[60 * 4]); // unchanged: grey
    }

    [Fact]
    public void Pixel_mode_takes_all_three_lsbs_of_changed_pixels()
    {
        var original = Solid(8, 1, 10, 11, 10);
        var modified = original.Clone();
        modified.Pixels[0] = 11;  // pixel 0: R changes, LSBs 1,1,0
        modified.Pixels[4 * 7 + 2] = 13; // pixel 7: B changes, LSBs 0,1,1

        var bits = ImageDiff.ExtractChangedLsbs(original, modified, ChangedPositionMode.Pixels, BitOrder.MsbFirst, cancellationToken: Ct);
        Assert.Empty(bits); // 6 bits, not yet a byte

        // Every pixel changed: the bit stream is R,G,B LSBs = 1,1,0 repeated, packed LSB first.
        var lsbFirst = ImageDiff.ExtractChangedLsbs(original, FlipAllRedLsbs(original), ChangedPositionMode.Pixels, BitOrder.LsbFirst, cancellationToken: Ct);
        Assert.Equal([219, 182, 109], lsbFirst);
    }

    [Fact]
    public void Rejects_images_of_different_size()
    {
        Assert.Throws<ArgumentException>(() => ImageDiff.Compare(Photo(4, 4), Photo(5, 4), cancellationToken: Ct));
    }

    private static PixelBuffer FlipAllRedLsbs(PixelBuffer image)
    {
        var copy = image.Clone();
        for (var i = 0; i < image.Width; i++)
        {
            copy.Pixels[i * 4] ^= 1;
        }

        return copy;
    }
}
