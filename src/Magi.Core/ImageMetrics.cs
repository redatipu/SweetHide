using Magi.Core.Imaging;

namespace Magi.Core;

/// <param name="ChangedSamples">R/G/B values that differ.</param>
/// <param name="TotalSamples">R/G/B values compared.</param>
/// <param name="MaxAbsoluteDelta">Largest change to any single value.</param>
/// <param name="Psnr">Peak signal-to-noise ratio in dB over R/G/B; infinity when identical. Above ~50 dB is invisible.</param>
public sealed record ImageDifference(long ChangedSamples, long TotalSamples, int MaxAbsoluteDelta, double Psnr)
{
    public double ChangedRatio => TotalSamples == 0 ? 0 : (double)ChangedSamples / TotalSamples;
}

public static class ImageMetrics
{
    public static ImageDifference Compare(PixelBuffer original, PixelBuffer modified)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(modified);
        if (original.Width != modified.Width || original.Height != modified.Height)
        {
            throw new ArgumentException("Images must have the same dimensions.", nameof(modified));
        }

        var a = original.Data;
        var b = modified.Data;
        long changed = 0;
        long total = 0;
        double squaredError = 0;
        var maxDelta = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if ((i & 3) == 3)
            {
                continue; // alpha
            }

            total++;
            var delta = a[i] - b[i];
            if (delta != 0)
            {
                changed++;
                squaredError += delta * delta;
                maxDelta = Math.Max(maxDelta, Math.Abs(delta));
            }
        }

        var psnr = squaredError == 0
            ? double.PositiveInfinity
            : 10 * Math.Log10(255.0 * 255.0 / (squaredError / total));
        return new ImageDifference(changed, total, maxDelta, psnr);
    }
}
