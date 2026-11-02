using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SweetHide.Core.Robust;

/// <summary>
/// Deterministic xoshiro256** stream seeded from SHA-256(label ‖ 0 ‖ key). Used to pick blocks, coefficients and
/// dithers, so the same key and label give the same choices on every platform. Not a cipher: it only decides where
/// bits go; secrecy of the payload is the caller's job.
/// </summary>
internal sealed class KeyedRandom
{
    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;

    public KeyedRandom(ReadOnlySpan<byte> key, string label)
    {
        var labelBytes = Encoding.ASCII.GetBytes(label);
        var input = new byte[labelBytes.Length + 1 + key.Length];
        labelBytes.CopyTo(input, 0);
        key.CopyTo(input.AsSpan(labelBytes.Length + 1));
        Span<byte> seed = stackalloc byte[32];
        SHA256.HashData(input, seed);
        _s0 = BinaryPrimitives.ReadUInt64LittleEndian(seed);
        _s1 = BinaryPrimitives.ReadUInt64LittleEndian(seed[8..]);
        _s2 = BinaryPrimitives.ReadUInt64LittleEndian(seed[16..]);
        _s3 = BinaryPrimitives.ReadUInt64LittleEndian(seed[24..]) | 1; // never the all-zero state
    }

    public ulong NextUInt64()
    {
        var result = ulong.RotateLeft(_s1 * 5, 7) * 9;
        var t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = ulong.RotateLeft(_s3, 45);
        return result;
    }

    /// <summary>Uniform integer in [0, bound), without modulo bias (Lemire's method).</summary>
    public int Next(int bound)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bound);
        var range = (ulong)bound;
        var threshold = (0 - range) % range;
        while (true)
        {
            var product = Math.BigMul(NextUInt64(), range, out var low);
            if (low >= threshold)
            {
                return (int)product;
            }
        }
    }

    /// <summary>Uniform in [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    public bool NextBit() => (NextUInt64() >> 63) == 1;

    /// <summary>A uniformly random permutation of [0, count) (Fisher–Yates).</summary>
    public int[] Permutation(int count)
    {
        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = i;
        }

        for (var i = count - 1; i > 0; i--)
        {
            var j = Next(i + 1);
            (result[i], result[j]) = (result[j], result[i]);
        }

        return result;
    }
}
