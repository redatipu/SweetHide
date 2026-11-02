using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;

namespace SweetHide.Core.Tests;

public class LosslessFormatTests
{
    private readonly SkiaImageCodec _codec = new();

    public static TheoryData<LosslessFormat> Formats() => [.. LosslessFormats.All];

    [Theory]
    [MemberData(nameof(Formats))]
    public void Opaque_images_round_trip_bit_exactly(LosslessFormat format)
    {
        var image = TestImages.Photo(73, 41, seed: 3);
        Assert.Equal(image.Pixels.ToArray(), Roundtrip(image, format).Pixels.ToArray());
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Visible_pixels_with_alpha_round_trip_bit_exactly(LosslessFormat format)
    {
        var random = new Random(8);
        var data = new byte[64 * 48 * 4];
        random.NextBytes(data);
        var back = Roundtrip(new PixelBuffer(64, 48, data), format).Pixels.ToArray();
        for (var i = 0; i < data.Length; i += 4)
        {
            Assert.Equal(data[i + 3], back[i + 3]);
            if (data[i + 3] != 0) // colour under fully transparent pixels is never used for hiding
            {
                Assert.Equal(data.AsSpan(i, 3).ToArray(), back.AsSpan(i, 3).ToArray());
            }
        }
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Hidden_data_survives_every_lossless_format(LosslessFormat format)
    {
        var engine = TestImages.Engine();
        var stego = engine.Embed(TestImages.Photo(120, 90, withTransparency: true), Payload.FromText("ผ่านทุกฟอร์แมต"), "pw");
        Assert.Equal("ผ่านทุกฟอร์แมต", engine.Extract(Roundtrip(stego, format), "pw").Entries[0].GetText());
    }

    [Theory]
    [InlineData("a.png", LosslessFormat.Png)]
    [InlineData("a.WebP", LosslessFormat.WebP)]
    [InlineData("a.bmp", LosslessFormat.Bmp)]
    [InlineData("a.tif", LosslessFormat.Tiff)]
    [InlineData("a.tiff", LosslessFormat.Tiff)]
    [InlineData("a.jpg", null)]
    [InlineData("a", null)]
    public void Format_from_extension(string path, LosslessFormat? expected) => Assert.Equal(expected, LosslessFormats.FromPath(path));

    [Fact]
    public void SaveImage_refuses_lossy_extensions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sweethide-{Guid.NewGuid():N}.jpg");
        Assert.Throws<ArgumentException>(() => _codec.SaveImage(TestImages.Photo(8, 8), path, overwrite: true));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Damaged_tiff_is_an_image_error_not_a_crash()
    {
        using var stream = new MemoryStream();
        TiffCodec.Write(TestImages.Photo(30, 20), stream);
        var tiff = stream.ToArray();
        for (var length = 4; length < tiff.Length; length += 37)
        {
            Assert.Throws<InvalidImageException>(() => _codec.Decode(new MemoryStream(tiff[..length]), false));
        }

        var huge = (byte[])tiff.Clone();
        BitConverter.GetBytes(100_000).CopyTo(huge, 8 + 2 + 8); // ImageWidth value
        Assert.Throws<InvalidImageException>(() => new SkiaImageCodec(maxPixels: 1_000_000).Decode(new MemoryStream(huge), false));
    }

    private PixelBuffer Roundtrip(PixelBuffer image, LosslessFormat format)
    {
        using var stream = new MemoryStream();
        LosslessFormats.Encode(_codec, image, format, stream);
        stream.Position = 0;
        return _codec.Decode(stream, applyOrientation: false);
    }
}
