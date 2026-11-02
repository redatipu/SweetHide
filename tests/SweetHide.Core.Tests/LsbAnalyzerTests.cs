using SweetHide.Core.Analysis;
using SweetHide.Core.Imaging;

namespace SweetHide.Core.Tests;

public class LsbAnalyzerTests
{
    [Fact]
    public void Chi_square_flags_full_lsb_replacement_but_not_a_clean_image()
    {
        // Clean cover with a strongly uneven pairs-of-values histogram (only even values).
        var random = new Random(9);
        var clean = new byte[200 * 200 * 4];
        for (var i = 0; i < clean.Length; i++)
        {
            clean[i] = (i & 3) == 3 ? (byte)255 : (byte)(random.Next(0, 128) * 2);
        }

        var cleanReport = LsbAnalyzer.Analyze(new PixelBuffer(200, 200, clean));
        Assert.All(cleanReport.Channels, c => Assert.True(c.EmbeddingProbability < 0.01));

        // Overwrite every LSB with a random bit (classic LSB replacement) → pairs equalise.
        var replaced = (byte[])clean.Clone();
        for (var i = 0; i < replaced.Length; i++)
        {
            if ((i & 3) != 3)
            {
                replaced[i] = (byte)((replaced[i] & 0xFE) | random.Next(0, 2));
            }
        }

        var stegoReport = LsbAnalyzer.Analyze(new PixelBuffer(200, 200, replaced));
        Assert.All(stegoReport.Channels, c => Assert.True(c.EmbeddingProbability > 0.5, $"{c.Channel}: {c.EmbeddingProbability}"));
        Assert.All(stegoReport.Channels, c => Assert.InRange(c.OnesRatio, 0.48, 0.52));
    }

    [Fact]
    public void Bit_plane_render_is_black_and_white()
    {
        var plane = LsbAnalyzer.RenderBitPlane(TestImages.Photo(30, 30));
        var data = plane.Pixels.ToArray();
        for (var i = 0; i < data.Length; i++)
        {
            Assert.True(data[i] is 0 or 255);
        }
    }

    [Theory]
    [InlineData(1.0, 0.0, 1.0)]
    [InlineData(1.0, 1.0, 0.36787944117144233)]  // Q(1, x) = e^-x
    [InlineData(2.5, 3.0, 0.3062189184132784)]   // chi-square survival, 5 dof, χ² = 6
    [InlineData(63.5, 63.5, 0.4833106814532075)] // chi-square survival, 127 dof, χ² = 127
    [InlineData(63.5, 200.0, 0.0)]               // far tail
    public void Upper_regularized_gamma_matches_reference_values(double a, double x, double expected)
    {
        Assert.Equal(expected, SpecialFunctions.UpperRegularizedGamma(a, x), 1e-9);
    }
}
