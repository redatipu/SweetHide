namespace SweetHide.Core.Stego;

/// <summary>
/// LSB matching (a.k.a. ±1 embedding), generalised to k low bits: move the sample to the nearest value whose low
/// bits equal the target, breaking ties at random. Unlike plain LSB replacement this does not create the
/// pairs-of-values histogram artefact that the chi-square attack looks for.
/// </summary>
internal static class LsbMatching
{
    public static byte Embed(byte value, int target, int bits, bool coin)
    {
        var modulus = 1 << bits;
        var low = value & (modulus - 1);
        if (low == target)
        {
            return value;
        }

        var down = value - ((low - target + modulus) % modulus); // largest candidate <= value
        var up = down + modulus;                                  // smallest candidate > value

        if (down < 0)
        {
            return (byte)up;
        }

        if (up > 255)
        {
            return (byte)down;
        }

        var distanceDown = value - down;
        var distanceUp = up - value;
        if (distanceDown != distanceUp)
        {
            return (byte)(distanceDown < distanceUp ? down : up);
        }

        return (byte)(coin ? up : down);
    }
}
