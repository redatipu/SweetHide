using System.Runtime.InteropServices;

using SkiaSharp;

using SweetHide.Core.Imaging;
using SweetHide.Core.Jpeg;

namespace SweetHide.Core.Tests.Jpeg;

/// <summary>Helpers that run images through SkiaSharp's JPEG codec (libjpeg-turbo), as a reference implementation.</summary>
internal static class JpegTestImages
{
    public static byte[] SkiaJpeg(PixelBuffer image, int quality, SKJpegEncoderDownsample downsample = SKJpegEncoderDownsample.Downsample420)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var handle = GCHandle.Alloc(image.Pixels.ToArray(), GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), info.RowBytes);
            using var data = pixmap.Encode(new SKJpegEncoderOptions(quality, downsample, SKJpegEncoderAlphaOption.Ignore))
                ?? throw new InvalidOperationException("Skia could not encode the JPEG.");
            return data.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }

    public static byte[] SkiaGrayJpeg(int width, int height, int quality, int seed)
    {
        var random = new Random(seed);
        var gray = new byte[width * height];
        for (var i = 0; i < gray.Length; i++)
        {
            gray[i] = (byte)Math.Clamp(((i % width) * 255 / width) + random.Next(-10, 11), 0, 255);
        }

        var info = new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
        var handle = GCHandle.Alloc(gray, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), info.RowBytes);
            using var data = pixmap.Encode(new SKJpegEncoderOptions(quality, SKJpegEncoderDownsample.Downsample444, SKJpegEncoderAlphaOption.Ignore))
                ?? throw new InvalidOperationException("Skia could not encode the JPEG.");
            return data.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Decodes with Skia (libjpeg-turbo, ISLOW IDCT) without colour management.</summary>
    public static PixelBuffer SkiaDecode(byte[] jpeg) => new SkiaImageCodec().Decode(new MemoryStream(jpeg), applyOrientation: false);

    public static byte[] Write(JpegCoefficients coefficients, JpegWriteOptions? options = null)
    {
        using var stream = new MemoryStream();
        JpegEncoder.Write(coefficients, stream, options);
        return stream.ToArray();
    }

    /// <summary>Busy, photo-like content with edges, so blocks have plenty of non-zero AC coefficients.</summary>
    public static PixelBuffer Textured(int width, int height, int seed = 1)
    {
        var random = new Random(seed);
        var data = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                var stripes = ((x / 5) + (y / 7)) % 2 == 0 ? 40 : -40;
                data[i] = Clamp(128 + stripes + (int)(50 * Math.Sin(x / 6.0)) + random.Next(-20, 21));
                data[i + 1] = Clamp((y * 255 / Math.Max(1, height - 1)) + random.Next(-20, 21));
                data[i + 2] = Clamp(100 - stripes + (int)(70 * Math.Cos((x + (2 * y)) / 11.0)) + random.Next(-20, 21));
                data[i + 3] = 255;
            }
        }

        return new PixelBuffer(width, height, data);
    }

    public static void AssertSameCoefficients(JpegCoefficients expected, JpegCoefficients actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Components.Count, actual.Components.Count);
        for (var c = 0; c < expected.Components.Count; c++)
        {
            var e = expected.Components[c];
            var a = actual.Components[c];
            Assert.Equal(e.Id, a.Id);
            Assert.Equal(e.HorizontalSampling, a.HorizontalSampling);
            Assert.Equal(e.VerticalSampling, a.VerticalSampling);
            Assert.Equal(e.QuantizationTableIndex, a.QuantizationTableIndex);
            Assert.Equal(expected.GetQuantizationTable(e.QuantizationTableIndex).ToArray(), actual.GetQuantizationTable(a.QuantizationTableIndex).ToArray());
            for (var by = 0; by < e.EncodedBlocksHigh; by++)
            {
                for (var bx = 0; bx < e.EncodedBlocksWide; bx++)
                {
                    Assert.True(e.Block(bx, by).SequenceEqual(a.Block(bx, by)), $"component {c} block ({bx},{by}) differs");
                }
            }
        }
    }

    /// <summary>Mean and max absolute difference over R, G and B.</summary>
    public static (double Mean, int Max) Difference(PixelBuffer a, PixelBuffer b)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        var pa = a.Pixels;
        var pb = b.Pixels;
        long sum = 0;
        var max = 0;
        long count = 0;
        for (var i = 0; i < pa.Length; i++)
        {
            if ((i & 3) == 3)
            {
                continue;
            }

            var d = Math.Abs(pa[i] - pb[i]);
            sum += d;
            max = Math.Max(max, d);
            count++;
        }

        return ((double)sum / count, max);
    }

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);
}
