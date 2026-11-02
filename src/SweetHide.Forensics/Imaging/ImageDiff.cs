using SweetHide.Core;
using SweetHide.Core.Imaging;
using SweetHide.Forensics.Detection;
using SweetHide.Forensics.Lsb;

namespace SweetHide.Forensics.Imaging;

/// <summary>Which bits <see cref="ImageDiff.ExtractChangedLsbs"/> collects.</summary>
public enum ChangedPositionMode
{
    /// <summary>The LSB of every R, G, B or A value that differs, in raster order.</summary>
    Samples,

    /// <summary>The R, G and B LSBs of every pixel where anything differs.</summary>
    Pixels,
}

public sealed record ImageDiffOptions
{
    public ChangedPositionMode Mode { get; init; } = ChangedPositionMode.Samples;

    public BitOrder BitOrder { get; init; } = BitOrder.MsbFirst;

    public int MaxExtractBytes { get; init; } = 16 * 1024 * 1024;

    public static ImageDiffOptions Default { get; } = new();
}

/// <summary>Bounding box of the changed pixels.</summary>
public sealed record PixelRegion(int X, int Y, int Width, int Height);

/// <param name="ChangedPixels">Pixels where any of R, G, B or A differs.</param>
/// <param name="TotalPixels">Pixels compared.</param>
/// <param name="Rgb">Changed R/G/B samples, largest change and PSNR (from <see cref="ImageMetrics"/>).</param>
/// <param name="ChangedRegion">Bounding box of the changes, or null when the images are identical.</param>
/// <param name="Heatmap">The modified image dimmed to a quarter, with changed pixels in red (small change) to yellow (large).</param>
/// <param name="ChangedLsbs">LSBs of the modified image at the changed positions, packed into bytes.</param>
/// <param name="LsbContent">What <see cref="ChangedLsbs"/> looks like (text, file, ...).</param>
public sealed record ImageDiffResult(
    long ChangedPixels,
    long TotalPixels,
    ImageDifference Rgb,
    PixelRegion? ChangedRegion,
    PixelBuffer Heatmap,
    byte[] ChangedLsbs,
    ContentAnalysis LsbContent);

/// <summary>Compares an original with a modified copy: how much changed, where, and what the changed LSBs spell.</summary>
public static class ImageDiff
{
    public static ImageDiffResult Compare(PixelBuffer original, PixelBuffer modified, ImageDiffOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(modified);
        ImageFilters.RequireSameSize(original, modified);
        options ??= ImageDiffOptions.Default;

        ReadOnlySpan<byte> a = original.Pixels;
        ReadOnlySpan<byte> b = modified.Pixels;
        var heat = new byte[b.Length];
        long changedPixels = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < original.Height; y++)
        {
            if ((y & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            for (var x = 0; x < original.Width; x++)
            {
                var i = ((y * original.Width) + x) * 4;
                var delta = 0;
                for (var c = 0; c < 4; c++)
                {
                    delta = Math.Max(delta, Math.Abs(a[i + c] - b[i + c]));
                }

                if (delta == 0)
                {
                    var luma = ((b[i] * 299) + (b[i + 1] * 587) + (b[i + 2] * 114)) / 4000;
                    heat[i] = heat[i + 1] = heat[i + 2] = (byte)luma;
                }
                else
                {
                    changedPixels++;
                    (minX, minY, maxX, maxY) = (Math.Min(minX, x), Math.Min(minY, y), Math.Max(maxX, x), Math.Max(maxY, y));
                    heat[i] = 255;
                    heat[i + 1] = (byte)Math.Min(255, delta * 32);
                    heat[i + 2] = 0;
                }

                heat[i + 3] = 255;
            }
        }

        var lsbs = ExtractChangedLsbs(original, modified, options.Mode, options.BitOrder, options.MaxExtractBytes, cancellationToken);
        var region = maxX < 0 ? null : new PixelRegion(minX, minY, maxX - minX + 1, maxY - minY + 1);
        return new ImageDiffResult(
            changedPixels,
            original.PixelCount,
            ImageMetrics.Compare(original, modified),
            region,
            new PixelBuffer(original.Width, original.Height, heat),
            lsbs,
            ContentAnalyzer.Analyze(lsbs.AsSpan(0, Math.Min(lsbs.Length, 4096)), lsbs.Length));
    }

    /// <summary>The least significant bits of <paramref name="modified"/> where it differs from <paramref name="original"/>.</summary>
    public static byte[] ExtractChangedLsbs(PixelBuffer original, PixelBuffer modified, ChangedPositionMode mode = ChangedPositionMode.Samples, BitOrder bitOrder = BitOrder.MsbFirst, int maxBytes = 16 * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(modified);
        ImageFilters.RequireSameSize(original, modified);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);

        ReadOnlySpan<byte> a = original.Pixels;
        ReadOnlySpan<byte> b = modified.Pixels;
        using var output = new MemoryStream();
        var accumulator = 0;
        var bits = 0;
        for (var i = 0; i < a.Length && output.Length < maxBytes; i += 4)
        {
            if ((i & 0x3FFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var pixelChanged = a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2] || a[i + 3] != b[i + 3];
            if (!pixelChanged)
            {
                continue;
            }

            for (var c = 0; c < (mode == ChangedPositionMode.Samples ? 4 : 3); c++)
            {
                if (mode == ChangedPositionMode.Samples && a[i + c] == b[i + c])
                {
                    continue;
                }

                var bit = b[i + c] & 1;
                accumulator = bitOrder == BitOrder.MsbFirst ? (accumulator << 1) | bit : accumulator | (bit << bits);
                if (++bits == 8)
                {
                    output.WriteByte((byte)accumulator);
                    accumulator = 0;
                    bits = 0;
                }
            }
        }

        return output.Length > maxBytes ? output.ToArray()[..maxBytes] : output.ToArray();
    }
}
