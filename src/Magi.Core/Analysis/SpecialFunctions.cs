namespace Magi.Core.Analysis;

/// <summary>Regularized incomplete gamma function (Numerical Recipes, 3rd ed., §6.2).</summary>
internal static class SpecialFunctions
{
    private const int MaxIterations = 10_000;
    private const double Epsilon = 1e-15;
    private const double TinyValue = 1e-300;

    /// <summary>Q(a, x) = 1 − P(a, x): the chi-square survival function when a = dof/2, x = χ²/2.</summary>
    public static double UpperRegularizedGamma(double a, double x)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(a);
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        if (x == 0)
        {
            return 1;
        }

        return x < a + 1 ? 1 - LowerSeries(a, x) : UpperContinuedFraction(a, x);
    }

    internal static double LogGamma(double x)
    {
        // Lanczos approximation (g = 7, n = 9), accurate to ~15 digits for x > 0.
        ReadOnlySpan<double> coefficients =
        [
            0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313,
            -176.61502916214059, 12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6,
            1.5056327351493116e-7,
        ];

        if (x < 0.5)
        {
            return Math.Log(Math.PI / Math.Abs(Math.Sin(Math.PI * x))) - LogGamma(1 - x);
        }

        x -= 1;
        var sum = coefficients[0];
        for (var i = 1; i < coefficients.Length; i++)
        {
            sum += coefficients[i] / (x + i);
        }

        var t = x + 7.5;
        return (0.5 * Math.Log(2 * Math.PI)) + ((x + 0.5) * Math.Log(t)) - t + Math.Log(sum);
    }

    private static double LowerSeries(double a, double x)
    {
        var ap = a;
        var term = 1.0 / a;
        var sum = term;
        for (var n = 0; n < MaxIterations; n++)
        {
            ap++;
            term *= x / ap;
            sum += term;
            if (Math.Abs(term) < Math.Abs(sum) * Epsilon)
            {
                break;
            }
        }

        return sum * Math.Exp(-x + (a * Math.Log(x)) - LogGamma(a));
    }

    private static double UpperContinuedFraction(double a, double x)
    {
        // Modified Lentz's method.
        var b = x + 1 - a;
        var c = 1 / TinyValue;
        var d = 1 / b;
        var h = d;
        for (var i = 1; i < MaxIterations; i++)
        {
            var an = -i * (i - a);
            b += 2;
            d = (an * d) + b;
            if (Math.Abs(d) < TinyValue)
            {
                d = TinyValue;
            }

            c = b + (an / c);
            if (Math.Abs(c) < TinyValue)
            {
                c = TinyValue;
            }

            d = 1 / d;
            var delta = d * c;
            h *= delta;
            if (Math.Abs(delta - 1) < Epsilon)
            {
                break;
            }
        }

        return Math.Exp(-x + (a * Math.Log(x)) - LogGamma(a)) * h;
    }
}
