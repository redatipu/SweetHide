using SweetHide.Core.Imaging;

namespace SweetHide.Forensics.Steganalysis;

/// <param name="Channel">"R", "G" or "B".</param>
/// <param name="EstimatedRate">Estimated share of samples carrying message bits (0–1), NaN when the image gives no signal.</param>
/// <param name="RegularM">Share of regular groups under the mask M.</param>
/// <param name="SingularM">Share of singular groups under M.</param>
/// <param name="RegularMinusM">Share of regular groups under −M.</param>
/// <param name="SingularMinusM">Share of singular groups under −M.</param>
public sealed record RsChannelResult(string Channel, double EstimatedRate, double RegularM, double SingularM, double RegularMinusM, double SingularMinusM);

/// <summary>
/// RS steganalysis (Fridrich, Goljan and Du, 2001). Natural images have more "regular" than "singular" pixel groups
/// and LSB flipping moves the two counts towards each other in a predictable way, which gives the length of an
/// LSB-replacement message. Reliable roughly between 0 and 0.9; it does not detect LSB matching (what SweetHide uses).
/// </summary>
public static class RsAnalysis
{
    private const int GroupSize = 4;
    private static readonly string[] ChannelNames = ["R", "G", "B"];

    public static IReadOnlyList<RsChannelResult> Analyze(PixelBuffer image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ReadOnlySpan<byte> data = image.Pixels;
        var results = new List<RsChannelResult>(3);

        // [0..3] counts for the image as is, [4..7] for the image with every LSB flipped:
        // regular M, singular M, regular −M, singular −M.
        Span<long> counts = stackalloc long[8];
        for (var c = 0; c < 3; c++)
        {
            counts.Clear();
            long groups = 0;
            for (var y = 0; y < image.Height; y++)
            {
                if ((y & 127) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var row = y * image.Width * 4;
                for (var x = 0; x + GroupSize <= image.Width; x += GroupSize)
                {
                    var i = row + (x * 4);
                    if (data[i + 3] == 0 || data[i + 7] == 0 || data[i + 11] == 0 || data[i + 15] == 0)
                    {
                        continue;
                    }

                    int g0 = data[i + c], g1 = data[i + 4 + c], g2 = data[i + 8 + c], g3 = data[i + 12 + c];
                    Classify(g0, g1, g2, g3, counts[..4]);
                    Classify(g0 ^ 1, g1 ^ 1, g2 ^ 1, g3 ^ 1, counts[4..]);
                    groups++;
                }
            }

            if (groups == 0)
            {
                results.Add(new RsChannelResult(ChannelNames[c], double.NaN, 0, 0, 0, 0));
                continue;
            }

            double n = groups;
            double rm = counts[0] / n, sm = counts[1] / n, rnm = counts[2] / n, snm = counts[3] / n;
            double rm1 = counts[4] / n, sm1 = counts[5] / n, rnm1 = counts[6] / n, snm1 = counts[7] / n;
            results.Add(new RsChannelResult(ChannelNames[c], Estimate(rm - sm, rm1 - sm1, rnm - snm, rnm1 - snm1), rm, sm, rnm, snm));
        }

        return results;
    }

    /// <summary>
    /// Solves 2(d1 + d0)z² + (d−0 − d−1 − d1 − 3d0)z + d0 − d−0 = 0 for the root of smaller magnitude and returns
    /// p = z / (z − ½). With no real root (close to full embedding) the vertex is used instead.
    /// </summary>
    internal static double Estimate(double d0, double d1, double dm0, double dm1)
    {
        var a = 2 * (d1 + d0);
        var b = dm0 - dm1 - d1 - (3 * d0);
        var c = d0 - dm0;
        double z;
        if (Math.Abs(a) < 1e-12)
        {
            if (Math.Abs(b) < 1e-12)
            {
                return double.NaN;
            }

            z = -c / b;
        }
        else
        {
            var discriminant = (b * b) - (4 * a * c);
            if (discriminant < 0)
            {
                z = -b / (2 * a);
            }
            else
            {
                var root = Math.Sqrt(discriminant);
                var z1 = (-b + root) / (2 * a);
                var z2 = (-b - root) / (2 * a);
                z = Math.Abs(z1) < Math.Abs(z2) ? z1 : z2;
            }
        }

        var p = z / (z - 0.5);
        return double.IsFinite(p) ? Math.Clamp(p, 0, 1) : double.NaN;
    }

    /// <summary>Counts the group as regular or singular under M = [0 1 1 0] and −M = [0 −1 −1 0].</summary>
    private static void Classify(int g0, int g1, int g2, int g3, Span<long> counts)
    {
        var f = Smoothness(g0, g1, g2, g3);
        var fm = Smoothness(g0, g1 ^ 1, g2 ^ 1, g3);
        var fnm = Smoothness(g0, ShiftedFlip(g1), ShiftedFlip(g2), g3);
        if (fm > f)
        {
            counts[0]++;
        }
        else if (fm < f)
        {
            counts[1]++;
        }

        if (fnm > f)
        {
            counts[2]++;
        }
        else if (fnm < f)
        {
            counts[3]++;
        }
    }

    private static int Smoothness(int a, int b, int c, int d) => Math.Abs(b - a) + Math.Abs(c - b) + Math.Abs(d - c);

    /// <summary>F−1: swaps −1↔0, 1↔2, 3↔4, … (values may leave 0–255, which is fine for the differences).</summary>
    private static int ShiftedFlip(int x) => ((x + 1) ^ 1) - 1;
}
