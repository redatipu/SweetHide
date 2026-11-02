namespace SweetHide.Core.Jpeg;

/// <summary>
/// The orthonormal 8x8 DCT-II that JPEG uses: <c>F(v,u) = 1/4 C(u) C(v) Σ f(y,x) cos((2x+1)uπ/16) cos((2y+1)vπ/16)</c>
/// with <c>C(0) = 1/√2</c>. Blocks are 64 values in natural (row-major) order; for coefficients the row is the
/// vertical frequency and the column the horizontal one, as in a JPEG file.
/// </summary>
/// <remarks>Plain separable float matrix products: accurate, simple, and fast enough (~1k multiplies per block).</remarks>
internal static class BlockDct
{
    /// <summary>M[u * 8 + x] = C(u)/2 · cos((2x + 1)uπ/16).</summary>
    private static readonly float[] Matrix = BuildMatrix();

    /// <summary>Basis[k * 64 + p]: pixel p of the basis image of natural coefficient k (unit coefficient).</summary>
    private static readonly float[] BasisImages = BuildBasis();

    /// <summary>The 64 basis images, each 64 values; coefficient k's image starts at k * 64.</summary>
    public static ReadOnlySpan<float> Basis => BasisImages;

    public static void Forward(ReadOnlySpan<float> pixels, Span<float> coefficients)
    {
        Span<float> temp = stackalloc float[64];
        var m = Matrix;
        for (var y = 0; y < 8; y++)
        {
            var row = pixels.Slice(y * 8, 8);
            for (var u = 0; u < 8; u++)
            {
                var sum = 0f;
                for (var x = 0; x < 8; x++)
                {
                    sum += m[(u * 8) + x] * row[x];
                }

                temp[(y * 8) + u] = sum;
            }
        }

        for (var v = 0; v < 8; v++)
        {
            for (var u = 0; u < 8; u++)
            {
                var sum = 0f;
                for (var y = 0; y < 8; y++)
                {
                    sum += m[(v * 8) + y] * temp[(y * 8) + u];
                }

                coefficients[(v * 8) + u] = sum;
            }
        }
    }

    public static void Inverse(ReadOnlySpan<float> coefficients, Span<float> pixels)
    {
        Span<float> temp = stackalloc float[64];
        var m = Matrix;
        for (var v = 0; v < 8; v++)
        {
            for (var x = 0; x < 8; x++)
            {
                var sum = 0f;
                for (var u = 0; u < 8; u++)
                {
                    sum += m[(u * 8) + x] * coefficients[(v * 8) + u];
                }

                temp[(v * 8) + x] = sum;
            }
        }

        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var sum = 0f;
                for (var v = 0; v < 8; v++)
                {
                    sum += m[(v * 8) + y] * temp[(v * 8) + x];
                }

                pixels[(y * 8) + x] = sum;
            }
        }
    }

    /// <summary>One coefficient of a block, as a dot product with its basis image.</summary>
    public static float Coefficient(ReadOnlySpan<float> pixels, int naturalIndex)
    {
        var basis = Basis.Slice(naturalIndex * 64, 64);
        var sum = 0f;
        for (var p = 0; p < 64; p++)
        {
            sum += basis[p] * pixels[p];
        }

        return sum;
    }

    private static float[] BuildMatrix()
    {
        var m = new float[64];
        for (var u = 0; u < 8; u++)
        {
            var c = u == 0 ? Math.Sqrt(0.5) : 1.0;
            for (var x = 0; x < 8; x++)
            {
                m[(u * 8) + x] = (float)(c / 2 * Math.Cos(((2 * x) + 1) * u * Math.PI / 16));
            }
        }

        return m;
    }

    private static float[] BuildBasis()
    {
        var basis = new float[64 * 64];
        for (var v = 0; v < 8; v++)
        {
            for (var u = 0; u < 8; u++)
            {
                var k = (v * 8) + u;
                for (var y = 0; y < 8; y++)
                {
                    for (var x = 0; x < 8; x++)
                    {
                        basis[(k * 64) + (y * 8) + x] = Matrix[(v * 8) + y] * Matrix[(u * 8) + x];
                    }
                }
            }
        }

        return basis;
    }
}
