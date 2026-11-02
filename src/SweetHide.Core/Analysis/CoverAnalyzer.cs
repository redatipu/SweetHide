using System.Globalization;
using System.Text;

using SweetHide.Core.Imaging;

namespace SweetHide.Core.Analysis;

/// <summary>How well a cover hides embedding noise.</summary>
public enum CoverRating
{
    /// <summary>Embedding is likely to be detectable; pick another photo.</summary>
    Poor = 0,

    /// <summary>Usable for small payloads at 1 bit per channel.</summary>
    Fair = 1,

    /// <summary>A noisy, detailed photo: the kind of cover steganography works best in.</summary>
    Good = 2,
}

/// <summary>A specific reason a cover scores lower. The UI can turn these into localized advice.</summary>
public enum CoverIssue
{
    /// <summary>Large areas without any noise (sky, flat graphics, screenshots): ±1 changes stand out there.</summary>
    FlatAreas = 1,

    /// <summary>Many pure black or white values, which embedding can only move in one direction.</summary>
    SaturatedValues = 2,

    /// <summary>Little texture overall (smoothed, denoised or synthetic image).</summary>
    LowTexture = 3,

    /// <summary>
    /// Looks like a decoded JPEG (visible 8x8 block edges). Changing its low bits breaks the JPEG structure, which
    /// a "JPEG compatibility" test detects even for tiny payloads. Prefer an original PNG or RAW export.
    /// </summary>
    JpegOrigin = 4,

    /// <summary>More than half of the pixels are fully transparent, so there is little room.</summary>
    MostlyTransparent = 5,
}

/// <param name="Score">0 (do not use) to 100 (excellent).</param>
/// <param name="Rating">The score in three bands: below 40 poor, below 70 fair, else good.</param>
/// <param name="FlatShare">Share of colour values whose neighbourhood varies by 2 or less.</param>
/// <param name="SaturatedShare">Share of colour values that are 0 or 255.</param>
/// <param name="MedianTexture">Median neighbourhood range (max - min) of the colour values.</param>
/// <param name="Blockiness">Average change across 8-pixel block edges divided by the average change elsewhere (about 1 for photos).</param>
/// <param name="Issues">What lowered the score, most important first.</param>
public sealed record CoverAssessment(
    int Score,
    CoverRating Rating,
    double FlatShare,
    double SaturatedShare,
    double MedianTexture,
    double Blockiness,
    IReadOnlyList<CoverIssue> Issues)
{
    /// <summary>A one-paragraph English explanation (UIs with translations should build theirs from <see cref="Issues"/>).</summary>
    public string Explanation
    {
        get
        {
            var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"Cover score {Score}/100 ({Rating}). "));
            if (Issues.Count == 0)
            {
                text.Append("Detailed and noisy throughout, which hides small changes well.");
            }

            foreach (var issue in Issues)
            {
                text.Append(issue switch
                {
                    CoverIssue.FlatAreas => string.Create(CultureInfo.InvariantCulture, $"{FlatShare:P0} of it is flat, where changes stand out. "),
                    CoverIssue.SaturatedValues => string.Create(CultureInfo.InvariantCulture, $"{SaturatedShare:P0} of the values are pure black or white. "),
                    CoverIssue.LowTexture => "It has little texture overall. ",
                    CoverIssue.JpegOrigin => "It looks like a decoded JPEG; hiding in it can be detected even for small payloads, so use an original PNG instead. ",
                    CoverIssue.MostlyTransparent => "Most of it is transparent. ",
                    _ => string.Empty,
                });
            }

            return text.ToString().TrimEnd();
        }
    }
}

/// <summary>
/// Estimates how detectable LSB embedding would be in a cover, before hiding anything. A heuristic for choosing
/// between photos, not a guarantee: no score makes a large payload at 4 bits per channel safe.
/// </summary>
/// <remarks>
/// Score = 100 − 100 × flat share − 50 × saturated share − 5 per step of median texture below 4 − 30 if it looks
/// like a decoded JPEG, clamped to 0–100. "Flat" means the value and its four neighbours span at most 2 levels.
/// </remarks>
public static class CoverAnalyzer
{
    private const int FlatRange = 2;
    private const double JpegBlockiness = 1.15;

    public static CoverAssessment Assess(PixelBuffer cover)
    {
        ArgumentNullException.ThrowIfNull(cover);
        var data = cover.Data;
        var width = cover.Width;
        var height = cover.Height;

        var histogram = new long[256];
        long saturated = 0, usable = 0;
        double edgeSum = 0, innerSum = 0;
        long edgeCount = 0, innerCount = 0;
        var gate = new object();

        Parallel.For(
            0,
            height,
            () => new Totals(),
            (y, _, totals) =>
            {
                for (var x = 0; x < width; x++)
                {
                    var i = ((y * width) + x) * 4;
                    if (data[i + 3] == 0)
                    {
                        continue;
                    }

                    totals.Usable++;
                    for (var c = 0; c < 3; c++)
                    {
                        var value = data[i + c];
                        int min = value, max = value;
                        Neighbour(x - 1, y, c, ref min, ref max);
                        Neighbour(x + 1, y, c, ref min, ref max);
                        Neighbour(x, y - 1, c, ref min, ref max);
                        Neighbour(x, y + 1, c, ref min, ref max);
                        totals.Histogram[max - min]++;
                        totals.Saturated += value is 0 or 255 ? 1 : 0;

                        if (x + 1 < width && data[i + 7] != 0)
                        {
                            var step = Math.Abs(value - data[i + 4 + c]);
                            if (x % 8 == 7)
                            {
                                totals.EdgeSum += step;
                                totals.EdgeCount++;
                            }
                            else
                            {
                                totals.InnerSum += step;
                                totals.InnerCount++;
                            }
                        }
                    }
                }

                return totals;
            },
            totals =>
            {
                lock (gate)
                {
                    for (var t = 0; t < 256; t++)
                    {
                        histogram[t] += totals.Histogram[t];
                    }

                    usable += totals.Usable;
                    saturated += totals.Saturated;
                    edgeSum += totals.EdgeSum;
                    innerSum += totals.InnerSum;
                    edgeCount += totals.EdgeCount;
                    innerCount += totals.InnerCount;
                }
            });

        var samples = usable * 3;
        if (samples == 0)
        {
            return new CoverAssessment(0, CoverRating.Poor, 0, 0, 0, 1, [CoverIssue.MostlyTransparent]);
        }

        var flatShare = (double)histogram.Take(FlatRange + 1).Sum() / samples;
        var saturatedShare = (double)saturated / samples;
        var median = Median(histogram, samples);
        var inner = innerCount == 0 ? 0 : innerSum / innerCount;
        var edge = edgeCount == 0 ? 0 : edgeSum / edgeCount;
        var blockiness = inner < 0.5 || width < 16 ? 1.0 : edge / inner;
        var jpeg = blockiness >= JpegBlockiness;

        var score = 100 - (100 * flatShare) - (50 * saturatedShare) - (5 * Math.Max(0, 4 - median)) - (jpeg ? 30 : 0);
        var rounded = (int)Math.Round(Math.Clamp(score, 0, 100), MidpointRounding.AwayFromZero);

        var issues = new List<CoverIssue>();
        if (jpeg)
        {
            issues.Add(CoverIssue.JpegOrigin);
        }

        if (flatShare > 0.15)
        {
            issues.Add(CoverIssue.FlatAreas);
        }

        if (saturatedShare > 0.05)
        {
            issues.Add(CoverIssue.SaturatedValues);
        }

        if (median < 4)
        {
            issues.Add(CoverIssue.LowTexture);
        }

        if (usable * 2 < cover.PixelCount)
        {
            issues.Add(CoverIssue.MostlyTransparent);
        }

        var rating = rounded >= 70 ? CoverRating.Good : rounded >= 40 ? CoverRating.Fair : CoverRating.Poor;
        return new CoverAssessment(rounded, rating, flatShare, saturatedShare, median, blockiness, issues);

        void Neighbour(int nx, int ny, int channel, ref int min, ref int max)
        {
            if (nx < 0 || ny < 0 || nx >= width || ny >= height)
            {
                return;
            }

            var offset = ((ny * width) + nx) * 4;
            if (data[offset + 3] == 0)
            {
                return;
            }

            var value = data[offset + channel];
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }
    }

    private static double Median(long[] histogram, long total)
    {
        long seen = 0;
        for (var t = 0; t < histogram.Length; t++)
        {
            seen += histogram[t];
            if (seen * 2 >= total)
            {
                return t;
            }
        }

        return histogram.Length - 1;
    }

    private sealed class Totals
    {
        public long[] Histogram { get; } = new long[256];

        public long Usable { get; set; }

        public long Saturated { get; set; }

        public double EdgeSum { get; set; }

        public double InnerSum { get; set; }

        public long EdgeCount { get; set; }

        public long InnerCount { get; set; }
    }
}
