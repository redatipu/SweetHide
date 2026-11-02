using SweetHide.Core.Analysis;
using SweetHide.Core.Imaging;

namespace SweetHide.Forensics.Steganalysis;

/// <param name="Channel">"R", "G" or "B".</param>
/// <param name="OnesRatio">Share of values whose LSB is 1 (from Core's <see cref="LsbAnalyzer"/>).</param>
/// <param name="ChiSquareProbability">Chi-square attack p-value from Core's <see cref="LsbAnalyzer"/>; near 1 = LSB replacement.</param>
/// <param name="RsRate">RS estimate of the embedding rate (0–1).</param>
/// <param name="SpaRate">Sample Pair Analysis estimate of the embedding rate (0–1).</param>
public sealed record ChannelSteganalysis(string Channel, double OnesRatio, double ChiSquareProbability, double RsRate, double SpaRate)
{
    /// <summary>The more conservative of the two rate estimates (they have different biases).</summary>
    public double EstimatedRate => double.IsNaN(RsRate) ? SpaRate : double.IsNaN(SpaRate) ? RsRate : Math.Min(RsRate, SpaRate);
}

/// <param name="Width">Image width.</param>
/// <param name="Height">Image height.</param>
/// <param name="Channels">Per-channel statistics.</param>
public sealed record SteganalysisReport(int Width, int Height, IReadOnlyList<ChannelSteganalysis> Channels)
{
    /// <summary>Highest per-channel <see cref="ChannelSteganalysis.EstimatedRate"/>.</summary>
    public double MaxEstimatedRate => Channels.Select(c => c.EstimatedRate).Where(double.IsFinite).DefaultIfEmpty(0).Max();
}

/// <summary>Runs chi-square (Core), RS and SPA together so their verdicts can be read side by side.</summary>
public static class Steganalyzer
{
    public static SteganalysisReport Analyze(PixelBuffer image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var chi = LsbAnalyzer.Analyze(image);
        cancellationToken.ThrowIfCancellationRequested();
        var rs = RsAnalysis.Analyze(image, cancellationToken);
        var spa = SamplePairAnalysis.Analyze(image, cancellationToken);
        var channels = chi.Channels
            .Select((c, i) => new ChannelSteganalysis(c.Channel, c.OnesRatio, c.EmbeddingProbability, rs[i].EstimatedRate, spa[i].EstimatedRate))
            .ToList();
        return new SteganalysisReport(image.Width, image.Height, channels);
    }
}
