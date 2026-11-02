using System.Runtime.InteropServices;

using SkiaSharp;

namespace SweetHide.Core.Imaging;

/// <summary>Output formats that keep every bit of every pixel, so hidden data survives.</summary>
public enum LosslessFormat
{
    Png,
    WebP,
    Bmp,
    Tiff,
}

/// <summary>Picks a lossless format from a file name and encodes into it.</summary>
public static class LosslessFormats
{
    public static IReadOnlyList<LosslessFormat> All { get; } = [LosslessFormat.Png, LosslessFormat.WebP, LosslessFormat.Bmp, LosslessFormat.Tiff];

    public static string Extension(LosslessFormat format) => format switch
    {
        LosslessFormat.WebP => ".webp",
        LosslessFormat.Bmp => ".bmp",
        LosslessFormat.Tiff => ".tiff",
        _ => ".png",
    };

    /// <summary>The format for a path's extension, or null if the extension isn't a lossless one we write.</summary>
    public static LosslessFormat? FromPath(string path) => Path.GetExtension(path).ToUpperInvariant() switch
    {
        ".PNG" => LosslessFormat.Png,
        ".WEBP" => LosslessFormat.WebP,
        ".BMP" => LosslessFormat.Bmp,
        ".TIF" or ".TIFF" => LosslessFormat.Tiff,
        _ => null,
    };

    public static void Encode(IImageCodec codec, PixelBuffer image, LosslessFormat format, Stream output)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(output);
        switch (format)
        {
            case LosslessFormat.WebP:
                EncodeWebPLossless(image, output);
                break;
            case LosslessFormat.Bmp:
                BmpWriter.Write(image, output);
                break;
            case LosslessFormat.Tiff:
                TiffCodec.Write(image, output);
                break;
            default:
                codec.EncodePng(image, output);
                break;
        }
    }

    private static void EncodeWebPLossless(PixelBuffer image, Stream output)
    {
        var alphaType = image.IsOpaque ? SKAlphaType.Opaque : SKAlphaType.Unpremul;
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, alphaType);
        var handle = GCHandle.Alloc(image.Data, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), info.RowBytes);
            using var encoded = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100))
                ?? throw new InvalidImageException("WebP encoding failed.");
            encoded.SaveTo(output);
        }
        finally
        {
            handle.Free();
        }
    }
}

/// <summary>Uncompressed BMP: 24-bit when opaque, otherwise 32-bit with an alpha mask (BITMAPV4HEADER).</summary>
internal static class BmpWriter
{
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;
    private const int V4HeaderSize = 108;

    public static void Write(PixelBuffer image, Stream output)
    {
        var withAlpha = !image.IsOpaque;
        var bytesPerPixel = withAlpha ? 4 : 3;
        var rowSize = ((image.Width * bytesPerPixel) + 3) & ~3;
        var headerSize = withAlpha ? V4HeaderSize : InfoHeaderSize;
        var pixelOffset = FileHeaderSize + headerSize;
        var imageSize = (long)rowSize * image.Height;
        if (pixelOffset + imageSize > uint.MaxValue)
        {
            throw new InvalidImageException("Image too large for BMP.");
        }

        using var writer = new BinaryWriter(output, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write((uint)(pixelOffset + imageSize));
        writer.Write(0u);
        writer.Write((uint)pixelOffset);

        writer.Write((uint)headerSize);
        writer.Write(image.Width);
        writer.Write(image.Height); // positive: rows stored bottom-up
        writer.Write((ushort)1);
        writer.Write((ushort)(bytesPerPixel * 8));
        writer.Write(withAlpha ? 3u : 0u); // BI_BITFIELDS : BI_RGB
        writer.Write((uint)imageSize);
        writer.Write(2835); // 72 dpi
        writer.Write(2835);
        writer.Write(0u);
        writer.Write(0u);
        if (withAlpha)
        {
            writer.Write(0x00FF0000u); // red mask
            writer.Write(0x0000FF00u); // green
            writer.Write(0x000000FFu); // blue
            writer.Write(0xFF000000u); // alpha
            writer.Write(0x73524742u); // LCS_sRGB
            writer.Write(new byte[36 + 12]); // endpoints + gamma
        }

        var data = image.Data;
        var row = new byte[rowSize];
        for (var y = image.Height - 1; y >= 0; y--)
        {
            var source = y * image.Width * 4;
            for (var x = 0; x < image.Width; x++)
            {
                var s = source + (x * 4);
                var d = x * bytesPerPixel;
                row[d] = data[s + 2];
                row[d + 1] = data[s + 1];
                row[d + 2] = data[s];
                if (withAlpha)
                {
                    row[d + 3] = data[s + 3];
                }
            }

            writer.Write(row);
        }
    }
}
