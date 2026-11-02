using System.Buffers.Binary;

using SweetHide.Core.Imaging;

namespace SweetHide.Core.Jpeg;

/// <summary>
/// Reads baseline (and extended 8-bit) sequential Huffman JPEGs into <see cref="JpegCoefficients"/>, exactly as
/// stored, and decodes coefficients to pixels for previews.
/// </summary>
/// <remarks>
/// Supports 1 or 3 components, sampling factors up to 2x2, restart intervals and multiple sequential scans.
/// Progressive, arithmetic-coded, lossless, hierarchical and 12-bit files are rejected with
/// <see cref="InvalidImageException"/>. Input is treated as hostile: every length is bounds-checked, the pixel count is
/// limited, and the coefficient buffers are only allocated once the file is long enough to plausibly fill them.
/// </remarks>
public static class JpegDecoder
{
    /// <summary>Same default limit as <see cref="SkiaImageCodec"/>.</summary>
    public const long DefaultMaxPixels = SkiaImageCodec.DefaultMaxPixels;

    /// <summary>Largest file accepted from a stream.</summary>
    public const int MaxInputBytes = 256 * 1024 * 1024;

    private const int MaxBlocksPerMcu = 10;

    /// <exception cref="InvalidImageException">Not a supported JPEG, corrupt, or over the size limits.</exception>
    public static JpegCoefficients Read(Stream input, long maxPixels = DefaultMaxPixels)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaxInputBytes)
            {
                throw new InvalidImageException($"The file is larger than {MaxInputBytes / (1024 * 1024)} MiB.");
            }

            buffer.Write(chunk, 0, read);
        }

        return Read(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), maxPixels);
    }

    /// <exception cref="InvalidImageException">Not a supported JPEG, corrupt, or over the size limits.</exception>
    public static JpegCoefficients Read(ReadOnlySpan<byte> data, long maxPixels = DefaultMaxPixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPixels);
        return new Parser(data.ToArray(), maxPixels).Parse();
    }

    internal static PixelBuffer ToPixels(JpegCoefficients image)
    {
        var components = image.Components;
        var planes = new byte[components.Count][];
        for (var c = 0; c < components.Count; c++)
        {
            planes[c] = ReconstructPlane(components[c], image.GetQuantizationTable(components[c].QuantizationTableIndex));
        }

        var width = image.Width;
        var rgba = new byte[(long)width * image.Height * 4];
        Parallel.For(0, image.Height, y =>
        {
            Span<float> samples = stackalloc float[3];
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < components.Count; c++)
                {
                    samples[c] = Sample(components[c], planes[c], image, x, y);
                }

                var o = ((y * width) + x) * 4;
                if (components.Count == 1)
                {
                    var gray = ToByte(samples[0]);
                    rgba[o] = rgba[o + 1] = rgba[o + 2] = gray;
                }
                else
                {
                    float luma = samples[0], cb = samples[1] - 128f, cr = samples[2] - 128f;
                    rgba[o] = ToByte(luma + (1.402f * cr));
                    rgba[o + 1] = ToByte(luma - (0.344136f * cb) - (0.714136f * cr));
                    rgba[o + 2] = ToByte(luma + (1.772f * cb));
                }

                rgba[o + 3] = 255;
            }
        });

        return new PixelBuffer(width, image.Height, rgba);
    }

    /// <summary>Dequantize + IDCT every block into an 8-bit plane covering the whole stored block grid.</summary>
    private static byte[] ReconstructPlane(JpegComponent component, ReadOnlySpan<ushort> quantization)
    {
        var stride = component.BlocksWide * 8;
        var plane = new byte[(long)stride * component.BlocksHigh * 8];
        var table = quantization.ToArray();
        var data = component.Data;
        Parallel.For(0, component.EncodedBlocksHigh, by =>
        {
            Span<float> dequantized = stackalloc float[64];
            Span<float> samples = stackalloc float[64];
            for (var bx = 0; bx < component.EncodedBlocksWide; bx++)
            {
                var offset = ((by * component.BlocksWide) + bx) * 64;
                for (var i = 0; i < 64; i++)
                {
                    dequantized[i] = data[offset + i] * table[i];
                }

                BlockDct.Inverse(dequantized, samples);
                for (var i = 0; i < 64; i++)
                {
                    plane[((((by * 8) + (i >> 3)) * stride) + (bx * 8)) + (i & 7)] = ToByte(samples[i] + 128f);
                }
            }
        });

        return plane;
    }

    /// <summary>
    /// A component sample at full-resolution pixel (x, y). Subsampled components are interpolated with the 3/4, 1/4
    /// triangle filter of libjpeg's "fancy" upsampling, replicating the edge samples.
    /// </summary>
    private static float Sample(JpegComponent component, byte[] plane, JpegCoefficients image, int x, int y)
    {
        var stride = component.BlocksWide * 8;
        var fx = image.MaxHorizontalSampling / component.HorizontalSampling;
        var fy = image.MaxVerticalSampling / component.VerticalSampling;
        if (fx == 1 && fy == 1)
        {
            return plane[(y * stride) + x];
        }

        var (x0, x1, wx) = Taps(x, fx, component.Width);
        var (y0, y1, wy) = Taps(y, fy, component.Height);
        float top = (plane[(y0 * stride) + x0] * (1 - wx)) + (plane[(y0 * stride) + x1] * wx);
        float bottom = (plane[(y1 * stride) + x0] * (1 - wx)) + (plane[(y1 * stride) + x1] * wx);
        return (top * (1 - wy)) + (bottom * wy);
    }

    private static (int First, int Second, float SecondWeight) Taps(int position, int factor, int size)
    {
        if (factor == 1)
        {
            return (Math.Min(position, size - 1), Math.Min(position, size - 1), 0f);
        }

        // Sample i covers full-resolution pixels 2i and 2i+1; its centre is at 2i + 0.5.
        var nearest = Math.Min(position / 2, size - 1);
        var other = position % 2 == 0 ? Math.Max(nearest - 1, 0) : Math.Min(nearest + 1, size - 1);
        return (nearest, other, 0.25f);
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);

    private static InvalidImageException Corrupt(string detail) => new($"Corrupt JPEG: {detail}");

    private sealed class Parser(byte[] data, long maxPixels)
    {
        private readonly ushort[]?[] _quantization = new ushort[]?[4];
        private readonly HuffmanTable?[] _dcTables = new HuffmanTable?[4];
        private readonly HuffmanTable?[] _acTables = new HuffmanTable?[4];
        private readonly bool[] _quantizationUsed = new bool[4];
        private int _position;
        private int _restartInterval;
        private JpegCoefficients? _image;
        private bool[] _decoded = [];

        public JpegCoefficients Parse()
        {
            if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            {
                throw new InvalidImageException("Not a JPEG file.");
            }

            _position = 2;
            while (NextMarker() is { } marker)
            {
                switch (marker)
                {
                    case 0xD9: // EOI
                        return Finish();
                    case 0xC0 or 0xC1:
                        ReadFrame(Segment());
                        break;
                    case 0xC2 or 0xC6 or 0xCA or 0xCE:
                        throw new InvalidImageException("Progressive JPEG is not supported; only baseline sequential files can be read.");
                    case 0xC3 or 0xC7 or 0xCB or 0xCF:
                        throw new InvalidImageException("Lossless JPEG is not supported; only baseline sequential files can be read.");
                    case 0xC5:
                        throw new InvalidImageException("Hierarchical JPEG is not supported; only baseline sequential files can be read.");
                    case 0xC8 or 0xC9 or 0xCC or 0xCD:
                        throw new InvalidImageException("Arithmetic-coded JPEG is not supported; only Huffman-coded files can be read.");
                    case 0xC4:
                        ReadHuffmanTables(Segment());
                        break;
                    case 0xDB:
                        ReadQuantizationTables(Segment());
                        break;
                    case 0xDD:
                        var dri = Segment();
                        _restartInterval = dri.Length == 2
                            ? BinaryPrimitives.ReadUInt16BigEndian(dri)
                            : throw Corrupt("bad DRI segment.");
                        break;
                    case 0xDA:
                        ReadScan(Segment());
                        break;
                    case 0xDC:
                        throw new InvalidImageException("JPEG files with a DNL marker are not supported.");
                    case 0xD8:
                        throw Corrupt("unexpected SOI marker.");
                    case >= 0xD0 and <= 0xD7 or 0x01:
                        break; // Stray RSTn / TEM: no payload.
                    default:
                        Segment(); // APPn, COM and anything else with a length: skipped.
                        break;
                }
            }

            return Finish(); // Tolerate a missing EOI if every component was decoded.
        }

        private JpegCoefficients Finish()
        {
            if (_image is null)
            {
                throw Corrupt("no image frame.");
            }

            if (Array.Exists(_decoded, d => !d))
            {
                throw Corrupt("a colour component has no scan.");
            }

            return _image;
        }

        /// <summary>Finds the next marker, skipping fill bytes and (like libjpeg) any garbage before it.</summary>
        private byte? NextMarker()
        {
            while (_position + 1 < data.Length)
            {
                if (data[_position] == 0xFF && data[_position + 1] is not (0x00 or 0xFF))
                {
                    var marker = data[_position + 1];
                    _position += 2;
                    return marker;
                }

                _position++;
            }

            return null;
        }

        private ReadOnlySpan<byte> Segment()
        {
            if (_position + 2 > data.Length)
            {
                throw Corrupt("truncated segment.");
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(_position));
            if (length < 2 || _position + length > data.Length)
            {
                throw Corrupt("truncated segment.");
            }

            var segment = data.AsSpan(_position + 2, length - 2);
            _position += length;
            return segment;
        }

        private void ReadQuantizationTables(ReadOnlySpan<byte> segment)
        {
            while (!segment.IsEmpty)
            {
                var precision = segment[0] >> 4;
                var index = segment[0] & 0x0F;
                var size = precision == 0 ? 64 : 128;
                if (precision > 1 || index > 3 || segment.Length < 1 + size)
                {
                    throw Corrupt("bad quantization table.");
                }

                var table = new ushort[64];
                for (var k = 0; k < 64; k++)
                {
                    table[JpegTables.ZigZag[k]] = precision == 0
                        ? segment[1 + k]
                        : BinaryPrimitives.ReadUInt16BigEndian(segment[(1 + (2 * k))..]);
                }

                if (Array.IndexOf(table, (ushort)0) >= 0)
                {
                    throw Corrupt("a quantization table contains 0.");
                }

                if (_quantizationUsed[index] && !table.AsSpan().SequenceEqual(_quantization[index]))
                {
                    throw new InvalidImageException("JPEG files that change a quantization table between scans are not supported.");
                }

                _quantization[index] = table;
                segment = segment[(1 + size)..];
            }
        }

        private void ReadHuffmanTables(ReadOnlySpan<byte> segment)
        {
            while (!segment.IsEmpty)
            {
                if (segment.Length < 17)
                {
                    throw Corrupt("bad Huffman table.");
                }

                var tableClass = segment[0] >> 4;
                var index = segment[0] & 0x0F;
                var bits = segment.Slice(1, 16);
                var total = 0;
                foreach (var count in bits)
                {
                    total += count;
                }

                if (tableClass > 1 || index > 3 || total > 256 || segment.Length < 17 + total)
                {
                    throw Corrupt("bad Huffman table.");
                }

                var table = HuffmanTable.Create(bits, segment.Slice(17, total));
                (tableClass == 0 ? _dcTables : _acTables)[index] = table;
                segment = segment[(17 + total)..];
            }
        }

        private void ReadFrame(ReadOnlySpan<byte> segment)
        {
            if (_image is not null)
            {
                throw Corrupt("more than one frame.");
            }

            if (segment.Length < 6)
            {
                throw Corrupt("bad frame header.");
            }

            if (segment[0] != 8)
            {
                throw new InvalidImageException($"{segment[0]}-bit JPEG is not supported; only 8-bit files can be read.");
            }

            int height = BinaryPrimitives.ReadUInt16BigEndian(segment[1..]);
            int width = BinaryPrimitives.ReadUInt16BigEndian(segment[3..]);
            int count = segment[5];
            if (width == 0 || height == 0)
            {
                throw new InvalidImageException("JPEG files without a height in the frame header (DNL) are not supported.");
            }

            if (count is not (1 or 3))
            {
                throw new InvalidImageException($"JPEG files with {count} components are not supported (only grayscale or YCbCr).");
            }

            if (segment.Length != 6 + (3 * count))
            {
                throw Corrupt("bad frame header.");
            }

            if ((long)width * height > maxPixels)
            {
                throw new InvalidImageException($"The image is {width}x{height}; the limit is {maxPixels:N0} pixels.");
            }

            var specs = new (byte Id, int H, int V, int Tq)[count];
            for (var i = 0; i < count; i++)
            {
                var spec = segment.Slice(6 + (3 * i), 3);
                int h = spec[1] >> 4, v = spec[1] & 0x0F;
                if (h is < 1 or > 2 || v is < 1 or > 2)
                {
                    throw new InvalidImageException("JPEG sampling factors above 2 are not supported.");
                }

                if (spec[2] > 3)
                {
                    throw Corrupt("bad quantization table index.");
                }

                var id = spec[0];
                if (Array.Exists(specs[..i], s => s.Id == id))
                {
                    throw Corrupt("duplicate component id.");
                }

                specs[i] = (id, h, v, spec[2]);
            }

            int hMax = specs.Max(s => s.H), vMax = specs.Max(s => s.V);
            var mcusWide = JpegCoefficients.DivideRoundingUp(width, 8 * hMax);
            var mcusHigh = JpegCoefficients.DivideRoundingUp(height, 8 * vMax);

            // Every block costs at least 2 bits (a DC code and an EOB or AC code), so a file shorter than that cannot
            // be valid. Checking before allocating stops tiny files from claiming gigabytes of coefficients.
            long minimumBlocks = 0;
            foreach (var s in specs)
            {
                var componentWidth = JpegCoefficients.DivideRoundingUp(width * s.H, hMax);
                var componentHeight = JpegCoefficients.DivideRoundingUp(height * s.V, vMax);
                minimumBlocks += (long)JpegCoefficients.DivideRoundingUp(componentWidth, 8) * JpegCoefficients.DivideRoundingUp(componentHeight, 8);
            }

            if (minimumBlocks > (data.Length - (long)_position) * 4)
            {
                throw Corrupt("the file is too short for its declared size.");
            }

            if (specs.Any(s => (long)mcusWide * s.H * mcusHigh * s.V * JpegTables.BlockSize > Array.MaxLength))
            {
                throw new InvalidImageException("The image is too large.");
            }

            var components = specs
                .Select(s => new JpegComponent(s.Id, s.H, s.V, s.Tq, mcusWide * s.H, mcusHigh * s.V))
                .ToArray();
            _image = new JpegCoefficients(width, height, components, _quantization);
            _decoded = new bool[count];
        }

        private void ReadScan(ReadOnlySpan<byte> segment)
        {
            var image = _image ?? throw Corrupt("scan before frame header.");
            if (segment.Length < 1)
            {
                throw Corrupt("bad scan header.");
            }

            int count = segment[0];
            if (count < 1 || count > image.Components.Count || segment.Length != 4 + (2 * count))
            {
                throw Corrupt("bad scan header.");
            }

            // Ss/Se/Ah/Al are ignored for sequential scans, as libjpeg does.
            var scan = new int[count];
            var dc = new HuffmanTable[count];
            var ac = new HuffmanTable[count];
            for (var i = 0; i < count; i++)
            {
                var id = segment[1 + (2 * i)];
                var tables = segment[2 + (2 * i)];
                var index = -1;
                for (var c = 0; c < image.Components.Count; c++)
                {
                    if (image.Components[c].Id == id)
                    {
                        index = c;
                    }
                }

                if (index < 0 || Array.IndexOf(scan, index, 0, i) >= 0)
                {
                    throw Corrupt("bad component in scan header.");
                }

                if (_decoded[index])
                {
                    throw Corrupt("a component appears in two scans.");
                }

                scan[i] = index;
                dc[i] = (tables >> 4) < 4 ? _dcTables[tables >> 4] ?? throw Corrupt("missing Huffman table.") : throw Corrupt("bad Huffman table index.");
                ac[i] = (tables & 15) < 4 ? _acTables[tables & 15] ?? throw Corrupt("missing Huffman table.") : throw Corrupt("bad Huffman table index.");

                var quantizationIndex = image.Components[index].QuantizationTableIndex;
                if (_quantization[quantizationIndex] is null)
                {
                    throw Corrupt("missing quantization table.");
                }

                _quantizationUsed[quantizationIndex] = true;
            }

            if (count > 1 && scan.Sum(i => image.Components[i].HorizontalSampling * image.Components[i].VerticalSampling) > MaxBlocksPerMcu)
            {
                throw Corrupt("too many blocks per MCU.");
            }

            var reader = new JpegBitReader(data, _position, data.Length);
            DecodeScan(image, scan, dc, ac, ref reader);
            foreach (var index in scan)
            {
                _decoded[index] = true;
            }

            _position = reader.Position;
        }

        private void DecodeScan(JpegCoefficients image, int[] scan, HuffmanTable[] dc, HuffmanTable[] ac, ref JpegBitReader reader)
        {
            var predictors = new int[scan.Length];
            var restarts = 0;
            var mcu = 0;
            if (scan.Length == 1)
            {
                var component = image.Components[scan[0]];
                var blocks = component.Data;
                for (var by = 0; by < component.EncodedBlocksHigh; by++)
                {
                    for (var bx = 0; bx < component.EncodedBlocksWide; bx++, mcu++)
                    {
                        if (_restartInterval > 0 && mcu > 0 && mcu % _restartInterval == 0)
                        {
                            reader.Restart(restarts++);
                            predictors[0] = 0;
                        }

                        DecodeBlock(ref reader, dc[0], ac[0], ref predictors[0], blocks, ((by * component.BlocksWide) + bx) * 64);
                    }
                }

                return;
            }

            for (var my = 0; my < image.McusHigh; my++)
            {
                for (var mx = 0; mx < image.McusWide; mx++, mcu++)
                {
                    if (_restartInterval > 0 && mcu > 0 && mcu % _restartInterval == 0)
                    {
                        reader.Restart(restarts++);
                        Array.Clear(predictors);
                    }

                    for (var i = 0; i < scan.Length; i++)
                    {
                        var component = image.Components[scan[i]];
                        for (var v = 0; v < component.VerticalSampling; v++)
                        {
                            for (var h = 0; h < component.HorizontalSampling; h++)
                            {
                                var bx = (mx * component.HorizontalSampling) + h;
                                var by = (my * component.VerticalSampling) + v;
                                DecodeBlock(ref reader, dc[i], ac[i], ref predictors[i], component.Data, ((by * component.BlocksWide) + bx) * 64);
                            }
                        }
                    }
                }
            }
        }

        private static void DecodeBlock(ref JpegBitReader reader, HuffmanTable dc, HuffmanTable ac, ref int predictor, short[] blocks, int offset)
        {
            var category = dc.Decode(ref reader);
            if (category > 11)
            {
                throw Corrupt("DC coefficient out of range.");
            }

            predictor += Extend(reader.ReadBits(category), category);
            if (predictor is < -2047 or > 2047)
            {
                throw Corrupt("DC coefficient out of range.");
            }

            blocks[offset] = (short)predictor;
            for (var k = 1; k < 64; k++)
            {
                var symbol = ac.Decode(ref reader);
                var run = symbol >> 4;
                var size = symbol & 15;
                if (size == 0)
                {
                    if (run != 15)
                    {
                        break; // EOB
                    }

                    k += 15; // ZRL: 16 zeros (the loop adds the 16th).
                    continue;
                }

                k += run;
                if (k > 63 || size > 10)
                {
                    throw Corrupt("AC coefficient out of range.");
                }

                blocks[offset + JpegTables.ZigZag[k]] = (short)Extend(reader.ReadBits(size), size);
            }
        }

        private static int Extend(int value, int size) =>
            size == 0 ? 0 : value < 1 << (size - 1) ? value - (1 << size) + 1 : value;
    }
}
