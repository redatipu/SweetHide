using SweetHide.Core.Analysis;
using SweetHide.Core.Imaging;
using SweetHide.Forensics.Steganalysis;

using static SweetHide.Forensics.Tests.TestData;

namespace SweetHide.Forensics.Tests;

public class SteganalysisTests
{
    [Fact]
    public void Rs_and_spa_track_the_embedding_rate()
    {
        var cover = Photo(256, 256, seed: 4);
        var rates = new[] { 0.0, 0.25, 0.5, 0.75 };
        var rs = rates.Select(r => RsAnalysis.Analyze(ReplaceLsbs(cover, r, 9), Ct).Average(c => c.EstimatedRate)).ToArray();
        var spa = rates.Select(r => SamplePairAnalysis.Analyze(ReplaceLsbs(cover, r, 9), Ct).Average(c => c.EstimatedRate)).ToArray();

        for (var i = 0; i < rates.Length; i++)
        {
            // Synthetic covers carry some bias, so allow a generous band around the truth.
            Assert.InRange(rs[i], rates[i] - 0.1, rates[i] + 0.2);
            Assert.InRange(spa[i], rates[i] - 0.1, rates[i] + 0.2);
            if (i > 0)
            {
                Assert.True(rs[i] > rs[i - 1], $"RS not increasing: {string.Join(", ", rs)}");
                Assert.True(spa[i] > spa[i - 1], $"SPA not increasing: {string.Join(", ", spa)}");
            }
        }
    }

    [Fact]
    public void Spa_still_reports_full_embedding()
    {
        var full = ReplaceLsbs(Photo(256, 256, seed: 5), 1.0, 3);

        Assert.All(SamplePairAnalysis.Analyze(full, Ct), c => Assert.True(c.EstimatedRate > 0.8, $"{c.Channel}: {c.EstimatedRate}"));
    }

    [Fact]
    public void Report_combines_core_chi_square_with_rs_and_spa()
    {
        var image = ReplaceLsbs(Photo(128, 128, seed: 6), 0.5, 1);

        var report = Steganalyzer.Analyze(image, Ct);
        var core = LsbAnalyzer.Analyze(image);

        Assert.Equal(["R", "G", "B"], report.Channels.Select(c => c.Channel));
        Assert.Equal(core.Channels.Select(c => c.EmbeddingProbability), report.Channels.Select(c => c.ChiSquareProbability));
        Assert.Equal(core.Channels.Select(c => c.OnesRatio), report.Channels.Select(c => c.OnesRatio));
        Assert.InRange(report.MaxEstimatedRate, 0.3, 0.8);
    }

    [Fact]
    public void Degenerate_images_do_not_throw()
    {
        foreach (var image in new[] { Solid(16, 16, 7, 7, 7), Solid(16, 16, 7, 7, 7, a: 0), Solid(1, 1, 0, 0, 0), Solid(3, 50, 255, 0, 255) })
        {
            var report = Steganalyzer.Analyze(image, Ct);
            Assert.Equal(3, report.Channels.Count);
            Assert.All(report.Channels, c => Assert.True(double.IsNaN(c.RsRate) || c.RsRate is >= 0 and <= 1));
            Assert.All(report.Channels, c => Assert.True(double.IsNaN(c.SpaRate) || c.SpaRate is >= 0 and <= 1));
            Assert.NotNull(LsbRandomness.Map(image, cancellationToken: Ct));
        }
    }

    [Fact]
    public void Randomness_map_finds_random_lsbs_in_a_smooth_area()
    {
        // Smooth gradient cover; random LSBs written into the left half only.
        var cover = new PixelBuffer(128, 64, new byte[128 * 64 * 4]);
        var pixels = cover.Pixels;
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 128; x++)
            {
                var i = ((y * 128) + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)(x * 2);
                pixels[i + 3] = 255;
            }
        }

        var random = new Random(2);
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var i = ((y * 128) + x) * 4;
                    pixels[i + c] = (byte)((pixels[i + c] & 0xFE) | random.Next(2));
                }
            }
        }

        var map = LsbRandomness.Map(cover, blockSize: 32, cancellationToken: Ct);

        Assert.Equal((4, 2), (map.Columns, map.Rows));
        for (var row = 0; row < 2; row++)
        {
            for (var column = 0; column < 4; column++)
            {
                var index = (row * 4) + column;
                if (column < 2)
                {
                    Assert.True(map.Randomness[index] > 0.8, $"block {index}: {map.Randomness[index]}");
                    Assert.True(map.ExcessOverBit1[index] > 0.5, $"block {index}: {map.ExcessOverBit1[index]}");
                }
                else
                {
                    Assert.True(map.Randomness[index] < 0.2, $"block {index}: {map.Randomness[index]}");
                }
            }
        }

        Assert.Equal(0.5, map.SuspiciousBlockRatio);
        Assert.Null(LsbRandomness.Map(cover, renderHeatmap: false, cancellationToken: Ct).Heatmap);
        Assert.NotNull(map.Heatmap);
        Assert.Equal((128, 64), (map.Heatmap.Width, map.Heatmap.Height));
        Assert.True(map.Heatmap.Pixels[0] > 200);         // left: red
        Assert.True(map.Heatmap.Pixels[(127 * 4) + 2] > 200); // right: blue
    }

    [Fact]
    public void Honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => Steganalyzer.Analyze(Photo(32, 32), cts.Token));
        Assert.Throws<OperationCanceledException>(() => LsbRandomness.Map(Photo(32, 32), cancellationToken: cts.Token));
    }
}
