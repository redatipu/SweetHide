using SweetHide.Core.Imaging;

namespace SweetHide.Core.Jpeg;

/// <summary>Chroma subsampling for <see cref="JpegEncoder.Quantize"/>.</summary>
public enum JpegSubsampling
{
    /// <summary>Full-resolution chroma (all components 1x1).</summary>
    Yuv444 = 0,

    /// <summary>Half-resolution chroma both ways (Y 2x2, Cb/Cr 1x1): what cameras and chat apps produce.</summary>
    Yuv420 = 1,
}

/// <summary>Options for <see cref="JpegEncoder.Write"/>.</summary>
public sealed record JpegWriteOptions
{
    /// <summary>Two-pass encoding with Huffman tables built for this image (T.81 Annex K.2): smaller files, same coefficients.</summary>
    public bool OptimizeHuffmanTables { get; init; }

    /// <summary>MCUs between restart markers (DRI/RSTn); 0 writes none.</summary>
    public int RestartInterval { get; init; }

    /// <summary>
    /// Writes the 16-byte JFIF APP0 segment (version 1.01, 1:1 aspect, no thumbnail). It carries nothing about the
    /// photo or its author, and makes the colour space explicit for every decoder.
    /// </summary>
    public bool WriteJfifHeader { get; init; } = true;
}

/// <summary>
/// Baseline sequential JPEG encoder, split in two so coefficients can be changed in between:
/// <see cref="Quantize"/> (pixels to quantized DCT coefficients) and <see cref="Write"/> (coefficients to a JFIF
/// stream). Writes no metadata: no EXIF, no ICC profile, no comments.
/// </summary>
public static class JpegEncoder
{
    private const int MaxBlocksPerMcu = 10;

    /// <summary>
    /// Converts RGB to JFIF YCbCr (alpha is ignored, flatten transparent images first), applies the forward DCT and
    /// quantizes with the Annex K tables scaled as libjpeg does for <paramref name="quality"/> (1–100).
    /// </summary>
    public static JpegCoefficients Quantize(PixelBuffer image, int quality, JpegSubsampling subsampling = JpegSubsampling.Yuv420)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfLessThan(quality, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quality, 100);

        var lumaFactor = subsampling == JpegSubsampling.Yuv420 ? 2 : 1;
        var mcuSize = 8 * lumaFactor;
        var mcusWide = JpegCoefficients.DivideRoundingUp(image.Width, mcuSize);
        var mcusHigh = JpegCoefficients.DivideRoundingUp(image.Height, mcuSize);

        var luma = new JpegComponent(1, lumaFactor, lumaFactor, 0, mcusWide * lumaFactor, mcusHigh * lumaFactor);
        var cb = new JpegComponent(2, 1, 1, 1, mcusWide, mcusHigh);
        var cr = new JpegComponent(3, 1, 1, 1, mcusWide, mcusHigh);
        var tables = new ushort[]?[4];
        tables[0] = JpegTables.ScaleQuantization(JpegTables.StandardLuminanceQuantization, quality);
        tables[1] = JpegTables.ScaleQuantization(JpegTables.StandardChrominanceQuantization, quality);
        var result = new JpegCoefficients(image.Width, image.Height, [luma, cb, cr], tables);

        var pixels = image.Data;
        var width = image.Width;
        var height = image.Height;
        var stripWidth = mcusWide * mcuSize;
        Parallel.For(0, mcusHigh, mcuRow =>
        {
            // One MCU row at a time, in full resolution, with edges replicated into the padding.
            var y = new float[stripWidth * mcuSize];
            var u = new float[stripWidth * mcuSize];
            var v = new float[stripWidth * mcuSize];
            for (var row = 0; row < mcuSize; row++)
            {
                var sy = Math.Min((mcuRow * mcuSize) + row, height - 1);
                for (var x = 0; x < stripWidth; x++)
                {
                    var p = ((sy * width) + Math.Min(x, width - 1)) * 4;
                    float r = pixels[p], g = pixels[p + 1], b = pixels[p + 2];
                    var i = (row * stripWidth) + x;
                    y[i] = (0.299f * r) + (0.587f * g) + (0.114f * b) - 128f;
                    u[i] = (-0.168736f * r) - (0.331264f * g) + (0.5f * b);
                    v[i] = (0.5f * r) - (0.418688f * g) - (0.081312f * b);
                }
            }

            Span<float> samples = stackalloc float[64];
            Span<float> dct = stackalloc float[64];
            for (var by = 0; by < lumaFactor; by++)
            {
                for (var bx = 0; bx < mcusWide * lumaFactor; bx++)
                {
                    for (var i = 0; i < 64; i++)
                    {
                        samples[i] = y[(((by * 8) + (i >> 3)) * stripWidth) + (bx * 8) + (i & 7)];
                    }

                    BlockDct.Forward(samples, dct);
                    QuantizeBlock(dct, tables[0], luma.Block(bx, (mcuRow * lumaFactor) + by));
                }
            }

            foreach (var (plane, component) in new[] { (u, cb), (v, cr) })
            {
                for (var bx = 0; bx < mcusWide; bx++)
                {
                    for (var i = 0; i < 64; i++)
                    {
                        // Box-filter down to chroma resolution (a no-op for 4:4:4).
                        var sum = 0f;
                        for (var dy = 0; dy < lumaFactor; dy++)
                        {
                            for (var dx = 0; dx < lumaFactor; dx++)
                            {
                                var sy = (((i >> 3) * lumaFactor) + dy) * stripWidth;
                                sum += plane[sy + (((bx * 8) + (i & 7)) * lumaFactor) + dx];
                            }
                        }

                        samples[i] = sum / (lumaFactor * lumaFactor);
                    }

                    BlockDct.Forward(samples, dct);
                    QuantizeBlock(dct, tables[1], component.Block(bx, mcuRow));
                }
            }
        });

        return result;
    }

    /// <summary>Writes a baseline sequential JFIF stream holding exactly these coefficients.</summary>
    /// <exception cref="ArgumentException">The coefficients cannot be represented in a baseline 8-bit stream.</exception>
    public static void Write(JpegCoefficients coefficients, Stream output, JpegWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(coefficients);
        ArgumentNullException.ThrowIfNull(output);
        options ??= new JpegWriteOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(options.RestartInterval);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.RestartInterval, ushort.MaxValue);

        var components = coefficients.Components;
        if (components.Count is not (1 or 3))
        {
            throw new ArgumentException("Only 1 or 3 components can be written.", nameof(coefficients));
        }

        var usedTables = components.Select(c => c.QuantizationTableIndex).Distinct().Order().ToArray();
        var wide = false;
        foreach (var index in usedTables)
        {
            foreach (var q in coefficients.GetQuantizationTable(index))
            {
                wide |= q > 255;
            }
        }

        // One interleaved scan when allowed (≤ 10 blocks per MCU), else one scan per component.
        var blocksPerMcu = components.Sum(c => c.HorizontalSampling * c.VerticalSampling);
        int[][] scans = components.Count > 1 && blocksPerMcu <= MaxBlocksPerMcu
            ? [[.. Enumerable.Range(0, components.Count)]]
            : [.. Enumerable.Range(0, components.Count).Select(i => new[] { i })];

        // Luminance uses tables 0, chrominance tables 1 (as libjpeg does).
        var tableOf = Enumerable.Range(0, components.Count).Select(i => i == 0 ? 0 : 1).ToArray();
        HuffmanTable[] dcTables, acTables;
        if (options.OptimizeHuffmanTables)
        {
            var counter = new CountingSink(components.Count, tableOf);
            foreach (var scan in scans)
            {
                WalkScan(coefficients, scan, options.RestartInterval, counter);
            }

            dcTables = [HuffmanTable.Optimal(counter.DcFrequencies[0]), HuffmanTable.Optimal(counter.DcFrequencies[1])];
            acTables = [HuffmanTable.Optimal(counter.AcFrequencies[0]), HuffmanTable.Optimal(counter.AcFrequencies[1])];
        }
        else
        {
            dcTables = [HuffmanTable.StandardDcLuminance, HuffmanTable.StandardDcChrominance];
            acTables = [HuffmanTable.StandardAcLuminance, HuffmanTable.StandardAcChrominance];
        }

        var writer = new JpegBitWriter(output);
        writer.WriteMarker(0xD8); // SOI
        if (options.WriteJfifHeader)
        {
            writer.WriteMarker(0xE0);
            writer.WriteUInt16(16);
            foreach (var b in "JFIF\0"u8)
            {
                writer.WriteByte(b);
            }

            writer.WriteUInt16(0x0101); // version 1.01
            writer.WriteByte(0);        // aspect ratio only
            writer.WriteUInt16(1);
            writer.WriteUInt16(1);
            writer.WriteUInt16(0);      // no thumbnail
        }

        foreach (var index in usedTables)
        {
            var table = coefficients.GetQuantizationTable(index);
            writer.WriteMarker(0xDB);
            writer.WriteUInt16(2 + 1 + (wide ? 128 : 64));
            writer.WriteByte(((wide ? 1 : 0) << 4) | index);
            foreach (var natural in JpegTables.ZigZag)
            {
                if (wide)
                {
                    writer.WriteUInt16(table[natural]);
                }
                else
                {
                    writer.WriteByte(table[natural]);
                }
            }
        }

        // SOF0 baseline, or SOF1 (extended sequential, still Huffman and 8-bit) when a table needs 16 bits.
        writer.WriteMarker(wide ? (byte)0xC1 : (byte)0xC0);
        writer.WriteUInt16(8 + (3 * components.Count));
        writer.WriteByte(8);
        writer.WriteUInt16(coefficients.Height);
        writer.WriteUInt16(coefficients.Width);
        writer.WriteByte(components.Count);
        foreach (var component in components)
        {
            writer.WriteByte(component.Id);
            writer.WriteByte((component.HorizontalSampling << 4) | component.VerticalSampling);
            writer.WriteByte(component.QuantizationTableIndex);
        }

        var tableCount = components.Count == 1 ? 1 : 2;
        for (var t = 0; t < tableCount; t++)
        {
            WriteHuffmanTable(writer, 0, t, dcTables[t]);
            WriteHuffmanTable(writer, 1, t, acTables[t]);
        }

        if (options.RestartInterval > 0)
        {
            writer.WriteMarker(0xDD);
            writer.WriteUInt16(4);
            writer.WriteUInt16(options.RestartInterval);
        }

        foreach (var scan in scans)
        {
            writer.WriteMarker(0xDA);
            writer.WriteUInt16(6 + (2 * scan.Length));
            writer.WriteByte(scan.Length);
            foreach (var i in scan)
            {
                writer.WriteByte(components[i].Id);
                writer.WriteByte((tableOf[i] << 4) | tableOf[i]);
            }

            writer.WriteByte(0);  // Ss
            writer.WriteByte(63); // Se
            writer.WriteByte(0);  // Ah/Al
            WalkScan(coefficients, scan, options.RestartInterval, new WritingSink(writer, components.Count, tableOf, dcTables, acTables));
        }

        writer.WriteMarker(0xD9); // EOI
        writer.Flush();
    }

    /// <summary><see cref="Quantize"/> then <see cref="Write"/>.</summary>
    public static void Encode(PixelBuffer image, Stream output, int quality, JpegSubsampling subsampling = JpegSubsampling.Yuv420, JpegWriteOptions? options = null) =>
        Write(Quantize(image, quality, subsampling), output, options);

    private static void QuantizeBlock(ReadOnlySpan<float> dct, ReadOnlySpan<ushort> table, Span<short> block)
    {
        for (var i = 0; i < 64; i++)
        {
            var q = (int)MathF.Round(dct[i] / table[i], MidpointRounding.AwayFromZero);
            var limit = i == 0 ? 1023 : JpegTables.MaxAcMagnitude;
            block[i] = (short)Math.Clamp(q, -limit, limit);
        }
    }

    private static void WriteHuffmanTable(JpegBitWriter writer, int tableClass, int index, HuffmanTable table)
    {
        writer.WriteMarker(0xC4);
        writer.WriteUInt16(2 + 1 + 16 + table.Values.Length);
        writer.WriteByte((tableClass << 4) | index);
        foreach (var count in table.Bits)
        {
            writer.WriteByte(count);
        }

        foreach (var symbol in table.Values)
        {
            writer.WriteByte(symbol);
        }
    }

    /// <summary>Visits a scan's blocks in coding order, signalling restart points.</summary>
    private static void WalkScan(JpegCoefficients image, int[] scan, int restartInterval, IBlockSink sink)
    {
        var components = image.Components;
        var mcu = 0;
        var restarts = 0;
        if (scan.Length == 1)
        {
            var component = components[scan[0]];
            for (var by = 0; by < component.EncodedBlocksHigh; by++)
            {
                for (var bx = 0; bx < component.EncodedBlocksWide; bx++, mcu++)
                {
                    if (restartInterval > 0 && mcu > 0 && mcu % restartInterval == 0)
                    {
                        sink.Restart(restarts++);
                    }

                    sink.Block(scan[0], component.Block(bx, by));
                }
            }

            return;
        }

        for (var my = 0; my < image.McusHigh; my++)
        {
            for (var mx = 0; mx < image.McusWide; mx++, mcu++)
            {
                if (restartInterval > 0 && mcu > 0 && mcu % restartInterval == 0)
                {
                    sink.Restart(restarts++);
                }

                foreach (var index in scan)
                {
                    var component = components[index];
                    for (var v = 0; v < component.VerticalSampling; v++)
                    {
                        for (var h = 0; h < component.HorizontalSampling; h++)
                        {
                            sink.Block(index, component.Block((mx * component.HorizontalSampling) + h, (my * component.VerticalSampling) + v));
                        }
                    }
                }
            }
        }
    }

    private interface IBlockSink
    {
        void Block(int component, ReadOnlySpan<short> block);

        void Restart(int index);
    }

    /// <summary>Turns a block into (Huffman symbol, extra bits) pairs, tracking the DC predictors.</summary>
    private abstract class SymbolSink(int componentCount, int[] tableOf) : IBlockSink
    {
        private readonly int[] _predictors = new int[componentCount];

        public void Block(int component, ReadOnlySpan<short> block)
        {
            var table = tableOf[component];
            var diff = block[0] - _predictors[component];
            _predictors[component] = block[0];
            var category = JpegTables.Category(diff);
            if (category > 11)
            {
                throw new ArgumentException("A DC coefficient is out of range for an 8-bit JPEG.", nameof(block));
            }

            Dc(table, category, diff);

            var run = 0;
            for (var k = 1; k < 64; k++)
            {
                int value = block[JpegTables.ZigZag[k]];
                if (value == 0)
                {
                    run++;
                    continue;
                }

                while (run > 15)
                {
                    Ac(table, 0xF0, 0, 0);
                    run -= 16;
                }

                category = JpegTables.Category(value);
                if (category > 10)
                {
                    throw new ArgumentException("An AC coefficient is out of range for an 8-bit JPEG.", nameof(block));
                }

                Ac(table, (run << 4) | category, category, value);
                run = 0;
            }

            if (run > 0)
            {
                Ac(table, 0x00, 0, 0); // EOB
            }
        }

        public virtual void Restart(int index) => Array.Clear(_predictors);

        protected abstract void Dc(int table, int category, int value);

        protected abstract void Ac(int table, int symbol, int category, int value);
    }

    private sealed class CountingSink(int componentCount, int[] tableOf) : SymbolSink(componentCount, tableOf)
    {
        public long[][] DcFrequencies { get; } = [new long[256], new long[256]];

        public long[][] AcFrequencies { get; } = [new long[256], new long[256]];

        protected override void Dc(int table, int category, int value) => DcFrequencies[table][category]++;

        protected override void Ac(int table, int symbol, int category, int value) => AcFrequencies[table][symbol]++;
    }

    private sealed class WritingSink(JpegBitWriter writer, int componentCount, int[] tableOf, HuffmanTable[] dcTables, HuffmanTable[] acTables)
        : SymbolSink(componentCount, tableOf)
    {
        public override void Restart(int index)
        {
            base.Restart(index);
            writer.WriteMarker((byte)(0xD0 + (index & 7)));
        }

        protected override void Dc(int table, int category, int value)
        {
            dcTables[table].Encode(writer, category);
            WriteMagnitude(category, value);
        }

        protected override void Ac(int table, int symbol, int category, int value)
        {
            acTables[table].Encode(writer, symbol);
            WriteMagnitude(category, value);
        }

        // Negative values are sent as value - 1 in 'category' bits (T.81 F.1.2.1.1).
        private void WriteMagnitude(int category, int value)
        {
            if (category > 0)
            {
                writer.WriteBits(value < 0 ? value - 1 : value, category);
            }
        }
    }
}
