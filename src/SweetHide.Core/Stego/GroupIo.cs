namespace SweetHide.Core.Stego;

/// <summary>
/// Moves a byte string in and out of slots as <c>bits</c>-bit groups, most significant bit first, one group per slot,
/// using all cores.
/// </summary>
/// <remarks>
/// Parallelism never changes the result. Each group's slot is a pure function of its index, so no two work items touch
/// the same slot; coin flips and padding are drawn up front on the calling thread; and read work items cover whole
/// bytes of the output. Filtered walks (adaptive bodies) scan a wave of elements in parallel, then hand out group
/// numbers in element order before writing, so the same slots get the same groups however the threads ran.
/// </remarks>
internal static class GroupIo
{
    // A multiple of 8, so every work item starts on a byte boundary of the data.
    private const int ChunkGroups = 1 << 14;
    private const int ChunkElements = 1 << 14;

    public static long GroupCount(long bytes, int bits) => ((bytes * 8) + bits - 1) / bits;

    /// <summary>Writes every group of <paramref name="data"/> to <c>slotOf(group)</c>.</summary>
    public static void Write(IStegoCarrier carrier, Func<long, long> slotOf, byte[] data, int bits, IRandomSource random)
    {
        var groups = GroupCount(data.LongLength, bits);
        var coins = RandomBytes(random, (groups + 7) / 8);
        var padding = RandomBytes(random, 1)[0];
        Parallel.For(0L, ChunkCount(groups), chunk =>
        {
            var end = Math.Min(groups, (chunk + 1) * ChunkGroups);
            for (var g = chunk * ChunkGroups; g < end; g++)
            {
                carrier.Write(slotOf(g), GroupValue(data, padding, g, bits), bits, Coin(coins, g));
            }
        });
    }

    /// <summary>Fills <paramref name="destination"/> from <c>slotOf(group)</c>.</summary>
    public static void Read(IStegoCarrier carrier, Func<long, long> slotOf, byte[] destination, int bits)
    {
        Array.Clear(destination);
        var groups = GroupCount(destination.LongLength, bits);
        Parallel.For(0L, ChunkCount(groups), chunk =>
        {
            var end = Math.Min(groups, (chunk + 1) * ChunkGroups);
            for (var g = chunk * ChunkGroups; g < end; g++)
            {
                PutGroup(destination, g, bits, carrier.Read(slotOf(g), bits));
            }
        });
    }

    /// <summary>Writes along <paramref name="sequence"/> from element <paramref name="start"/>, into accepted slots only.</summary>
    /// <returns>False if the sequence ran out of accepted slots (nothing useful was written then).</returns>
    public static bool WriteFiltered(
        IStegoCarrier carrier, SlotSequence sequence, long start, Func<long, bool> accept, byte[] data, int bits, IRandomSource random)
    {
        var groups = GroupCount(data.LongLength, bits);
        var coins = RandomBytes(random, (groups + 7) / 8);
        var padding = RandomBytes(random, 1)[0];
        return Scan(
            Workers(),
            sequence,
            start,
            accept,
            groups,
            (slots, count, firstGroup) =>
            {
                for (var i = 0; i < count; i++)
                {
                    var g = firstGroup + i;
                    carrier.Write(slots[i], GroupValue(data, padding, g, bits), bits, Coin(coins, g));
                }
            },
            afterWave: null);
    }

    /// <summary>Reads what <see cref="WriteFiltered"/> wrote.</summary>
    /// <returns>False if the sequence ran out of accepted slots.</returns>
    public static bool ReadFiltered(
        IStegoCarrier carrier, SlotSequence sequence, long start, Func<long, bool> accept, byte[] destination, int bits)
    {
        Array.Clear(destination);
        var groups = GroupCount(destination.LongLength, bits);
        var workers = Workers();
        var values = new byte[workers * ChunkElements];
        long waveFirst = 0;
        return Scan(
            workers,
            sequence,
            start,
            accept,
            groups,
            (slots, count, firstGroup) =>
            {
                var offset = (int)(firstGroup - waveFirst);
                for (var i = 0; i < count; i++)
                {
                    values[offset + i] = (byte)carrier.Read(slots[i], bits);
                }
            },
            afterWave: (first, end) =>
            {
                for (var g = first; g < end; g++)
                {
                    PutGroup(destination, g, bits, values[g - first]);
                }

                waveFirst = end;
            });
    }

    /// <summary>Counts accepted elements of a sequence by texture: <c>histogram[t]</c> = elements with texture t.</summary>
    public static long[] TextureHistogram(IAdaptiveCarrier carrier, SlotSequence sequence, long start)
    {
        var histogram = new long[256];
        var elements = Math.Max(0, sequence.Length - start);
        var gate = new object();
        Parallel.For(
            0L,
            (elements + ChunkElements - 1) / ChunkElements,
            () => new long[256],
            (chunk, _, local) =>
            {
                var end = Math.Min(sequence.Length, start + ((chunk + 1) * ChunkElements));
                for (var e = start + (chunk * ChunkElements); e < end; e++)
                {
                    var texture = carrier.Texture(sequence.SlotAt(e));
                    if (texture >= 0)
                    {
                        local[texture]++;
                    }
                }

                return local;
            },
            local =>
            {
                lock (gate)
                {
                    for (var t = 0; t < local.Length; t++)
                    {
                        histogram[t] += local[t];
                    }
                }
            });
        return histogram;
    }

    private static bool Scan(
        int workers,
        SlotSequence sequence,
        long start,
        Func<long, bool> accept,
        long groups,
        Action<long[], int, long> process,
        Action<long, long>? afterWave)
    {
        var lists = new long[workers][];
        for (var w = 0; w < workers; w++)
        {
            lists[w] = new long[ChunkElements];
        }

        var counts = new int[workers];
        var firstGroups = new long[workers];
        var element = start;
        long done = 0;
        while (done < groups)
        {
            if (element >= sequence.Length)
            {
                return false;
            }

            var waveStart = element;
            Parallel.For(0, workers, w =>
            {
                var from = waveStart + ((long)w * ChunkElements);
                var to = Math.Min(from + ChunkElements, sequence.Length);
                var list = lists[w];
                var n = 0;
                for (var e = from; e < to; e++)
                {
                    var slot = sequence.SlotAt(e);
                    if (accept(slot))
                    {
                        list[n++] = slot;
                    }
                }

                counts[w] = n;
            });
            element = Math.Min(waveStart + ((long)workers * ChunkElements), sequence.Length);

            var waveFirst = done;
            for (var w = 0; w < workers; w++)
            {
                firstGroups[w] = done;
                counts[w] = (int)Math.Min(counts[w], groups - done);
                done += counts[w];
            }

            Parallel.For(0, workers, w => process(lists[w], counts[w], firstGroups[w]));
            afterWave?.Invoke(waveFirst, done);
        }

        return true;
    }

    private static long ChunkCount(long groups) => (groups + ChunkGroups - 1) / ChunkGroups;

    // Work items per wave of a filtered walk; the wave's results are still assigned in element order.
    private static int Workers() => Math.Max(1, Environment.ProcessorCount);

    private static byte[] RandomBytes(IRandomSource random, long count)
    {
        var bytes = new byte[count];
        random.Fill(bytes);
        return bytes;
    }

    private static bool Coin(byte[] coins, long group) => ((coins[group >> 3] >> (int)(group & 7)) & 1) == 1;

    /// <summary>Bits <c>[g*bits, (g+1)*bits)</c> of the data; bits past the end come from <paramref name="padding"/>.</summary>
    private static int GroupValue(byte[] data, byte padding, long group, int bits)
    {
        var totalBits = data.LongLength * 8;
        var bit = group * bits;
        var value = 0;
        for (var j = 0; j < bits; j++, bit++)
        {
            var b = bit < totalBits
                ? (data[bit >> 3] >> (7 - (int)(bit & 7))) & 1
                : (padding >> (int)(bit - totalBits)) & 1;
            value = (value << 1) | b;
        }

        return value;
    }

    private static void PutGroup(byte[] destination, long group, int bits, int value)
    {
        var totalBits = destination.LongLength * 8;
        var bit = group * bits;
        for (var j = bits - 1; j >= 0 && bit < totalBits; j--, bit++)
        {
            if (((value >> j) & 1) != 0)
            {
                destination[bit >> 3] |= (byte)(0x80 >> (int)(bit & 7));
            }
        }
    }
}
