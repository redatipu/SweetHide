namespace SweetHide.Core.Robust;

/// <summary>
/// Where every bit goes for an image of a given size, a key and a profile.
/// </summary>
/// <remarks>
/// A <em>physical slot</em> is one coefficient of one 8x8 luminance block: block <c>s / k</c>, the <c>(s % k)</c>-th
/// coefficient the key picked for that block from <see cref="Band"/> (k = coefficients per block). Each physical
/// slot has a keyed dither in [0, step). A keyed permutation maps <em>logical slots</em> to physical ones; logical
/// slots are used in order for the sync pattern, the header and the body, each bit repeated round-robin. The
/// permutation is the interleaver: repetitions of a bit, and neighbouring bytes of a codeword, land in unrelated
/// parts of the image, so a damaged region costs a little of everything instead of all of something.
/// </remarks>
internal sealed class RobustLayout
{
    public const int SyncBits = 64;
    public const int HeaderDataBytes = 4;
    public const int HeaderParityBytes = 8;
    public const int HeaderBits = (HeaderDataBytes + HeaderParityBytes) * 8;
    public const int CrcBytes = 4;
    public const int MinParity = 4;

    /// <summary>
    /// Candidate coefficients (natural order): zigzag positions 3–9, the low-mid frequencies (1,1), (0,2), (2,0),
    /// (0,3), (1,2), (2,1), (3,0). Low enough that JPEG quantizes them finely and resizing barely touches them, high
    /// enough that a change spreads as fine texture rather than visible blockiness.
    /// </summary>
    public static ReadOnlySpan<byte> Band => [16, 9, 2, 3, 10, 17, 24];

    private readonly int[] _logicalToPhysical;

    public RobustLayout(int width, int height, ReadOnlySpan<byte> key, RobustProfile profile)
    {
        Profile = profile;
        Width = width;
        Height = height;
        BlocksWide = width / 8;
        BlocksHigh = height / 8;
        var perBlock = profile.CoefficientsPerBlock;
        SlotCount = BlocksWide * BlocksHigh * perBlock;
        SyncSlotCount = SyncBits * profile.HeaderRepetition;
        HeaderSlotCount = SyncSlotCount + (HeaderBits * profile.HeaderRepetition);
        BodySlotCount = Math.Max(0, SlotCount - HeaderSlotCount);

        var random = new KeyedRandom(key, $"sweethide/robust/v1/{profile.Name}");
        Coefficient = new byte[SlotCount];
        Dither = new float[SlotCount];
        Span<byte> band = stackalloc byte[Band.Length];
        for (var block = 0; block < BlocksWide * BlocksHigh; block++)
        {
            // Partial Fisher–Yates: a keyed choice of distinct coefficients for this block.
            Band.CopyTo(band);
            for (var j = 0; j < perBlock; j++)
            {
                var pick = j + random.Next(band.Length - j);
                (band[j], band[pick]) = (band[pick], band[j]);
                var slot = (block * perBlock) + j;
                Coefficient[slot] = band[j];
                Dither[slot] = (float)(random.NextDouble() * profile.Step);
            }
        }

        _logicalToPhysical = random.Permutation(SlotCount);
        SyncPattern = new byte[SyncBits];
        for (var i = 0; i < SyncBits; i++)
        {
            SyncPattern[i] = random.NextBit() ? (byte)1 : (byte)0;
        }
    }

    public RobustProfile Profile { get; }

    public int Width { get; }

    public int Height { get; }

    public int BlocksWide { get; }

    public int BlocksHigh { get; }

    public int SlotCount { get; }

    public int SyncSlotCount { get; }

    /// <summary>Sync plus header slots.</summary>
    public int HeaderSlotCount { get; }

    public int BodySlotCount { get; }

    /// <summary>Natural coefficient index per physical slot.</summary>
    public byte[] Coefficient { get; }

    /// <summary>QIM dither per physical slot, in [0, step).</summary>
    public float[] Dither { get; }

    /// <summary>The keyed sync bits.</summary>
    public byte[] SyncPattern { get; }

    public int Physical(int logical) => _logicalToPhysical[logical];

    /// <summary>Reed–Solomon codewords (data, parity) for a payload of this many bytes, CRC included.</summary>
    public static (int Data, int Parity)[] Codewords(int payloadLength, int parityPercent)
    {
        var total = payloadLength + CrcBytes;
        var maxData = ReedSolomon.MaxCodewordLength;
        while (maxData + Parity(maxData, parityPercent) > ReedSolomon.MaxCodewordLength)
        {
            maxData--;
        }

        var count = (total + maxData - 1) / maxData;
        var result = new (int, int)[count];
        for (var i = 0; i < count; i++)
        {
            var data = (total / count) + (i < total % count ? 1 : 0);
            result[i] = (data, Parity(data, parityPercent));
        }

        return result;
    }

    public static int BodyBits(int payloadLength, int parityPercent) =>
        Codewords(payloadLength, parityPercent).Sum(c => c.Data + c.Parity) * 8;

    /// <summary>Largest payload whose body still gets <see cref="RobustProfile.MinRepetition"/> copies of every bit.</summary>
    public int MaxPayloadBytes()
    {
        bool Fits(int n) => (long)BodyBits(n, Profile.ParityPercent) * Profile.MinRepetition <= BodySlotCount;
        if (!Fits(0))
        {
            return 0;
        }

        int low = 0, high = RobustStego.MaxPayloadLength;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (Fits(mid))
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }

    private static int Parity(int data, int parityPercent) => Math.Max(MinParity, ((data * parityPercent) + 99) / 100);
}
