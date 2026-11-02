using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

using SkiaSharp;

using SweetHide.Forensics.Containers;

namespace SweetHide.Forensics.Tests;

/// <summary>Builds small container files in code (no binary fixtures).</summary>
internal static class FileBuilders
{
    /// <summary>A real PNG with extra chunks inserted before IEND.</summary>
    public static byte[] Png(int width = 8, int height = 8, params (string Type, byte[] Data)[] extraChunks)
    {
        var png = TestData.EncodePng(TestData.Photo(width, height));
        var iend = png.Length - 12;
        using var output = new MemoryStream();
        output.Write(png, 0, iend);
        foreach (var (type, data) in extraChunks)
        {
            output.Write(Chunk(type, data));
        }

        output.Write(png, iend, 12);
        return output.ToArray();
    }

    public static byte[] Chunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc32.Compute(chunk.AsSpan(4, 4 + data.Length)));
        return chunk;
    }

    public static byte[] TextChunk(string keyword, string text) => [.. Encoding.Latin1.GetBytes(keyword), 0, .. Encoding.Latin1.GetBytes(text)];

    /// <summary>A real JPEG with extra segments inserted right after SOI.</summary>
    public static byte[] Jpeg(params (byte Marker, byte[] Data)[] segments)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(16, 16, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(new SKColor(200, 100, 50));
        using var encoded = new MemoryStream();
        bitmap.Encode(encoded, SKEncodedImageFormat.Jpeg, 80);
        var jpeg = encoded.ToArray();

        using var output = new MemoryStream();
        output.Write(jpeg, 0, 2);
        foreach (var (marker, data) in segments)
        {
            output.Write(Segment(marker, data));
        }

        output.Write(jpeg, 2, jpeg.Length - 2);
        return output.ToArray();
    }

    public static byte[] Segment(byte marker, byte[] data)
    {
        var segment = new byte[4 + data.Length];
        segment[0] = 0xFF;
        segment[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(data.Length + 2));
        data.CopyTo(segment, 4);
        return segment;
    }

    /// <summary>The classic 1x1 GIF89a with optional comment extensions and application blocks.</summary>
    public static byte[] Gif(string[]? comments = null, (string AppId, byte[] Data)[]? applications = null)
    {
        using var output = new MemoryStream();
        output.Write("GIF89a"u8);
        output.Write([1, 0, 1, 0, 0x80, 0, 0]);
        output.Write([0, 0, 0, 255, 255, 255]);
        foreach (var comment in comments ?? [])
        {
            output.Write([0x21, 0xFE]);
            WriteSubBlocks(output, Encoding.UTF8.GetBytes(comment));
        }

        foreach (var (appId, data) in applications ?? [])
        {
            output.Write([0x21, 0xFF, 11]);
            output.Write(Encoding.ASCII.GetBytes(appId.PadRight(11)[..11]));
            WriteSubBlocks(output, data);
        }

        output.Write([0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0]);
        output.Write([2, 2, 0x44, 0x01, 0]);
        output.WriteByte(0x3B);
        return output.ToArray();
    }

    public static void WriteSubBlocks(Stream output, byte[] data)
    {
        for (var i = 0; i < data.Length; i += 255)
        {
            var n = Math.Min(255, data.Length - i);
            output.WriteByte((byte)n);
            output.Write(data, i, n);
        }

        output.WriteByte(0);
    }

    /// <summary>A RIFF container with the given form type and chunks; sizes are correct.</summary>
    public static byte[] Riff(string form, params (string Id, byte[] Data)[] chunks)
    {
        using var body = new MemoryStream();
        body.Write(Encoding.ASCII.GetBytes(form));
        var size = new byte[4];
        foreach (var (id, data) in chunks)
        {
            body.Write(Encoding.ASCII.GetBytes(id));
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)data.Length);
            body.Write(size);
            body.Write(data);
            if (data.Length % 2 == 1)
            {
                body.WriteByte(0);
            }
        }

        var riff = new byte[8 + body.Length];
        "RIFF"u8.CopyTo(riff);
        BinaryPrimitives.WriteUInt32LittleEndian(riff.AsSpan(4), (uint)body.Length);
        body.ToArray().CopyTo(riff, 8);
        return riff;
    }

    /// <summary>A 24-bit BMP; <paramref name="gap"/> extra bytes sit between the header and the pixels.</summary>
    public static byte[] Bmp(int width, int height, int gap = 0)
    {
        var rowBytes = ((width * 24) + 31) / 32 * 4;
        var pixelOffset = 54 + gap;
        var size = pixelOffset + (rowBytes * height);
        var bmp = new byte[size];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), (uint)pixelOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), height);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(28), 24);
        for (var i = pixelOffset; i < size; i++)
        {
            bmp[i] = (byte)(i * 7);
        }

        return bmp;
    }

    /// <summary>A real ZIP archive with the given text entries.</summary>
    public static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }
}
