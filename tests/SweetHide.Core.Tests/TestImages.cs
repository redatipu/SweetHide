using SweetHide.Core.Crypto;
using SweetHide.Core.Imaging;

namespace SweetHide.Core.Tests;

internal static class TestImages
{
    /// <summary>Cheap Argon2 settings so tests run fast. Never use outside tests.</summary>
    public static readonly KdfParameters FastKdf = new(MemoryKiB: 64, Iterations: 1, Parallelism: 1);

    public static StegoEngine Engine() => new(FastKdf);

    /// <summary>A photo-like image: smooth gradients plus mild noise, so LSBs look natural.</summary>
    public static PixelBuffer Photo(int width, int height, int seed = 1, bool withTransparency = false)
    {
        var random = new Random(seed);
        var data = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                data[i] = Clamp((x * 255 / Math.Max(1, width - 1)) + random.Next(-6, 7));
                data[i + 1] = Clamp((y * 255 / Math.Max(1, height - 1)) + random.Next(-6, 7));
                data[i + 2] = Clamp(128 + (int)(60 * Math.Sin((x + y) / 9.0)) + random.Next(-6, 7));
                data[i + 3] = withTransparency && (x + y) % 5 == 0 ? (byte)0
                    : withTransparency && (x + y) % 7 == 0 ? (byte)random.Next(1, 255)
                    : (byte)255;
            }
        }

        return new PixelBuffer(width, height, data);
    }

    public static PixelBuffer Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var data = new byte[width * height * 4];
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = r;
            data[i + 1] = g;
            data[i + 2] = b;
            data[i + 3] = a;
        }

        return new PixelBuffer(width, height, data);
    }

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);
}
