using System.Buffers.Binary;
using System.IO.Compression;

namespace SweetHide.Core.Imaging;

/// <summary>A palette image: one palette index per pixel, plus up to 256 RGBA palette entries.</summary>
public sealed class IndexedImage
{
    public IndexedImage(int width, int height, byte[] indices, IReadOnlyList<uint> palette)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(palette);
        if ((long)width * height != indices.LongLength)
        {
            throw new ArgumentException("Index count does not match the size.", nameof(indices));
        }

        if (palette.Count is 0 or > 256 || indices.Any(i => i >= palette.Count))
        {
            throw new ArgumentException("Palette must have 1-256 entries covering every index.", nameof(palette));
        }

        Width = width;
        Height = height;
        Indices = indices;
        Palette = palette;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Row-major palette indices.</summary>
    public byte[] Indices { get; }

    /// <summary>Entries as 0xAARRGGBB.</summary>
    public IReadOnlyList<uint> Palette { get; }

    public PixelBuffer ToPixels()
    {
        var rgba = new byte[Indices.LongLength * 4];
        for (var i = 0; i < Indices.Length; i++)
        {
            var c = Palette[Indices[i]];
            rgba[i * 4] = (byte)(c >> 16);
            rgba[(i * 4) + 1] = (byte)(c >> 8);
            rgba[(i * 4) + 2] = (byte)c;
            rgba[(i * 4) + 3] = (byte)(c >> 24);
        }

        return new PixelBuffer(Width, Height, rgba);
    }
}

/// <summary>
/// Reads and writes palette PNGs (colour type 3) with their indices intact. SkiaSharp expands palettes to RGBA,
/// which loses exactly the information palette steganography lives in.
/// </summary>
public static class IndexedPng
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>True when the bytes are a PNG whose IHDR says colour type 3 (palette).</summary>
    public static bool IsIndexed(ReadOnlySpan<byte> png) =>
        png.Length >= 26 && png[..8].SequenceEqual(Signature) && png.Slice(12, 4).SequenceEqual("IHDR"u8) && png[25] == 3;

    public static void Write(IndexedImage image, Stream output)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(output);
        output.Write(Signature);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), image.Height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 3; // palette
        WriteChunk(output, "IHDR"u8, ihdr);

        var plte = new byte[image.Palette.Count * 3];
        var trns = new byte[image.Palette.Count];
        var anyTransparent = false;
        for (var i = 0; i < image.Palette.Count; i++)
        {
            var c = image.Palette[i];
            plte[i * 3] = (byte)(c >> 16);
            plte[(i * 3) + 1] = (byte)(c >> 8);
            plte[(i * 3) + 2] = (byte)c;
            trns[i] = (byte)(c >> 24);
            anyTransparent |= trns[i] != 255;
        }

        WriteChunk(output, "PLTE"u8, plte);
        if (anyTransparent)
        {
            WriteChunk(output, "tRNS"u8, trns);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[image.Width + 1]; // filter type 0 (None): best for palette data
            for (var y = 0; y < image.Height; y++)
            {
                image.Indices.AsSpan(y * image.Width, image.Width).CopyTo(row.AsSpan(1));
                zlib.Write(row);
            }
        }

        WriteChunk(output, "IDAT"u8, compressed.ToArray());
        WriteChunk(output, "IEND"u8, []);
    }

    /// <exception cref="InvalidImageException">Not a non-interlaced palette PNG, or damaged.</exception>
    public static IndexedImage Read(ReadOnlySpan<byte> png, long maxPixels = SkiaImageCodec.DefaultMaxPixels)
    {
        try
        {
            return ReadCore(png.ToArray(), maxPixels);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException or OverflowException)
        {
            throw new InvalidImageException("Damaged palette PNG.", ex);
        }
    }

    private static IndexedImage ReadCore(byte[] png, long maxPixels)
    {
        if (!IsIndexed(png))
        {
            throw new InvalidImageException("Not a palette PNG.");
        }

        int width = 0, height = 0, depth = 0;
        uint[]? palette = null;
        byte[]? alpha = null;
        using var idat = new MemoryStream();
        var offset = 8;
        while (offset + 12 <= png.Length)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset)));
            var type = png.AsSpan(offset + 4, 4);
            var data = png.AsSpan(offset + 8, length);
            if (type.SequenceEqual("IHDR"u8))
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                depth = data[8];
                if (data[12] != 0)
                {
                    throw new InvalidImageException("Interlaced palette PNGs are not supported.");
                }
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                palette = new uint[length / 3];
                for (var i = 0; i < palette.Length; i++)
                {
                    palette[i] = 0xFF000000u | ((uint)data[i * 3] << 16) | ((uint)data[(i * 3) + 1] << 8) | data[(i * 3) + 2];
                }
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                alpha = data.ToArray();
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }

            offset += 12 + length;
        }

        if (width <= 0 || height <= 0 || (long)width * height > maxPixels || palette is null || depth is not (1 or 2 or 4 or 8))
        {
            throw new InvalidImageException("Unsupported palette PNG.");
        }

        for (var i = 0; i < Math.Min(alpha?.Length ?? 0, palette.Length); i++)
        {
            palette[i] = (palette[i] & 0x00FFFFFFu) | ((uint)alpha![i] << 24);
        }

        var stride = ((width * depth) + 7) / 8;
        var raw = new byte[(long)(stride + 1) * height];
        idat.Position = 0;
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress))
        {
            var read = 0;
            int n;
            while (read < raw.Length && (n = zlib.Read(raw, read, raw.Length - read)) > 0)
            {
                read += n;
            }

            if (read < raw.Length)
            {
                throw new InvalidDataException("Image data is truncated.");
            }
        }

        var indices = new byte[(long)width * height];
        var previous = new byte[stride];
        var current = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[(long)y * (stride + 1)];
            Array.Copy(raw, ((long)y * (stride + 1)) + 1, current, 0, stride);
            Unfilter(filter, current, previous);
            for (var x = 0; x < width; x++)
            {
                var bitOffset = x * depth;
                var value = (current[bitOffset / 8] >> (8 - depth - (bitOffset % 8))) & ((1 << depth) - 1);
                if (value >= palette.Length)
                {
                    throw new InvalidDataException("Palette index out of range.");
                }

                indices[((long)y * width) + x] = (byte)value;
            }

            (previous, current) = (current, previous);
        }

        return new IndexedImage(width, height, indices, palette);
    }

    private static void Unfilter(byte filter, byte[] row, byte[] above)
    {
        // Palette images use 1 byte per "pixel" for filtering purposes (bpp rounds up to 1).
        for (var i = 0; i < row.Length; i++)
        {
            int left = i > 0 ? row[i - 1] : 0;
            int up = above[i];
            int upLeft = i > 0 ? above[i - 1] : 0;
            row[i] = filter switch
            {
                0 => row[i],
                1 => (byte)(row[i] + left),
                2 => (byte)(row[i] + up),
                3 => (byte)(row[i] + ((left + up) / 2)),
                4 => (byte)(row[i] + Paeth(left, up, upLeft)),
                _ => throw new InvalidDataException($"Unknown PNG filter {filter}."),
            };
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    internal static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
        type.CopyTo(header[4..]);
        output.Write(header);
        output.Write(data);
        var crc = Crc32(Crc32(0xFFFFFFFFu, type), data) ^ 0xFFFFFFFFu;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
