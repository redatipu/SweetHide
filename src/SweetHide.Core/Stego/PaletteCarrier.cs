using SweetHide.Core.Imaging;

namespace SweetHide.Core.Stego;

/// <summary>
/// One bit per pixel of a palette image, carried by the parity of the pixel's entry in a colour chain built from the
/// palette, in the spirit of EzStego.
/// </summary>
/// <remarks>
/// <para>
/// The palette itself is never changed, only which entry each pixel points at. The opaque entries are put in a
/// chain where neighbours look alike: start at the darkest colour and keep stepping to the nearest colour not yet
/// used. A pixel's bit is the parity of its entry's position in that chain, and writing moves the pixel one step
/// along the chain, to whichever neighbour is closer in colour (<c>coin</c> only breaks exact ties).
/// </para>
/// <para>
/// A pixel is a slot only if its entry has a chain neighbour within <see cref="MaxStepDistance"/>, so a bit can
/// always be flipped without a visible jump. That rule depends on the palette alone, and a step only ever goes to
/// such a neighbour (which in turn has the original entry as its own near neighbour), so the reader finds exactly
/// the same slots. Pixels whose entry is not fully opaque are never used.
/// </para>
/// </remarks>
internal sealed class PaletteCarrier : IStegoCarrier
{
    /// <summary>Largest RGB distance (Euclidean, 0-441) a pixel may move when a bit is written.</summary>
    public const double MaxStepDistance = 48;

    private readonly byte[] _indices;

    // Chain position of each palette entry, or -1 for entries that are not in the chain (translucent).
    private readonly int[] _position;

    // Palette entry at each chain position.
    private readonly int[] _chain;

    // Colour distance from each chain position to the next one.
    private readonly double[] _distanceToNext;

    // Pixel index of each slot.
    private readonly int[] _pixels;

    /// <param name="image">Writes go straight into this image's indices.</param>
    public PaletteCarrier(IndexedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        _indices = image.Indices;
        var palette = image.Palette;

        _chain = BuildChain(palette);
        _position = new int[palette.Count];
        Array.Fill(_position, -1);
        for (var p = 0; p < _chain.Length; p++)
        {
            _position[_chain[p]] = p;
        }

        _distanceToNext = new double[Math.Max(0, _chain.Length - 1)];
        for (var p = 0; p + 1 < _chain.Length; p++)
        {
            _distanceToNext[p] = Distance(palette[_chain[p]], palette[_chain[p + 1]]);
        }

        var usableEntry = new bool[palette.Count];
        for (var p = 0; p < _chain.Length; p++)
        {
            usableEntry[_chain[p]] = CanStepBack(p) || CanStepForward(p);
        }

        var count = 0;
        foreach (var index in _indices)
        {
            if (usableEntry[index])
            {
                count++;
            }
        }

        _pixels = new int[count];
        var n = 0;
        for (var i = 0; i < _indices.Length; i++)
        {
            if (usableEntry[_indices[i]])
            {
                _pixels[n++] = i;
            }
        }
    }

    public long SlotCount => _pixels.Length;

    public int MaxBitsPerSlot => 1;

    /// <summary>Pixels that can carry a bit.</summary>
    public long UsablePixels => _pixels.Length;

    public int Read(long slot, int bits)
    {
        RequireOneBit(bits);
        return _position[_indices[_pixels[slot]]] & 1;
    }

    public void Write(long slot, int value, int bits, bool coin)
    {
        RequireOneBit(bits);
        var pixel = _pixels[slot];
        var p = _position[_indices[pixel]];
        if ((p & 1) == (value & 1))
        {
            return;
        }

        var back = CanStepBack(p);
        var forward = CanStepForward(p);
        int step;
        if (back && forward)
        {
            var dBack = _distanceToNext[p - 1];
            var dForward = _distanceToNext[p];
            step = dBack < dForward ? -1 : dForward < dBack ? 1 : (coin ? 1 : -1);
        }
        else
        {
            step = forward ? 1 : -1;
        }

        _indices[pixel] = (byte)_chain[p + step];
    }

    private bool CanStepBack(int p) => p > 0 && _distanceToNext[p - 1] <= MaxStepDistance;

    private bool CanStepForward(int p) => p + 1 < _chain.Length && _distanceToNext[p] <= MaxStepDistance;

    /// <summary>Opaque entries, darkest first, then greedily the nearest unused colour (ties: lower index).</summary>
    private static int[] BuildChain(IReadOnlyList<uint> palette)
    {
        var opaque = new List<int>();
        for (var i = 0; i < palette.Count; i++)
        {
            if (palette[i] >> 24 == 0xFF)
            {
                opaque.Add(i);
            }
        }

        if (opaque.Count == 0)
        {
            return [];
        }

        var chain = new int[opaque.Count];
        var used = new bool[palette.Count];
        var current = opaque.MinBy(i => (Luma(palette[i]), i));
        chain[0] = current;
        used[current] = true;
        for (var k = 1; k < chain.Length; k++)
        {
            var best = -1;
            var bestDistance = double.MaxValue;
            foreach (var candidate in opaque)
            {
                if (used[candidate])
                {
                    continue;
                }

                var d = Distance(palette[current], palette[candidate]);
                if (d < bestDistance)
                {
                    best = candidate;
                    bestDistance = d;
                }
            }

            chain[k] = best;
            used[best] = true;
            current = best;
        }

        return chain;
    }

    private static int Luma(uint argb) =>
        (299 * (int)((argb >> 16) & 0xFF)) + (587 * (int)((argb >> 8) & 0xFF)) + (114 * (int)(argb & 0xFF));

    private static double Distance(uint a, uint b)
    {
        var dr = (int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF);
        var dg = (int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF);
        var db = (int)(a & 0xFF) - (int)(b & 0xFF);
        return Math.Sqrt((dr * dr) + (dg * dg) + (db * db));
    }

    private static void RequireOneBit(int bits)
    {
        if (bits != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(bits), bits, "Palette images carry one bit per pixel.");
        }
    }
}
