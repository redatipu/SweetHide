using System.Buffers.Binary;
using System.Numerics;

namespace Magi.Core.Stego;

/// <summary>
/// A keyed pseudo-random permutation of [0, size) built from a balanced Feistel network with cycle-walking.
/// O(1) memory and random access, so huge images never need a shuffled index array.
/// </summary>
/// <remarks>
/// Only spreads the embedded bits over the image; confidentiality comes from AES-GCM, not from this.
/// Pure integer arithmetic, so results are identical on every platform.
/// </remarks>
internal sealed class FeistelPermutation
{
    public const int KeySize = Rounds * sizeof(ulong);
    private const int Rounds = 8;

    private readonly ulong _size;
    private readonly int _halfBits;
    private readonly ulong _halfMask;
    private readonly ulong[] _roundKeys = new ulong[Rounds];

    public FeistelPermutation(long size, ReadOnlySpan<byte> key)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        if (key.Length < KeySize)
        {
            throw new ArgumentException($"Key must be at least {KeySize} bytes.", nameof(key));
        }

        _size = (ulong)size;

        // Smallest even bit width whose domain 2^bits covers [0, size). Domain < 4 * size, so cycle-walking
        // needs fewer than 4 rounds of the network on average.
        var bits = size <= 1 ? 2 : 64 - BitOperations.LeadingZeroCount((ulong)(size - 1));
        bits = Math.Max(bits, 2);
        if (bits % 2 == 1)
        {
            bits++;
        }

        _halfBits = bits / 2;
        _halfMask = (1UL << _halfBits) - 1;

        for (var r = 0; r < Rounds; r++)
        {
            _roundKeys[r] = BinaryPrimitives.ReadUInt64LittleEndian(key[(r * 8)..]);
        }
    }

    public long Size => (long)_size;

    public long Map(long index)
    {
        if ((ulong)index >= _size)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var x = (ulong)index;
        do
        {
            x = Encrypt(x);
        }
        while (x >= _size);

        return (long)x;
    }

    private ulong Encrypt(ulong x)
    {
        var left = x >> _halfBits;
        var right = x & _halfMask;
        for (var r = 0; r < Rounds; r++)
        {
            var f = Mix(right ^ _roundKeys[r]) & _halfMask;
            (left, right) = (right, left ^ f);
        }

        return (left << _halfBits) | right;
    }

    // SplitMix64 finalizer: a fast, well-distributed 64-bit bijection.
    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
