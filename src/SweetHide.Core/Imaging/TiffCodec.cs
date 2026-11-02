using System.Buffers.Binary;
using System.IO.Compression;

namespace SweetHide.Core.Imaging;

/// <summary>
/// Baseline TIFF, enough for SweetHide's own output: 8-bit RGB or RGBA (unassociated alpha), chunky, in strips,
/// uncompressed or Deflate. SkiaSharp can't read TIFF, so this is also what reads it back.
/// </summary>
public static class TiffCodec
{
    private const ushort ImageWidth = 256;
    private const ushort ImageLength = 257;
    private const ushort BitsPerSample = 258;
    private const ushort Compression = 259;
    private const ushort Photometric = 262;
    private const ushort StripOffsets = 273;
    private const ushort SamplesPerPixel = 277;
    private const ushort RowsPerStrip = 278;
    private const ushort StripByteCounts = 279;
    private const ushort PlanarConfiguration = 284;
    private const ushort ExtraSamples = 338;
    private const ushort Predictor = 317;
    private const int RowsPerStripValue = 64;

    public static bool IsTiff(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && ((header[0] == 'I' && header[1] == 'I' && header[2] == 42 && header[3] == 0)
            || (header[0] == 'M' && header[1] == 'M' && header[2] == 0 && header[3] == 42));

    public static void Write(PixelBuffer image, Stream output)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(output);
        var samples = image.IsOpaque ? 3 : 4;
        var strips = new List<byte[]>();
        var data = image.Data;
        for (var top = 0; top < image.Height; top += RowsPerStripValue)
        {
            var rows = Math.Min(RowsPerStripValue, image.Height - top);
            var raw = new byte[rows * image.Width * samples];
            var r = 0;
            for (var y = top; y < top + rows; y++)
            {
                for (var x = 0; x < image.Width; x++)
                {
                    var s = ((y * image.Width) + x) * 4;
                    raw[r++] = data[s];
                    raw[r++] = data[s + 1];
                    raw[r++] = data[s + 2];
                    if (samples == 4)
                    {
                        raw[r++] = data[s + 3];
                    }
                }
            }

            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            strips.Add(compressed.ToArray());
        }

        var entries = samples == 4 ? 12 : 11;
        const int ifdOffset = 8;
        var ifdSize = 2 + (entries * 12) + 4;
        var extraOffset = ifdOffset + ifdSize;
        var bitsOffset = extraOffset;
        var stripOffsetsOffset = bitsOffset + (samples * 2);
        var stripCountsOffset = stripOffsetsOffset + (strips.Count * 4);
        var dataOffset = stripCountsOffset + (strips.Count * 4);

        var header = new byte[dataOffset];
        header[0] = (byte)'I';
        header[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), ifdOffset);
        var e = header.AsSpan(ifdOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(e, (ushort)entries);
        var p = 2;
        void Entry(Span<byte> ifd, ushort tag, ushort type, uint count, uint value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(ifd[p..], tag);
            BinaryPrimitives.WriteUInt16LittleEndian(ifd[(p + 2)..], type);
            BinaryPrimitives.WriteUInt32LittleEndian(ifd[(p + 4)..], count);
            if (type == 3 && count == 1)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(ifd[(p + 8)..], (ushort)value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(ifd[(p + 8)..], value);
            }

            p += 12;
        }

        // Entries must be in ascending tag order.
        Entry(e, ImageWidth, 4, 1, (uint)image.Width);
        Entry(e, ImageLength, 4, 1, (uint)image.Height);
        Entry(e, BitsPerSample, 3, (uint)samples, (uint)bitsOffset);
        Entry(e, Compression, 3, 1, 8); // Adobe Deflate
        Entry(e, Photometric, 3, 1, 2); // RGB
        // TIFF stores a single value inline in the entry instead of pointing at an array.
        var singleStrip = strips.Count == 1;
        Entry(e, StripOffsets, 4, (uint)strips.Count, singleStrip ? (uint)dataOffset : (uint)stripOffsetsOffset);
        Entry(e, SamplesPerPixel, 3, 1, (uint)samples);
        Entry(e, RowsPerStrip, 4, 1, RowsPerStripValue);
        Entry(e, StripByteCounts, 4, (uint)strips.Count, singleStrip ? (uint)strips[0].Length : (uint)stripCountsOffset);
        Entry(e, PlanarConfiguration, 3, 1, 1);
        Entry(e, Predictor, 3, 1, 1);
        if (samples == 4)
        {
            Entry(e, ExtraSamples, 3, 1, 2); // unassociated alpha
        }

        for (var i = 0; i < samples; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(bitsOffset + (i * 2)), 8);
        }

        long offset = dataOffset;
        for (var i = 0; i < strips.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(stripOffsetsOffset + (i * 4)), checked((uint)offset));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(stripCountsOffset + (i * 4)), (uint)strips[i].Length);
            offset += strips[i].Length;
        }

        output.Write(header);
        foreach (var strip in strips)
        {
            output.Write(strip);
        }
    }

    /// <exception cref="InvalidImageException">Not a TIFF SweetHide can read, or damaged.</exception>
    public static PixelBuffer Read(ReadOnlySpan<byte> file, long maxPixels)
    {
        try
        {
            return ReadCore(file.ToArray(), maxPixels);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException or OverflowException)
        {
            throw new InvalidImageException("Damaged or unsupported TIFF.", ex);
        }
    }

    private static PixelBuffer ReadCore(byte[] file, long maxPixels)
    {
        if (!IsTiff(file))
        {
            throw new InvalidImageException("Not a TIFF file.");
        }

        var little = file[0] == 'I';
        ushort U16(int at) => little ? BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(at));
        uint U32(int at) => little ? BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at));

        var ifd = checked((int)U32(4));
        int count = U16(ifd);
        var tags = new Dictionary<ushort, (ushort Type, uint Count, int ValueOffset)>();
        for (var i = 0; i < count; i++)
        {
            var at = ifd + 2 + (i * 12);
            tags[U16(at)] = (U16((at + 2)), U32((at + 4)), at + 8);
        }

        uint Value(ushort tag, uint fallback = 0)
        {
            if (!tags.TryGetValue(tag, out var t))
            {
                return fallback;
            }

            return t.Type == 3 ? U16(t.ValueOffset) : U32(t.ValueOffset);
        }

        uint[] Values(ushort tag)
        {
            var t = tags[tag];
            var size = t.Type == 3 ? 2 : 4;
            if (t.Count > 1_000_000)
            {
                throw new InvalidDataException("Too many values.");
            }

            var start = t.Count * size <= 4 ? t.ValueOffset : checked((int)U32(t.ValueOffset));
            var values = new uint[t.Count];
            for (var i = 0; i < values.Length; i++)
            {
                var at = start + (i * size);
                values[i] = size == 2 ? U16(at) : U32(at);
            }

            return values;
        }

        var width = (int)Value(ImageWidth);
        var height = (int)Value(ImageLength);
        var samples = (int)Value(SamplesPerPixel, 1);
        var compression = Value(Compression, 1);
        if (width <= 0 || height <= 0 || (long)width * height > maxPixels)
        {
            throw new InvalidImageException($"TIFF size {width}x{height} is not supported.");
        }

        if (samples is not (3 or 4) || Value(Photometric) != 2 || Value(PlanarConfiguration, 1) != 1
            || Value(Predictor, 1) != 1 || Values(BitsPerSample).Any(b => b != 8) || compression is not (1 or 8 or 32946))
        {
            throw new InvalidImageException("Only 8-bit RGB/RGBA TIFF (uncompressed or Deflate) is supported.");
        }

        var offsets = Values(StripOffsets);
        var counts = Values(StripByteCounts);
        var rowsPerStrip = (int)Math.Min(Value(RowsPerStrip, (uint)height), (uint)height);
        var rgba = new byte[(long)width * height * 4];
        var y = 0;
        for (var strip = 0; strip < offsets.Length && y < height; strip++)
        {
            var rows = Math.Min(rowsPerStrip, height - y);
            var expected = rows * width * samples;
            var raw = file.AsSpan(checked((int)offsets[strip]), checked((int)counts[strip]));
            byte[] pixels;
            if (compression == 1)
            {
                pixels = raw.ToArray();
            }
            else
            {
                pixels = new byte[expected];
                using var zlib = new ZLibStream(new MemoryStream(raw.ToArray()), CompressionMode.Decompress);
                var read = 0;
                int n;
                while (read < expected && (n = zlib.Read(pixels, read, expected - read)) > 0)
                {
                    read += n;
                }
            }

            if (pixels.Length < expected)
            {
                throw new InvalidDataException("Strip too short.");
            }

            for (var i = 0; i < rows * width; i++)
            {
                var d = (((long)y * width) + i) * 4;
                rgba[d] = pixels[i * samples];
                rgba[d + 1] = pixels[(i * samples) + 1];
                rgba[d + 2] = pixels[(i * samples) + 2];
                rgba[d + 3] = samples == 4 ? pixels[(i * samples) + 3] : (byte)255;
            }

            y += rows;
        }

        return y < height ? throw new InvalidDataException("Missing strips.") : new PixelBuffer(width, height, rgba);
    }
}
