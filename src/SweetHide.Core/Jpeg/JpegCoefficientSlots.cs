using System.Diagnostics.CodeAnalysis;

namespace SweetHide.Core.Jpeg;

/// <summary>
/// The JPEG coefficients that may carry one bit each: every AC coefficient with |c| ≥ 2, in every component.
/// </summary>
/// <remarks>
/// <para>
/// <b>Invariant: the usable set never changes.</b> <see cref="Write(long, int, bool)"/> only moves a coefficient by
/// ±1 and never takes |c| below 2: a coefficient at ±2 that needs a different parity goes to ±3 (away from zero),
/// one at ±1023 (the baseline limit) goes to ±1022, anything else moves either way by the coin. Coefficients that are
/// 0 or ±1 are never touched and nothing is ever moved onto them, so building a new
/// <see cref="JpegCoefficientSlots"/> from the stego coefficients finds exactly the same slots in the same order.
/// This avoids the "shrinkage" problem of F5-style embedding without needing wet-paper codes.
/// </para>
/// <para>
/// Slot order: component by component (frame order), blocks in raster order within the region every encoder
/// writes (<see cref="JpegComponent.EncodedBlocksWide"/> × <see cref="JpegComponent.EncodedBlocksHigh"/>, so MCU padding
/// blocks are never used), and natural coefficient order 1–63 within a block. DC coefficients are never used.
/// </para>
/// <para>
/// The bit is the parity of the coefficient (<c>c &amp; 1</c>, which equals <c>|c| mod 2</c>). Writes to distinct
/// slots touch distinct coefficients, so they may run concurrently. Shaped to fit a carrier interface of the form
/// <c>SlotCount / BitsPerSlot / Read(slot) / Write(slot, target, coin)</c>, with <c>(slot, bits)</c> overloads for
/// carriers that take a bit count.
/// </para>
/// <para>
/// This is the fragile, high-capacity mode: any re-encoding of the JPEG destroys the message.
/// </para>
/// </remarks>
public sealed class JpegCoefficientSlots
{
    private const int MinimumMagnitude = 2;

    private readonly short[][] _data;
    private readonly int[][] _offsets; // Per component: offsets into that component's coefficient array.
    private readonly long[] _firstSlot; // Per component: index of its first slot.

    /// <param name="coefficients">Writes go straight into these coefficients.</param>
    public JpegCoefficientSlots(JpegCoefficients coefficients)
    {
        ArgumentNullException.ThrowIfNull(coefficients);
        var components = coefficients.Components;
        _data = new short[components.Count][];
        _offsets = new int[components.Count][];
        _firstSlot = new long[components.Count];
        long total = 0;
        for (var c = 0; c < components.Count; c++)
        {
            var component = components[c];
            var data = component.Data;
            var offsets = new List<int>();
            for (var by = 0; by < component.EncodedBlocksHigh; by++)
            {
                for (var bx = 0; bx < component.EncodedBlocksWide; bx++)
                {
                    var block = ((by * component.BlocksWide) + bx) * JpegTables.BlockSize;
                    for (var k = 1; k < JpegTables.BlockSize; k++)
                    {
                        if (IsUsable(data[block + k]))
                        {
                            offsets.Add(block + k);
                        }
                    }
                }
            }

            _data[c] = data;
            _offsets[c] = [.. offsets];
            _firstSlot[c] = total;
            total += offsets.Count;
        }

        SlotCount = total;
    }

    /// <summary>Number of usable coefficients, i.e. the capacity in bits.</summary>
    public long SlotCount { get; }

    /// <summary>Always 1: only the parity of a coefficient is used.</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance member so the type can implement a carrier interface.")]
    public int BitsPerSlot => 1;

    /// <summary>Same as <see cref="BitsPerSlot"/>, for carriers that support several bit depths.</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance member so the type can implement a carrier interface.")]
    public int MaxBitsPerSlot => 1;

    /// <summary>True for coefficient values that count as a slot (|c| ≥ 2).</summary>
    public static bool IsUsable(int value) => value is <= -MinimumMagnitude or >= MinimumMagnitude;

    /// <summary>The bit stored in a slot: the coefficient's parity.</summary>
    public int Read(long slot)
    {
        var (data, offset) = Locate(slot);
        return data[offset] & 1;
    }

    /// <summary><see cref="Read(long)"/> for carriers that pass a bit count; <paramref name="bits"/> must be 1.</summary>
    public int Read(long slot, int bits)
    {
        RequireOneBit(bits);
        return Read(slot);
    }

    /// <summary>
    /// Sets a slot's parity to <paramref name="target"/>, changing the coefficient by at most 1 and keeping |c| ≥ 2.
    /// <paramref name="coin"/> picks the direction when both are allowed (true = away from zero).
    /// </summary>
    public void Write(long slot, int target, bool coin)
    {
        if (target is not (0 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(target), target, "A slot holds a single bit.");
        }

        var (data, offset) = Locate(slot);
        int value = data[offset];
        if ((value & 1) == target)
        {
            return;
        }

        var magnitude = Math.Abs(value);
        var away = magnitude == MinimumMagnitude || (magnitude != JpegTables.MaxAcMagnitude && coin);
        var newMagnitude = away ? magnitude + 1 : magnitude - 1;
        data[offset] = (short)(value < 0 ? -newMagnitude : newMagnitude);
    }

    /// <summary><see cref="Write(long, int, bool)"/> for carriers that pass a bit count; <paramref name="bits"/> must be 1.</summary>
    public void Write(long slot, int value, int bits, bool coin)
    {
        RequireOneBit(bits);
        Write(slot, value, coin);
    }

    private static void RequireOneBit(int bits)
    {
        if (bits != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(bits), bits, "JPEG coefficient slots carry exactly one bit.");
        }
    }

    private (short[] Data, int Offset) Locate(long slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, SlotCount);
        var c = _firstSlot.Length - 1;
        while (_firstSlot[c] > slot)
        {
            c--;
        }

        return (_data[c], _offsets[c][slot - _firstSlot[c]]);
    }
}
