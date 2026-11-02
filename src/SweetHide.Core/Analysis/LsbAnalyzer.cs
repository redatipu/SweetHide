using SweetHide.Core.Imaging;

namespace SweetHide.Core.Analysis;

/// <param name="Channel">"R", "G" or "B".</param>
/// <param name="OnesRatio">Share of values whose least significant bit is 1.</param>
/// <param name="ChiSquare">Westfeld–Pfitzmann chi-square statistic over pairs of values (2k, 2k+1).</param>
/// <param name="DegreesOfFreedom">Number of value pairs used minus one.</param>
/// <param name="EmbeddingProbability">
/// P-value of the test. Close to 1 means the pairs are suspiciously equal, as LSB <em>replacement</em> makes them.
/// </param>
public sealed record ChannelLsbStats(string Channel, double OnesRatio, double ChiSquare, int DegreesOfFreedom, double EmbeddingProbability);

public sealed record LsbReport(int Width, int Height, IReadOnlyList<ChannelLsbStats> Channels);

/// <summary>
/// Simple steganalysis to compare images with friends: LSB statistics, the chi-square attack and an LSB-plane view.
/// </summary>
/// <remarks>
/// The chi-square attack reliably flags heavy LSB <em>replacement</em>. SweetHide itself uses LSB <em>matching</em>, which
/// this test is not designed to catch, which is the point of having it here to experiment with.
/// </remarks>
public static class LsbAnalyzer
{
    private static readonly string[] ChannelNames = ["R", "G", "B"];

    public static LsbReport Analyze(PixelBuffer image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var data = image.Data;
        var stats = new List<ChannelLsbStats>(3);
        for (var c = 0; c < 3; c++)
        {
            var histogram = new long[256];
            long count = 0;
            for (var i = 0; i < data.Length; i += 4)
            {
                if (data[i + 3] == 0)
                {
                    continue;
                }

                histogram[data[i + c]]++;
                count++;
            }

            long ones = 0;
            double chi = 0;
            var pairs = 0;
            for (var k = 0; k < 128; k++)
            {
                var even = histogram[2 * k];
                var odd = histogram[(2 * k) + 1];
                ones += odd;
                var expected = (even + odd) / 2.0;
                if (expected < 5)
                {
                    continue; // too few samples for the chi-square approximation
                }

                pairs++;
                var diff = even - expected;
                chi += diff * diff / expected;
            }

            var dof = Math.Max(1, pairs - 1);
            var probability = pairs < 2 ? double.NaN : SpecialFunctions.UpperRegularizedGamma(dof / 2.0, chi / 2.0);
            stats.Add(new ChannelLsbStats(ChannelNames[c], count == 0 ? 0 : (double)ones / count, chi, dof, probability));
        }

        return new LsbReport(image.Width, image.Height, stats);
    }

    /// <summary>Visualises one bit plane: each channel becomes 0 or 255 according to that bit.</summary>
    public static PixelBuffer RenderBitPlane(PixelBuffer image, int bit = 0)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegative(bit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bit, 7);

        var src = image.Data;
        var dst = new byte[src.Length];
        for (var i = 0; i < src.Length; i += 4)
        {
            dst[i] = (byte)(((src[i] >> bit) & 1) * 255);
            dst[i + 1] = (byte)(((src[i + 1] >> bit) & 1) * 255);
            dst[i + 2] = (byte)(((src[i + 2] >> bit) & 1) * 255);
            dst[i + 3] = 255;
        }

        return new PixelBuffer(image.Width, image.Height, dst);
    }
}
