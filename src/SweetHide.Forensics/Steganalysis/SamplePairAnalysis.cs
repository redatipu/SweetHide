using SweetHide.Core.Imaging;

namespace SweetHide.Forensics.Steganalysis;

/// <param name="Channel">"R", "G" or "B".</param>
/// <param name="EstimatedRate">Estimated share of samples carrying message bits (0–1), NaN when it cannot be computed.</param>
/// <param name="Pairs">Number of adjacent sample pairs used.</param>
public sealed record SpaChannelResult(string Channel, double EstimatedRate, long Pairs);

/// <summary>
/// Sample Pair Analysis (Dumitrescu, Wu and Wang, 2003) over horizontally and vertically adjacent pixels. Like RS
/// it estimates the length of an LSB-replacement message, from a different statistic, so the two can be compared.
/// </summary>
public static class SamplePairAnalysis
{
    private static readonly string[] ChannelNames = ["R", "G", "B"];

    public static IReadOnlyList<SpaChannelResult> Analyze(PixelBuffer image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ReadOnlySpan<byte> data = image.Pixels;
        var width = image.Width;
        var results = new List<SpaChannelResult>(3);
        for (var c = 0; c < 3; c++)
        {
            var counts = new PairCounts();
            for (var y = 0; y < image.Height; y++)
            {
                if ((y & 127) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                for (var x = 0; x < width; x++)
                {
                    var i = ((y * width) + x) * 4;
                    if (data[i + 3] == 0)
                    {
                        continue;
                    }

                    if (x + 1 < width && data[i + 7] != 0)
                    {
                        counts.Add(data[i + c], data[i + 4 + c]);
                    }

                    if (y + 1 < image.Height && data[i + (width * 4) + 3] != 0)
                    {
                        counts.Add(data[i + c], data[i + (width * 4) + c]);
                    }
                }
            }

            results.Add(new SpaChannelResult(ChannelNames[c], counts.Estimate(), counts.Total));
        }

        return results;
    }

    private sealed class PairCounts
    {
        private long _x;
        private long _y;
        private long _k;

        public long Total { get; private set; }

        public void Add(int u, int v)
        {
            Total++;
            if ((u >> 1) == (v >> 1))
            {
                _k++; // the pair differs at most in its least significant bit
            }

            if (u == v)
            {
                return;
            }

            // X: v even and u < v, or v odd and u > v.  Y: the opposite.
            if (((v & 1) == 0) == (u < v))
            {
                _x++;
            }
            else
            {
                _y++;
            }
        }

        /// <summary>
        /// Smaller root β of 2k·β² + 2(2x − n)·β + (y − x) = 0; β is half the embedding rate. Near full embedding
        /// the equation has no real root and the vertex is used.
        /// </summary>
        public double Estimate()
        {
            if (_k == 0)
            {
                return double.NaN;
            }

            double a = 2 * _k, b = 2 * ((2 * _x) - Total), c = _y - _x;
            var discriminant = (b * b) - (4 * a * c);
            var beta = discriminant < 0 ? -b / (2 * a) : Math.Min((-b + Math.Sqrt(discriminant)) / (2 * a), (-b - Math.Sqrt(discriminant)) / (2 * a));
            return double.IsFinite(beta) ? Math.Clamp(2 * beta, 0, 1) : double.NaN;
        }
    }
}
