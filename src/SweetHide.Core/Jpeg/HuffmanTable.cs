using SweetHide.Core.Imaging;

namespace SweetHide.Core.Jpeg;

/// <summary>A JPEG Huffman table (T.81 Annex C), usable for both decoding and encoding.</summary>
internal sealed class HuffmanTable
{
    private const int LookaheadBits = 9;

    private readonly byte[] _bits;
    private readonly byte[] _values;

    // Encoding: code and length per symbol (length 0 = symbol not in the table).
    private readonly ushort[] _codes = new ushort[256];
    private readonly byte[] _sizes = new byte[256];

    // Decoding (Annex F.2.2.3): fast path for codes up to LookaheadBits long, slow path per length.
    private readonly ushort[] _lookahead = new ushort[1 << LookaheadBits]; // (length << 8) | symbol, 0 = longer code
    private readonly int[] _maxCode = new int[18];
    private readonly int[] _valueOffset = new int[17];

    private HuffmanTable(byte[] bits, byte[] values)
    {
        _bits = bits;
        _values = values;

        var code = 0;
        var k = 0;
        for (var length = 1; length <= 16; length++)
        {
            int count = bits[length - 1];
            _valueOffset[length] = k - code;
            _maxCode[length] = count == 0 ? -1 : code + count - 1;
            for (var i = 0; i < count; i++, k++, code++)
            {
                var symbol = values[k];
                if (_sizes[symbol] != 0)
                {
                    throw new InvalidImageException("Corrupt JPEG: a Huffman table lists the same symbol twice.");
                }

                _codes[symbol] = (ushort)code;
                _sizes[symbol] = (byte)length;
                if (length <= LookaheadBits)
                {
                    // Every LookaheadBits-bit pattern that starts with this code decodes to it.
                    var shift = LookaheadBits - length;
                    var first = code << shift;
                    for (var fill = 0; fill < 1 << shift; fill++)
                    {
                        _lookahead[first + fill] = (ushort)((length << 8) | symbol);
                    }
                }
            }

            // Like libjpeg: the all-ones code of every length is reserved, so the code space may never fill up.
            if (code >= 1 << length)
            {
                throw new InvalidImageException("Corrupt JPEG: invalid Huffman table.");
            }

            code <<= 1;
        }

        _maxCode[17] = int.MaxValue;
    }

    public static HuffmanTable StandardDcLuminance { get; } = Create(JpegTables.DcLuminanceBits, JpegTables.DcLuminanceValues);

    public static HuffmanTable StandardDcChrominance { get; } = Create(JpegTables.DcChrominanceBits, JpegTables.DcChrominanceValues);

    public static HuffmanTable StandardAcLuminance { get; } = Create(JpegTables.AcLuminanceBits, JpegTables.AcLuminanceValues);

    public static HuffmanTable StandardAcChrominance { get; } = Create(JpegTables.AcChrominanceBits, JpegTables.AcChrominanceValues);

    /// <summary>Counts of codes of length 1..16.</summary>
    public ReadOnlySpan<byte> Bits => _bits;

    /// <summary>Symbols in order of increasing code length.</summary>
    public ReadOnlySpan<byte> Values => _values;

    /// <exception cref="InvalidImageException">The table is malformed.</exception>
    public static HuffmanTable Create(ReadOnlySpan<byte> bits, ReadOnlySpan<byte> values)
    {
        if (bits.Length != 16)
        {
            throw new ArgumentException("Expected 16 code counts.", nameof(bits));
        }

        var total = 0;
        foreach (var count in bits)
        {
            total += count;
        }

        if (total > 256 || total != values.Length)
        {
            throw new InvalidImageException("Corrupt JPEG: invalid Huffman table size.");
        }

        return new HuffmanTable(bits.ToArray(), values.ToArray());
    }

    /// <summary>
    /// Builds an optimal length-limited table for the given symbol frequencies, exactly as libjpeg's
    /// <c>jpeg_gen_optimal_table</c> (T.81 Annex K.2): Huffman's algorithm, then lengths over 16 are folded down
    /// and the reserved all-ones code is removed.
    /// </summary>
    public static HuffmanTable Optimal(ReadOnlySpan<long> frequencies)
    {
        if (frequencies.Length != 256)
        {
            throw new ArgumentException("Expected 256 frequencies.", nameof(frequencies));
        }

        var scaled = frequencies.ToArray();
        if (Array.TrueForAll(scaled, f => f == 0))
        {
            scaled[0] = 1; // An unused table still needs one valid code.
        }

        int[] codeSize;
        while (!TryCodeSizes(scaled, out codeSize))
        {
            // Pathological (Fibonacci-like) statistics can need codes over 32 bits; flatten them and retry.
            for (var i = 0; i < scaled.Length; i++)
            {
                scaled[i] = scaled[i] == 0 ? 0 : (scaled[i] + 1) / 2;
            }
        }

        var bits = new int[33];
        for (var i = 0; i <= 256; i++)
        {
            if (codeSize[i] > 0)
            {
                bits[codeSize[i]]++;
            }
        }

        for (var i = 32; i > 16; i--)
        {
            while (bits[i] > 0)
            {
                var j = i - 2;
                while (bits[j] == 0)
                {
                    j--;
                }

                bits[i] -= 2;
                bits[i - 1]++;
                bits[j + 1] += 2;
                bits[j]--;
            }
        }

        var last = 16;
        while (bits[last] == 0)
        {
            last--;
        }

        bits[last]--; // Drop the reserved code point again.

        var values = new List<byte>();
        for (var length = 1; length <= 32; length++)
        {
            for (var symbol = 0; symbol < 256; symbol++)
            {
                if (codeSize[symbol] == length)
                {
                    values.Add((byte)symbol);
                }
            }
        }

        var counts = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            counts[i] = (byte)bits[i + 1];
        }

        return new HuffmanTable(counts, [.. values]);
    }

    private static bool TryCodeSizes(ReadOnlySpan<long> frequencies, out int[] codeSize)
    {
        var freq = new long[257];
        frequencies.CopyTo(freq);
        freq[256] = 1; // Reserves one code point so no real symbol gets the all-ones code.
        codeSize = new int[257];
        var others = new int[257];
        Array.Fill(others, -1);

        while (true)
        {
            // c1 = least frequent symbol, c2 = next least; ties go to the larger index (as in libjpeg).
            var c1 = -1;
            var v = long.MaxValue;
            for (var i = 0; i <= 256; i++)
            {
                if (freq[i] != 0 && freq[i] <= v)
                {
                    v = freq[i];
                    c1 = i;
                }
            }

            var c2 = -1;
            v = long.MaxValue;
            for (var i = 0; i <= 256; i++)
            {
                if (freq[i] != 0 && freq[i] <= v && i != c1)
                {
                    v = freq[i];
                    c2 = i;
                }
            }

            if (c2 < 0)
            {
                break;
            }

            freq[c1] += freq[c2];
            freq[c2] = 0;

            codeSize[c1]++;
            while (others[c1] >= 0)
            {
                c1 = others[c1];
                codeSize[c1]++;
            }

            others[c1] = c2;

            codeSize[c2]++;
            while (others[c2] >= 0)
            {
                c2 = others[c2];
                codeSize[c2]++;
            }
        }

        foreach (var size in codeSize)
        {
            if (size > 32)
            {
                return false;
            }
        }

        return true;
    }

    public bool Contains(int symbol) => _sizes[symbol] != 0;

    public void Encode(JpegBitWriter writer, int symbol)
    {
        var size = _sizes[symbol];
        if (size == 0)
        {
            throw new InvalidOperationException($"Symbol 0x{symbol:X2} is not in the Huffman table.");
        }

        writer.WriteBits(_codes[symbol], size);
    }

    public int Decode(ref JpegBitReader reader)
    {
        var peek = reader.Peek16();
        var entry = _lookahead[peek >> (16 - LookaheadBits)];
        if (entry != 0)
        {
            reader.Skip(entry >> 8);
            return entry & 0xFF;
        }

        var length = LookaheadBits + 1;
        var code = peek >> (16 - length);
        while (code > _maxCode[length])
        {
            length++;
            if (length > 16)
            {
                throw new InvalidImageException("Corrupt JPEG: bad Huffman code.");
            }

            code = peek >> (16 - length);
        }

        reader.Skip(length);
        return _values[_valueOffset[length] + code];
    }
}
