namespace Magi.Core.Stego;

/// <summary>Writes a byte stream into slots, <c>bitsPerSlot</c> bits per slot, most significant bit first.</summary>
internal static class SlotWriter
{
    public static void Write(byte[] pixels, SlotMap map, ISlotSource slots, ReadOnlySpan<byte> data, int bitsPerSlot, RandomBits random)
    {
        var totalBits = (long)data.Length * 8;
        var groups = (totalBits + bitsPerSlot - 1) / bitsPerSlot;
        long bitIndex = 0;
        for (long g = 0; g < groups; g++)
        {
            var target = 0;
            for (var j = 0; j < bitsPerSlot; j++, bitIndex++)
            {
                // The final slot may be only partly used; pad it with random bits rather than a constant.
                var bit = bitIndex < totalBits
                    ? (data[(int)(bitIndex >> 3)] >> (7 - (int)(bitIndex & 7))) & 1
                    : (random.Next() ? 1 : 0);
                target = (target << 1) | bit;
            }

            var offset = map.ByteOffset(slots.NextSlot());
            pixels[offset] = LsbMatching.Embed(pixels[offset], target, bitsPerSlot, random.Next());
        }
    }
}

/// <summary>Reads back what <see cref="SlotWriter"/> wrote. Keeps leftover bits between calls.</summary>
internal sealed class SlotReader(byte[] pixels, SlotMap map, ISlotSource slots, int bitsPerSlot)
{
    private readonly int _mask = (1 << bitsPerSlot) - 1;
    private int _pending;
    private int _pendingCount;

    public void ReadBytes(Span<byte> destination)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            var value = 0;
            for (var j = 0; j < 8; j++)
            {
                if (_pendingCount == 0)
                {
                    _pending = pixels[map.ByteOffset(slots.NextSlot())] & _mask;
                    _pendingCount = bitsPerSlot;
                }

                _pendingCount--;
                value = (value << 1) | ((_pending >> _pendingCount) & 1);
            }

            destination[i] = (byte)value;
        }
    }
}
