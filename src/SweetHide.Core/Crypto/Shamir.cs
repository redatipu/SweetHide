using SweetHide.Core.Stego;

namespace SweetHide.Core.Crypto;

/// <summary>
/// Shamir secret sharing over GF(2^8) (the AES field, polynomial x^8 + x^4 + x^3 + x + 1), byte by byte: each secret
/// byte is the constant term of its own random polynomial of degree k - 1, and share <c>x</c> holds the polynomials'
/// values at <c>x</c>. Any k shares determine the secret; any k - 1 are uniformly random and say nothing about it.
/// </summary>
/// <remarks>
/// Multiplication is branch-free shift-and-add rather than log/exp tables, so timing does not depend on secret bytes.
/// Shares are malleable (changing one share shifts the secret predictably); <see cref="ShamirShare"/> adds a MAC with
/// a key inside the secret to catch that.
/// </remarks>
internal static class Shamir
{
    private const int Block = 4096;

    /// <summary>Splits <paramref name="secret"/> into <paramref name="count"/> shares at x = 1, 2, …, count.</summary>
    public static byte[][] Split(ReadOnlySpan<byte> secret, int threshold, int count, IRandomSource random)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threshold, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(threshold, count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 255);

        var shares = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            shares[i] = new byte[secret.Length];
        }

        // Coefficients 1..k-1 for one block of secret bytes at a time.
        var coefficients = new byte[(threshold - 1) * Block];
        try
        {
            for (var start = 0; start < secret.Length; start += Block)
            {
                var length = Math.Min(Block, secret.Length - start);
                random.Fill(coefficients);
                for (var i = 0; i < count; i++)
                {
                    var x = (byte)(i + 1);
                    var share = shares[i];
                    for (var j = 0; j < length; j++)
                    {
                        // Horner: ((c[k-1] * x + c[k-2]) * x + ... + c[1]) * x + secret
                        byte y = 0;
                        for (var d = threshold - 2; d >= 0; d--)
                        {
                            y = (byte)(Multiply(y, x) ^ coefficients[(d * Block) + j]);
                        }

                        share[start + j] = (byte)(Multiply(y, x) ^ secret[start + j]);
                    }
                }
            }
        }
        finally
        {
            Array.Clear(coefficients);
        }

        return shares;
    }

    /// <summary>Recovers the secret from shares with distinct non-zero x (exactly the threshold number of them).</summary>
    public static byte[] Combine(IReadOnlyList<(byte X, byte[] Y)> shares)
    {
        var length = shares[0].Y.Length;
        var weights = new byte[shares.Count];
        for (var i = 0; i < shares.Count; i++)
        {
            // Lagrange basis at 0: product over m != i of x_m / (x_m - x_i); subtraction is XOR in GF(2^8).
            byte numerator = 1, denominator = 1;
            for (var m = 0; m < shares.Count; m++)
            {
                if (m != i)
                {
                    numerator = Multiply(numerator, shares[m].X);
                    denominator = Multiply(denominator, (byte)(shares[m].X ^ shares[i].X));
                }
            }

            weights[i] = Multiply(numerator, Inverse(denominator));
        }

        var secret = new byte[length];
        for (var i = 0; i < shares.Count; i++)
        {
            var y = shares[i].Y;
            var weight = weights[i];
            for (var j = 0; j < length; j++)
            {
                secret[j] ^= Multiply(y[j], weight);
            }
        }

        return secret;
    }

    /// <summary>GF(2^8) product, without branches or table lookups on the operands.</summary>
    public static byte Multiply(byte a, byte b)
    {
        var product = 0;
        int x = a, y = b;
        for (var bit = 0; bit < 8; bit++)
        {
            product ^= -(y & 1) & x;
            x = (x << 1) ^ (-((x >> 7) & 1) & 0x11B);
            y >>= 1;
        }

        return (byte)product;
    }

    /// <summary>a^254 = a^-1 for a != 0.</summary>
    public static byte Inverse(byte a)
    {
        if (a == 0)
        {
            throw new DivideByZeroException();
        }

        byte result = 1;
        var power = a;
        for (var exponent = 254; exponent > 0; exponent >>= 1)
        {
            if ((exponent & 1) != 0)
            {
                result = Multiply(result, power);
            }

            power = Multiply(power, power);
        }

        return result;
    }
}
