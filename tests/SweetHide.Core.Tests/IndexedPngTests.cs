using SweetHide.Core.Imaging;

namespace SweetHide.Core.Tests;

public class IndexedPngTests
{
    [Fact]
    public void Round_trips_indices_palette_and_transparency()
    {
        var random = new Random(4);
        var palette = Enumerable.Range(0, 200).Select(i => (uint)((i == 0 ? 0x00u : 0xFFu) << 24 | (uint)random.Next(0, 0xFFFFFF))).ToList();
        var indices = new byte[37 * 23];
        for (var i = 0; i < indices.Length; i++)
        {
            indices[i] = (byte)random.Next(0, palette.Count);
        }

        var image = new IndexedImage(37, 23, indices, palette);
        using var stream = new MemoryStream();
        IndexedPng.Write(image, stream);
        var png = stream.ToArray();

        Assert.True(IndexedPng.IsIndexed(png));
        var back = IndexedPng.Read(png);
        Assert.Equal(indices, back.Indices);
        Assert.Equal(palette, back.Palette);

        // Other decoders see the same colours.
        var viaSkia = new SkiaImageCodec().Decode(new MemoryStream(png), false);
        Assert.Equal(image.ToPixels().Pixels.ToArray(), viaSkia.Pixels.ToArray());
    }

    [Fact]
    public void Undoes_every_png_filter_type()
    {
        // Other encoders use Sub/Up/Average/Paeth filters; a reference filter below produces each one.
        var indices = Enumerable.Range(0, 64 * 8).Select(i => (byte)((i * 7) % 16)).ToArray();
        var palette = Enumerable.Range(0, 16).Select(i => 0xFF000000u | (uint)(i * 0x101010)).ToList();
        foreach (var filter in new byte[] { 0, 1, 2, 3, 4 })
        {
            var png = FilteredPalettePng(64, 8, indices, palette, filter);
            Assert.Equal(indices, IndexedPng.Read(png).Indices);
        }
    }

    [Fact]
    public void Rejects_non_palette_and_damaged_files()
    {
        using var rgb = new MemoryStream();
        new SkiaImageCodec().EncodePng(TestImages.Photo(10, 10), rgb);
        Assert.False(IndexedPng.IsIndexed(rgb.ToArray()));
        Assert.Throws<InvalidImageException>(() => IndexedPng.Read(rgb.ToArray()));

        using var stream = new MemoryStream();
        IndexedPng.Write(new IndexedImage(20, 20, new byte[400], [0xFF000000u, 0xFFFFFFFFu]), stream);
        var png = stream.ToArray();
        for (var length = 30; length < png.Length - 12; length += 7)
        {
            Assert.Throws<InvalidImageException>(() => IndexedPng.Read(png.AsSpan(0, length)));
        }
    }

    /// <summary>A minimal reference encoder applying one PNG filter type to every row (8-bit palette).</summary>
    private static byte[] FilteredPalettePng(int width, int height, byte[] indices, IReadOnlyList<uint> palette, byte filter)
    {
        using var stream = new MemoryStream();
        IndexedPng.Write(new IndexedImage(width, height, indices, palette), stream);
        var plain = IndexedPng.Read(stream.ToArray()); // sanity
        var raw = new List<byte>();
        var previous = new byte[width];
        for (var y = 0; y < height; y++)
        {
            var row = plain.Indices.AsSpan(y * width, width).ToArray();
            raw.Add(filter);
            for (var x = 0; x < width; x++)
            {
                int left = x > 0 ? row[x - 1] : 0, up = previous[x], upLeft = x > 0 ? previous[x - 1] : 0;
                var predictor = filter switch
                {
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, upLeft),
                    _ => 0,
                };
                raw.Add((byte)(row[x] - predictor));
            }

            previous = row;
        }

        // Re-assemble: reuse our writer's chunks except IDAT.
        var original = stream.ToArray();
        using var compressed = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write([.. raw]);
        }

        var idatStart = IndexOf(original, "IDAT"u8) - 4;
        var idatLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(original.AsSpan(idatStart));
        var before = original[..idatStart];
        var after = original[(idatStart + 12 + idatLength)..];
        var data = compressed.ToArray();
        var chunk = new byte[12 + data.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(chunk, data.Length);
        "IDAT"u8.CopyTo(chunk.AsSpan(4));
        data.CopyTo(chunk, 8); // CRC left as zero: our reader doesn't verify CRCs
        return [.. before, .. chunk, .. after];
    }

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle) => haystack.AsSpan().IndexOf(needle);

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
