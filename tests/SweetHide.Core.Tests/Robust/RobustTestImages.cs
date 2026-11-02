using System.Runtime.InteropServices;

using SkiaSharp;

using SweetHide.Core.Imaging;

namespace SweetHide.Core.Tests.Robust;

/// <summary>Photo-like covers and a simulated chat app (SkiaSharp's libjpeg-turbo) for robust-mode tests.</summary>
internal static class RobustTestImages
{
    /// <summary>
    /// A synthetic "photo": a smooth sky with soft clouds, textured ground with sensor-like noise, a white building
    /// with dark windows (clipped highlights, hard edges), a deep shadow and a saturated red sign.
    /// </summary>
    public static PixelBuffer Photo(int width, int height, int seed = 7)
    {
        var random = new Random(seed);
        var coarse = Grid(random, 24);
        var medium = Grid(random, 96);
        var fine = Grid(random, 400);
        var data = new byte[width * height * 4];
        Parallel.For(0, height, y =>
        {
            var noise = new Random((seed * 100_003) + y);
            for (var x = 0; x < width; x++)
            {
                double u = (double)x / width, v = (double)y / height;
                var horizon = 0.42 + (0.06 * Math.Sin(u * 9)) + (0.03 * Sample(coarse, 24, u, v));
                double r, g, b;
                if (v < horizon)
                {
                    var t = v / horizon;
                    var cloud = Math.Max(0, Sample(coarse, 24, u * 1.7, v * 2.3)) * 60;
                    r = 95 + (70 * t) + cloud + Gauss(noise);
                    g = 150 + (55 * t) + cloud + Gauss(noise);
                    b = 225 + (20 * t) + (cloud * 0.5) + Gauss(noise);
                }
                else
                {
                    var texture = (Sample(medium, 96, u, v) * 35) + (Sample(fine, 400, u, v) * 25);
                    r = 95 + texture + (Gauss(noise) * 8);
                    g = 120 + texture + (Gauss(noise) * 8);
                    b = 60 + (texture * 0.6) + (Gauss(noise) * 8);
                }

                if (u is > 0.55 and < 0.8 && v is > 0.25 and < 0.75)
                {
                    var window = (int)(u * 160) % 3 == 0 && (int)(v * 90) % 3 == 0;
                    r = g = b = window ? 40 : 252 + (Gauss(noise) * 3);
                }

                if (u is > 0.05 and < 0.3 && v > 0.8)
                {
                    r *= 0.12;
                    g *= 0.12;
                    b *= 0.12;
                }

                if (((u - 0.35) * (u - 0.35)) + ((v - 0.6) * (v - 0.6)) < 0.004)
                {
                    (r, g, b) = (220, 25, 30);
                }

                var i = ((y * width) + x) * 4;
                data[i] = Clamp(r);
                data[i + 1] = Clamp(g);
                data[i + 2] = Clamp(b);
                data[i + 3] = 255;
            }
        });

        return new PixelBuffer(width, height, data);
    }

    /// <summary>What a chat app does: encode as JPEG with libjpeg-turbo, then the receiver decodes it.</summary>
    public static PixelBuffer Recompress(PixelBuffer image, int quality, SKJpegEncoderDownsample downsample = SKJpegEncoderDownsample.Downsample420) =>
        Jpeg.JpegTestImages.SkiaDecode(Jpeg.JpegTestImages.SkiaJpeg(image, quality, downsample));

    public static PixelBuffer Resize(PixelBuffer image, double scale)
    {
        var width = (int)Math.Round(image.Width * scale);
        var height = (int)Math.Round(image.Height * scale);
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info);
        Marshal.Copy(image.Pixels.ToArray(), 0, bitmap.GetPixels(), image.Pixels.Length);
        using var scaled = bitmap.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque), new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? throw new InvalidOperationException("Resize failed.");
        var bytes = new byte[width * height * 4];
        Marshal.Copy(scaled.GetPixels(), bytes, 0, bytes.Length);
        return new PixelBuffer(width, height, bytes);
    }

    public static byte[] Key(int seed)
    {
        var key = new byte[32];
        new Random(seed).NextBytes(key);
        return key;
    }

    public static byte[] Payload(int length, int seed = 3)
    {
        var payload = new byte[length];
        new Random(seed).NextBytes(payload);
        return payload;
    }

    private static double[] Grid(Random random, int n)
    {
        var grid = new double[(n + 2) * (n + 2)];
        for (var i = 0; i < grid.Length; i++)
        {
            grid[i] = (random.NextDouble() * 2) - 1;
        }

        return grid;
    }

    private static double Sample(double[] grid, int n, double u, double v)
    {
        u = u % 1.0 * n;
        v = v % 1.0 * n;
        int x0 = (int)u, y0 = (int)v;
        double fx = Smooth(u - x0), fy = Smooth(v - y0);
        var stride = n + 2;
        var top = grid[(y0 * stride) + x0] + ((grid[(y0 * stride) + x0 + 1] - grid[(y0 * stride) + x0]) * fx);
        var bottom = grid[((y0 + 1) * stride) + x0] + ((grid[((y0 + 1) * stride) + x0 + 1] - grid[((y0 + 1) * stride) + x0]) * fx);
        return top + ((bottom - top) * fy);
    }

    private static double Smooth(double t) => t * t * (3 - (2 * t));

    // Cheap bell-shaped noise with unit variance (sum of three uniforms), fast enough for 12 MP test images.
    private static double Gauss(Random random) => (random.NextDouble() + random.NextDouble() + random.NextDouble() - 1.5) * 2;

    private static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);
}
