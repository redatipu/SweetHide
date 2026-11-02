using System.Security.Cryptography;

using SweetHide.Core.Imaging;

namespace SweetHide.Core.Stego;

/// <summary>A sequence of slot indices to read or write, in order.</summary>
internal interface ISlotSource
{
    long NextSlot();
}

/// <summary>
/// Splits an image's slots into the 128 salt slots (positions fixed by a public key, so they can be found before
/// any password-derived key exists) and the data slots (positions from the password-derived permutation).
/// </summary>
internal sealed class SlotLayout
{
    public const int SaltBits = StegoEngine.SaltSize * 8;

    private static readonly byte[] SaltPermutationKey = SHA512.HashData("sweethide/v1/salt-slots"u8);

    private readonly HashSet<long> _saltSet;

    public SlotLayout(PixelBuffer image)
    {
        Map = new SlotMap(image);
        if (Map.Count > SaltBits)
        {
            var permutation = new FeistelPermutation(Map.Count, SaltPermutationKey);
            var saltSlots = new long[SaltBits];
            for (var i = 0; i < SaltBits; i++)
            {
                saltSlots[i] = permutation.Map(i);
            }

            SaltSlots = saltSlots;
        }
        else
        {
            SaltSlots = [];
        }

        _saltSet = [.. SaltSlots];
    }

    public SlotMap Map { get; }

    public IReadOnlyList<long> SaltSlots { get; }

    /// <summary>True when the image has room for at least the salt plus one data slot.</summary>
    public bool HasRoom => SaltSlots.Count == SaltBits;

    public long DataSlotCount => HasRoom ? Map.Count - SaltBits : 0;

    public ISlotSource SaltSource() => new ListSlotSource(SaltSlots);

    public ISlotSource DataSource(ReadOnlySpan<byte> permutationKey) =>
        new PermutedSlotSource(new FeistelPermutation(Map.Count, permutationKey), _saltSet);

    private sealed class ListSlotSource(IReadOnlyList<long> slots) : ISlotSource
    {
        private int _next;

        public long NextSlot() =>
            _next < slots.Count ? slots[_next++] : throw new InvalidOperationException("Ran out of salt slots.");
    }

    private sealed class PermutedSlotSource(FeistelPermutation permutation, HashSet<long> skip) : ISlotSource
    {
        private long _next;

        public long NextSlot()
        {
            while (_next < permutation.Size)
            {
                var slot = permutation.Map(_next++);
                if (!skip.Contains(slot))
                {
                    return slot;
                }
            }

            throw new InvalidOperationException("Ran out of data slots.");
        }
    }
}
