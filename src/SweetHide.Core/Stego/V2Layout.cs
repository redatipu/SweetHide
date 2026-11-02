namespace SweetHide.Core.Stego;

/// <summary>Which part of the data region a format v2 container occupies.</summary>
internal enum ContainerPlacement : byte
{
    /// <summary>The whole data region: normal password mode, recipients, Shamir shares.</summary>
    Whole = 0,

    /// <summary>The first half of the data region (deniable mode).</summary>
    FirstHalf = 1,

    /// <summary>The second half of the data region (deniable mode).</summary>
    SecondHalf = 2,
}

/// <summary>
/// Format v2 slot geometry of one carrier. A public Feistel permutation gives every slot a <em>rank</em>; ranks
/// 0..255 hold the 32 public bytes (salt or ephemeral key), then an optional recipient table, then the data region.
/// </summary>
/// <remarks>
/// The public order uses the same key as v1's salt slots, so ranks 0..127 are exactly where a v1 image keeps its salt.
/// The public order only scatters the fixed regions over the image; every container walks the data region in its
/// own key-dependent order (<see cref="SlotSequence"/>), so without a key nobody knows which slots it used.
/// </remarks>
internal sealed class V2Layout
{
    public const int PublicBytes = 32;
    public const int PublicSlots = PublicBytes * 8;

    private readonly FeistelPermutation? _order;

    public V2Layout(IStegoCarrier carrier)
    {
        Carrier = carrier;
        SlotCount = carrier.SlotCount;
        if (SlotCount > PublicSlots)
        {
            _order = new FeistelPermutation(SlotCount, SlotLayout.SaltPermutationKey);
        }
    }

    public IStegoCarrier Carrier { get; }

    public long SlotCount { get; }

    /// <summary>True when the carrier has room for the public region plus at least one data slot.</summary>
    public bool HasPublicRegion => _order is not null;

    public long SlotAtRank(long rank) => _order!.Map(rank);

    /// <summary>Reads <c>destination.Length</c> bytes, 1 bit per slot, from consecutive ranks.</summary>
    public void ReadRanks(long firstRank, Span<byte> destination) =>
        new SlotReader(Carrier, new RankSource(this, firstRank), 1).ReadBytes(destination);

    /// <summary>Writes bytes, 1 bit per slot, to consecutive ranks.</summary>
    public void WriteRanks(long firstRank, ReadOnlySpan<byte> data, RandomBits random) =>
        SlotWriter.Write(Carrier, new RankSource(this, firstRank), data, 1, random);

    /// <summary>The container order for one placement, from <paramref name="dataStart"/> (first data rank) on.</summary>
    public SlotSequence Sequence(long dataStart, ContainerPlacement placement, ReadOnlySpan<byte> permutationKey)
    {
        var data = Math.Max(0, SlotCount - dataStart);
        var firstHalf = data / 2;
        var (baseRank, length) = placement switch
        {
            ContainerPlacement.Whole => (dataStart, data),
            ContainerPlacement.FirstHalf => (dataStart, firstHalf),
            ContainerPlacement.SecondHalf => (dataStart + firstHalf, data - firstHalf),
            _ => throw new ArgumentOutOfRangeException(nameof(placement)),
        };
        return new SlotSequence(this, baseRank, length, permutationKey);
    }

    /// <summary>Number of data slots a placement covers (no key needed).</summary>
    public long PlacementLength(long dataStart, ContainerPlacement placement)
    {
        var data = Math.Max(0, SlotCount - dataStart);
        return placement switch
        {
            ContainerPlacement.Whole => data,
            ContainerPlacement.FirstHalf => data / 2,
            _ => data - (data / 2),
        };
    }

    private sealed class RankSource(V2Layout layout, long firstRank) : ISlotSource
    {
        private long _next = firstRank;

        public long NextSlot() => layout.SlotAtRank(_next++);
    }
}

/// <summary>
/// One container's walk through a range of ranks: element <c>k</c> is slot <c>order(base + P_key(k))</c>, where
/// <c>P_key</c> is a Feistel permutation of the range keyed by the container's permutation key. Random access, so
/// slots can be computed in parallel.
/// </summary>
internal sealed class SlotSequence
{
    private readonly V2Layout _layout;
    private readonly long _baseRank;
    private readonly FeistelPermutation? _permutation;

    public SlotSequence(V2Layout layout, long baseRank, long length, ReadOnlySpan<byte> permutationKey)
    {
        _layout = layout;
        _baseRank = baseRank;
        Length = length;
        if (length > 0)
        {
            _permutation = new FeistelPermutation(length, permutationKey);
        }
    }

    public long Length { get; }

    public long SlotAt(long element) => _layout.SlotAtRank(_baseRank + _permutation!.Map(element));
}
