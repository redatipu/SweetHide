namespace SweetHide.Core.Robust;

/// <summary>
/// Systematic Reed–Solomon code over GF(2^8) (primitive polynomial 0x11D, generator α = 2, first consecutive root
/// α^0), the same field and convention as QR codes. Corrects <c>e</c> errors and <c>f</c> erasures whenever
/// <c>2e + f ≤ paritySymbols</c>; shortened codewords (fewer than 255 symbols) are supported.
/// </summary>
/// <remarks>
/// Codeword layout is <c>data ‖ parity</c>; symbol <c>i</c> of an <c>n</c>-symbol codeword is the coefficient of
/// <c>x^(n-1-i)</c>. Decoding is Berlekamp–Massey on Forney syndromes, Chien search, then Forney's formula, and the
/// result is accepted only if every syndrome of the corrected word is zero.
/// </remarks>
internal sealed class ReedSolomon
{
    public const int MaxCodewordLength = 255;

    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    private readonly int[] _generator;

    static ReedSolomon()
    {
        var x = 1;
        for (var i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0)
            {
                x ^= 0x11D;
            }
        }

        for (var i = 255; i < Exp.Length; i++)
        {
            Exp[i] = Exp[i - 255];
        }
    }

    public ReedSolomon(int paritySymbols)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(paritySymbols, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(paritySymbols, MaxCodewordLength - 1);
        ParitySymbols = paritySymbols;

        // g(x) = Π (x - α^i), i = 0 .. paritySymbols-1, highest degree first.
        int[] generator = [1];
        for (var i = 0; i < paritySymbols; i++)
        {
            generator = PolyMultiply(generator, [1, Power(2, i)]);
        }

        _generator = generator;
    }

    public int ParitySymbols { get; }

    /// <summary>Computes the parity symbols for <paramref name="data"/>.</summary>
    public void Encode(ReadOnlySpan<byte> data, Span<byte> parity)
    {
        if (data.Length + ParitySymbols > MaxCodewordLength)
        {
            throw new ArgumentException($"At most {MaxCodewordLength - ParitySymbols} data symbols fit in one codeword.", nameof(data));
        }

        if (parity.Length != ParitySymbols)
        {
            throw new ArgumentException($"Expected {ParitySymbols} parity symbols.", nameof(parity));
        }

        // Remainder of data(x) · x^parity divided by g(x), by synthetic division.
        Span<int> remainder = stackalloc int[ParitySymbols];
        foreach (var symbol in data)
        {
            var factor = symbol ^ remainder[0];
            for (var j = 0; j < ParitySymbols - 1; j++)
            {
                remainder[j] = remainder[j + 1] ^ Multiply(_generator[j + 1], factor);
            }

            remainder[ParitySymbols - 1] = Multiply(_generator[ParitySymbols], factor);
        }

        for (var j = 0; j < ParitySymbols; j++)
        {
            parity[j] = (byte)remainder[j];
        }
    }

    /// <summary>Corrects <paramref name="codeword"/> (data ‖ parity) in place.</summary>
    /// <param name="codeword">The received codeword; left unchanged if decoding fails.</param>
    /// <param name="erasures">Positions known (or suspected) to be wrong; their values are ignored.</param>
    /// <param name="corrected">Number of symbols that were changed.</param>
    /// <returns>False if the word has more errors than the code can correct (as far as can be detected).</returns>
    public bool TryDecode(Span<byte> codeword, ReadOnlySpan<int> erasures, out int corrected)
    {
        corrected = 0;
        var n = codeword.Length;
        if (n <= ParitySymbols || n > MaxCodewordLength)
        {
            throw new ArgumentException("Codeword length out of range.", nameof(codeword));
        }

        var erasureList = new List<int>();
        foreach (var position in erasures)
        {
            if (position < 0 || position >= n)
            {
                throw new ArgumentOutOfRangeException(nameof(erasures), position, "Erasure position outside the codeword.");
            }

            if (!erasureList.Contains(position))
            {
                erasureList.Add(position);
            }
        }

        if (erasureList.Count > ParitySymbols)
        {
            return false;
        }

        var message = new int[n];
        for (var i = 0; i < n; i++)
        {
            message[i] = codeword[i];
        }

        foreach (var position in erasureList)
        {
            message[position] = 0;
        }

        var syndromes = Syndromes(message);
        if (Array.TrueForAll(syndromes, s => s == 0))
        {
            return Commit(codeword, message, out corrected);
        }

        var forney = ForneySyndromes(syndromes, erasureList, n);
        var locator = FindErrorLocator(forney, erasureList.Count);
        if (locator is null)
        {
            return false;
        }

        var errors = FindErrors([.. Enumerable.Reverse(locator)], n);
        if (errors is null)
        {
            return false;
        }

        var errata = new List<int>(erasureList);
        errata.AddRange(errors);
        if (!CorrectErrata(message, syndromes, errata))
        {
            return false;
        }

        return Array.TrueForAll(Syndromes(message), s => s == 0) && Commit(codeword, message, out corrected);
    }

    private static bool Commit(Span<byte> codeword, int[] message, out int corrected)
    {
        corrected = 0;
        for (var i = 0; i < message.Length; i++)
        {
            if (codeword[i] != message[i])
            {
                corrected++;
                codeword[i] = (byte)message[i];
            }
        }

        return true;
    }

    /// <summary>S_i = r(α^i), with a leading 0 so that indices line up with the textbook formulas.</summary>
    private int[] Syndromes(int[] message)
    {
        var syndromes = new int[ParitySymbols + 1];
        for (var i = 0; i < ParitySymbols; i++)
        {
            syndromes[i + 1] = PolyEvaluate(message, Power(2, i));
        }

        return syndromes;
    }

    /// <summary>Syndromes with the effect of the erasures removed, so Berlekamp–Massey only has to find the errors.</summary>
    private static int[] ForneySyndromes(int[] syndromes, List<int> erasures, int n)
    {
        var forney = syndromes[1..];
        foreach (var position in erasures)
        {
            var x = Power(2, n - 1 - position);
            for (var j = 0; j < forney.Length - 1; j++)
            {
                forney[j] = Multiply(forney[j], x) ^ forney[j + 1];
            }
        }

        return forney;
    }

    /// <summary>Berlekamp–Massey. Returns the error locator (highest degree first), or null if there are too many errors.</summary>
    private int[]? FindErrorLocator(int[] syndromes, int erasureCount)
    {
        int[] locator = [1];
        int[] previous = [1];
        for (var i = 0; i < ParitySymbols - erasureCount; i++)
        {
            var k = i;
            var delta = syndromes[k];
            for (var j = 1; j < locator.Length && j <= k; j++)
            {
                delta ^= Multiply(locator[^(j + 1)], syndromes[k - j]);
            }

            previous = [.. previous, 0];
            if (delta != 0)
            {
                if (previous.Length > locator.Length)
                {
                    var next = PolyScale(previous, delta);
                    previous = PolyScale(locator, Inverse(delta));
                    locator = next;
                }

                locator = PolyAdd(locator, PolyScale(previous, delta));
            }
        }

        var start = 0;
        while (start < locator.Length && locator[start] == 0)
        {
            start++;
        }

        locator = locator[start..];
        var errorCount = locator.Length - 1;
        return (errorCount * 2) + erasureCount > ParitySymbols ? null : locator;
    }

    /// <summary>Chien search: positions whose locator root exists. Null if the root count doesn't match the degree.</summary>
    private static List<int>? FindErrors(int[] locator, int n)
    {
        var errorCount = locator.Length - 1;
        var positions = new List<int>();
        for (var i = 0; i < n; i++)
        {
            if (PolyEvaluate(locator, Power(2, i)) == 0)
            {
                positions.Add(n - 1 - i);
            }
        }

        return positions.Count == errorCount ? positions : null;
    }

    /// <summary>Forney's algorithm: computes and applies the error values at the known errata positions.</summary>
    private static bool CorrectErrata(int[] message, int[] syndromes, List<int> positions)
    {
        var n = message.Length;
        var coefficientPositions = positions.Select(p => n - 1 - p).ToArray();

        // Errata locator Λ(x) = Π (1 - x α^c), highest degree first.
        int[] locator = [1];
        foreach (var c in coefficientPositions)
        {
            locator = PolyMultiply(locator, PolyAdd([1], [Power(2, c), 0]));
        }

        // Evaluator Ω(x) = S(x) Λ(x) mod x^(ν+1).
        var reversedSyndromes = syndromes.Reverse().ToArray();
        var product = PolyMultiply(reversedSyndromes, locator);
        var keep = locator.Length; // ν + 1 coefficients
        var evaluator = product[Math.Max(0, product.Length - keep)..];

        var roots = coefficientPositions.Select(c => Power(2, c)).ToArray();
        for (var i = 0; i < roots.Length; i++)
        {
            var rootInverse = Inverse(roots[i]);
            var derivative = 1;
            for (var j = 0; j < roots.Length; j++)
            {
                if (j != i)
                {
                    derivative = Multiply(derivative, 1 ^ Multiply(rootInverse, roots[j]));
                }
            }

            if (derivative == 0)
            {
                return false;
            }

            var y = Multiply(roots[i], PolyEvaluate(evaluator, rootInverse));
            message[positions[i]] ^= Divide(y, derivative);
        }

        return true;
    }

    private static int Multiply(int a, int b) => a == 0 || b == 0 ? 0 : Exp[Log[a] + Log[b]];

    private static int Divide(int a, int b) => a == 0 ? 0 : Exp[Log[a] + 255 - Log[b]];

    private static int Inverse(int a) => Exp[255 - Log[a]];

    private static int Power(int a, int exponent) => Exp[(((Log[a] * exponent) % 255) + 255) % 255];

    private static int[] PolyScale(int[] p, int x) => [.. p.Select(c => Multiply(c, x))];

    private static int[] PolyAdd(int[] p, int[] q)
    {
        var result = new int[Math.Max(p.Length, q.Length)];
        for (var i = 0; i < p.Length; i++)
        {
            result[i + result.Length - p.Length] = p[i];
        }

        for (var i = 0; i < q.Length; i++)
        {
            result[i + result.Length - q.Length] ^= q[i];
        }

        return result;
    }

    private static int[] PolyMultiply(int[] p, int[] q)
    {
        var result = new int[p.Length + q.Length - 1];
        for (var j = 0; j < q.Length; j++)
        {
            for (var i = 0; i < p.Length; i++)
            {
                result[i + j] ^= Multiply(p[i], q[j]);
            }
        }

        return result;
    }

    /// <summary>Horner evaluation; coefficients highest degree first.</summary>
    private static int PolyEvaluate(ReadOnlySpan<int> poly, int x)
    {
        var y = poly[0];
        for (var i = 1; i < poly.Length; i++)
        {
            y = Multiply(y, x) ^ poly[i];
        }

        return y;
    }
}
