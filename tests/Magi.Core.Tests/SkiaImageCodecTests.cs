using Magi.Core.Imaging;

using SkiaSharp;

namespace Magi.Core.Tests;

public class SkiaImageCodecTests
{
    private readonly SkiaImageCodec _codec = new();

    [Fact]
    public void Png_roundtrip_is_bit_exact_for_opaque_images()
    {
        var image = TestImages.Photo(97, 61, seed: 11);
        Assert.Equal(image.Pixels.ToArray(), Roundtrip(image).Pixels.ToArray());
    }

    [Fact]
    public void Png_roundtrip_is_bit_exact_with_partial_transparency()
    {
        // Every combination of colour and alpha, including colour under alpha 0 and low alpha values,
        // which premultiplication would destroy.
        var random = new Random(4);
        var data = new byte[128 * 128 * 4];
        random.NextBytes(data);
        var image = new PixelBuffer(128, 128, data);

        var back = Roundtrip(image).Pixels.ToArray();
        for (var i = 0; i < data.Length; i += 4)
        {
            if (data[i + 3] == 0)
            {
                Assert.Equal(0, back[i + 3]); // colour under alpha 0 is irrelevant (never used as a slot)
                continue;
            }

            Assert.Equal(data.AsSpan(i, 4).ToArray(), back.AsSpan(i, 4).ToArray());
        }
    }

    [Fact]
    public void Decodes_jpeg_covers()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(64, 48, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(new SKColor(200, 100, 50));
        using var jpeg = new MemoryStream();
        bitmap.Encode(jpeg, SKEncodedImageFormat.Jpeg, 90);
        jpeg.Position = 0;

        var image = _codec.Decode(jpeg, applyOrientation: true);

        Assert.Equal(64, image.Width);
        Assert.Equal(48, image.Height);
        Assert.True(image.IsOpaque);
    }

    [Fact]
    public void Rejects_garbage_and_oversized_images()
    {
        Assert.Throws<InvalidImageException>(() => _codec.Decode(new MemoryStream([1, 2, 3, 4, 5]), true));
        Assert.Throws<InvalidImageException>(() => _codec.Decode(new MemoryStream(), true));

        using var png = new MemoryStream();
        _codec.EncodePng(TestImages.Photo(100, 100), png);
        png.Position = 0;
        Assert.Throws<InvalidImageException>(() => new SkiaImageCodec(maxPixels: 9_999).Decode(png, true));
    }

    [Fact]
    public void Writes_no_metadata_chunks()
    {
        using var png = new MemoryStream();
        _codec.EncodePng(TestImages.Photo(20, 20), png);
        var chunks = PngChunkTypes(png.ToArray());

        Assert.Equal("IHDR", chunks[0]);
        Assert.Equal("IEND", chunks[^1]);
        string[] identifying = ["tEXt", "zTXt", "iTXt", "eXIf", "iCCP", "tIME", "pHYs"];
        Assert.DoesNotContain(chunks, identifying.Contains);
    }

    [Theory]
    [InlineData(SKEncodedOrigin.TopLeft, 3, 2, new byte[] { 1, 2, 3, 4, 5, 6 })]
    [InlineData(SKEncodedOrigin.TopRight, 3, 2, new byte[] { 3, 2, 1, 6, 5, 4 })]
    [InlineData(SKEncodedOrigin.BottomRight, 3, 2, new byte[] { 6, 5, 4, 3, 2, 1 })]
    [InlineData(SKEncodedOrigin.BottomLeft, 3, 2, new byte[] { 4, 5, 6, 1, 2, 3 })]
    [InlineData(SKEncodedOrigin.LeftTop, 2, 3, new byte[] { 1, 4, 2, 5, 3, 6 })]
    [InlineData(SKEncodedOrigin.RightTop, 2, 3, new byte[] { 4, 1, 5, 2, 6, 3 })]
    [InlineData(SKEncodedOrigin.RightBottom, 2, 3, new byte[] { 6, 3, 5, 2, 4, 1 })]
    [InlineData(SKEncodedOrigin.LeftBottom, 2, 3, new byte[] { 3, 6, 2, 5, 1, 4 })]
    public void Applies_exif_orientation(SKEncodedOrigin origin, int width, int height, byte[] expected)
    {
        // Source 3x2:  1 2 3
        //              4 5 6
        var source = new PixelBuffer(3, 2, [.. new byte[] { 1, 2, 3, 4, 5, 6 }.SelectMany(v => new byte[] { v, 0, 0, 255 })]);

        var result = Orientation.Apply(source, origin);

        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
        var reds = new byte[width * height];
        for (var i = 0; i < reds.Length; i++)
        {
            reds[i] = result.Pixels[i * 4];
        }

        Assert.Equal(expected, reds);
    }

    private PixelBuffer Roundtrip(PixelBuffer image)
    {
        using var png = new MemoryStream();
        _codec.EncodePng(image, png);
        png.Position = 0;
        return _codec.Decode(png, applyOrientation: false);
    }

    private static List<string> PngChunkTypes(byte[] png)
    {
        var types = new List<string>();
        var offset = 8;
        while (offset + 8 <= png.Length)
        {
            var length = (png[offset] << 24) | (png[offset + 1] << 16) | (png[offset + 2] << 8) | png[offset + 3];
            types.Add(System.Text.Encoding.ASCII.GetString(png, offset + 4, 4));
            offset += 12 + length;
        }

        return types;
    }
}
