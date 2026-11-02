using SweetHide.Core.Analysis;
using SweetHide.Core.Imaging;

namespace SweetHide.Forensics.Imaging;

public enum ColorChannel
{
    Red = 0,
    Green = 1,
    Blue = 2,
    Alpha = 3,
}

public enum CombineMode
{
    /// <summary>Bitwise XOR per channel: identical pixels turn black, differences light up.</summary>
    Xor,

    /// <summary>(a − b) mod 256 per channel, as StegSolve's SUB.</summary>
    Subtract,

    /// <summary>|a − b| per channel.</summary>
    AbsoluteDifference,

    /// <summary>(a + b) mod 256 per channel.</summary>
    Add,

    And,

    Or,
}

/// <summary>
/// StegSolve-style views. Every filter returns a new, fully opaque image (so nothing stays hidden under
/// transparency) and leaves the input untouched.
/// </summary>
public static class ImageFilters
{
    /// <summary>One channel's bit as black (0) and white (1).</summary>
    public static PixelBuffer BitPlane(PixelBuffer image, ColorChannel channel, int bit)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateBit(bit);
        var c = Channel(channel);
        return Map(image, (src, dst, i) => Gray(dst, i, ((src[i + c] >> bit) & 1) * 255));
    }

    /// <summary>R, G and B each show their own bit (0 or 255): coloured noise where data is hidden.</summary>
    public static PixelBuffer CombinedBitPlane(PixelBuffer image, int bit) => LsbAnalyzer.RenderBitPlane(image, bit);

    /// <summary>One channel shown as greyscale.</summary>
    public static PixelBuffer ChannelGray(PixelBuffer image, ColorChannel channel)
    {
        ArgumentNullException.ThrowIfNull(image);
        var c = Channel(channel);
        return Map(image, (src, dst, i) => Gray(dst, i, src[i + c]));
    }

    /// <summary>Combines the R, G and B channels of two images of the same size.</summary>
    /// <exception cref="ArgumentException">The images differ in size.</exception>
    public static PixelBuffer Combine(PixelBuffer a, PixelBuffer b, CombineMode mode)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        RequireSameSize(a, b);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }

        ReadOnlySpan<byte> left = a.Pixels;
        ReadOnlySpan<byte> right = b.Pixels;
        var dst = new byte[left.Length];
        for (var i = 0; i < left.Length; i++)
        {
            if ((i & 3) == 3)
            {
                dst[i] = 255;
                continue;
            }

            int x = left[i], y = right[i];
            dst[i] = (byte)(mode switch
            {
                CombineMode.Xor => x ^ y,
                CombineMode.Subtract => x - y,
                CombineMode.AbsoluteDifference => Math.Abs(x - y),
                CombineMode.Add => x + y,
                CombineMode.And => x & y,
                _ => x | y,
            });
        }

        return new PixelBuffer(a.Width, a.Height, dst);
    }

    /// <summary>Stretches each channel's used range to 0–255, so faint differences become visible.</summary>
    public static PixelBuffer ContrastStretch(PixelBuffer image)
    {
        ArgumentNullException.ThrowIfNull(image);
        ReadOnlySpan<byte> src = image.Pixels;
        Span<int> min = [255, 255, 255];
        Span<int> max = [0, 0, 0];
        for (var i = 0; i < src.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                min[c] = Math.Min(min[c], src[i + c]);
                max[c] = Math.Max(max[c], src[i + c]);
            }
        }

        var tables = new byte[3][];
        for (var c = 0; c < 3; c++)
        {
            tables[c] = new byte[256];
            var range = max[c] - min[c];
            for (var v = 0; v < 256; v++)
            {
                tables[c][v] = range == 0 ? (byte)v : (byte)Math.Clamp((v - min[c]) * 255 / range, 0, 255);
            }
        }

        return ApplyTables(image, tables);
    }

    /// <summary>Per-channel histogram equalisation.</summary>
    public static PixelBuffer EqualizeHistogram(PixelBuffer image)
    {
        ArgumentNullException.ThrowIfNull(image);
        ReadOnlySpan<byte> src = image.Pixels;
        var tables = new byte[3][];
        for (var c = 0; c < 3; c++)
        {
            var histogram = new long[256];
            for (var i = c; i < src.Length; i += 4)
            {
                histogram[src[i]]++;
            }

            var cdfMin = histogram.First(h => h > 0);
            long cumulative = 0;
            tables[c] = new byte[256];
            var denominator = image.PixelCount - cdfMin;
            for (var v = 0; v < 256; v++)
            {
                cumulative += histogram[v];
                tables[c][v] = denominator <= 0 ? (byte)v : (byte)Math.Clamp((cumulative - cdfMin) * 255 / denominator, 0, 255);
            }
        }

        return ApplyTables(image, tables);
    }

    /// <summary>
    /// Sends every channel value through a random lookup table, so values that differ by one get unrelated
    /// colours: flat-looking areas that are not quite flat suddenly show their structure.
    /// </summary>
    public static PixelBuffer RandomColorMap(PixelBuffer image, int seed = 0)
    {
        ArgumentNullException.ThrowIfNull(image);
        var random = new Random(seed);
        var tables = new byte[3][];
        for (var c = 0; c < 3; c++)
        {
            tables[c] = new byte[256];
            random.NextBytes(tables[c]);
        }

        return ApplyTables(image, tables);
    }

    /// <summary>Makes every pixel opaque, revealing colour stored under fully transparent pixels.</summary>
    public static PixelBuffer DropAlpha(PixelBuffer image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Map(image, static (src, dst, i) =>
        {
            dst[i] = src[i];
            dst[i + 1] = src[i + 1];
            dst[i + 2] = src[i + 2];
            dst[i + 3] = 255;
        });
    }

    /// <summary>StegSolve "gray bits": white where a pixel is exactly grey (R = G = B), black elsewhere.</summary>
    public static PixelBuffer GrayBits(PixelBuffer image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Map(image, static (src, dst, i) => Gray(dst, i, src[i] == src[i + 1] && src[i] == src[i + 2] ? 255 : 0));
    }

    /// <summary>255 − value on R, G and B.</summary>
    public static PixelBuffer Invert(PixelBuffer image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Map(image, static (src, dst, i) =>
        {
            dst[i] = (byte)(255 - src[i]);
            dst[i + 1] = (byte)(255 - src[i + 1]);
            dst[i + 2] = (byte)(255 - src[i + 2]);
            dst[i + 3] = 255;
        });
    }

    internal static void RequireSameSize(PixelBuffer a, PixelBuffer b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            throw new ArgumentException($"Images must have the same size ({a.Width}x{a.Height} vs {b.Width}x{b.Height}).", nameof(b));
        }
    }

    private static PixelBuffer ApplyTables(PixelBuffer image, byte[][] tables) =>
        Map(image, (src, dst, i) =>
        {
            dst[i] = tables[0][src[i]];
            dst[i + 1] = tables[1][src[i + 1]];
            dst[i + 2] = tables[2][src[i + 2]];
            dst[i + 3] = 255;
        });

    private static PixelBuffer Map(PixelBuffer image, PixelAction action)
    {
        ReadOnlySpan<byte> src = image.Pixels;
        var dst = new byte[src.Length];
        for (var i = 0; i < src.Length; i += 4)
        {
            action(src, dst, i);
        }

        return new PixelBuffer(image.Width, image.Height, dst);
    }

    private static void Gray(byte[] dst, int i, int value)
    {
        dst[i] = dst[i + 1] = dst[i + 2] = (byte)value;
        dst[i + 3] = 255;
    }

    private static int Channel(ColorChannel channel) =>
        Enum.IsDefined(channel) ? (int)channel : throw new ArgumentOutOfRangeException(nameof(channel), channel, null);

    private static void ValidateBit(int bit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bit, 7);
    }

    private delegate void PixelAction(ReadOnlySpan<byte> source, byte[] destination, int index);
}
