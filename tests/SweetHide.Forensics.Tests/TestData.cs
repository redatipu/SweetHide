using System.Buffers.Binary;
using System.Text;

using SweetHide.Core.Imaging;
using SweetHide.Forensics.Lsb;

namespace SweetHide.Forensics.Tests;

/// <summary>Builds test images and files in code, so the repository needs no binary fixtures.</summary>
internal static class TestData
{
    public static readonly SkiaImageCodec Codec = new();

    /// <summary>The running test's token, so a cancelled test run stops long scans promptly.</summary>
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A photo-like image: smooth gradients plus mild noise, so LSBs look natural.</summary>
    public static PixelBuffer Photo(int width, int height, int seed = 1)
    {
        var random = new Random(seed);
        var data = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                data[i] = Clamp((x * 255 / Math.Max(1, width - 1)) + random.Next(-6, 7));
                data[i + 1] = Clamp((y * 255 / Math.Max(1, height - 1)) + random.Next(-6, 7));
                data[i + 2] = Clamp(128 + (int)(60 * Math.Sin((x + y) / 9.0)) + random.Next(-6, 7));
                data[i + 3] = 255;
            }
        }

        return new PixelBuffer(width, height, data);
    }

    public static PixelBuffer Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var data = new byte[width * height * 4];
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = r;
            data[i + 1] = g;
            data[i + 2] = b;
            data[i + 3] = a;
        }

        return new PixelBuffer(width, height, data);
    }

    /// <summary>
    /// Classic LSB replacement as simple CTF tools do it, written independently of the scanner's extractor:
    /// visits pixels in the given order and overwrites one bit per selected channel.
    /// </summary>
    public static PixelBuffer HideBits(PixelBuffer cover, byte[] payload, LsbConfig config)
    {
        var image = cover.Clone();
        var pixels = image.Pixels;
        int[] channels = config.Channels switch
        {
            LsbChannels.R => [0],
            LsbChannels.G => [1],
            LsbChannels.B => [2],
            LsbChannels.A => [3],
            LsbChannels.Rgb => [0, 1, 2],
            LsbChannels.Bgr => [2, 1, 0],
            LsbChannels.Rgba => [0, 1, 2, 3],
            _ => [3, 0, 1, 2],
        };

        var bitIndex = 0;
        var totalBits = payload.Length * 8;
        for (var outer = 0; outer < (config.Order == PixelOrder.RowMajor ? image.Height : image.Width) && bitIndex < totalBits; outer++)
        {
            for (var inner = 0; inner < (config.Order == PixelOrder.RowMajor ? image.Width : image.Height) && bitIndex < totalBits; inner++)
            {
                var (x, y) = config.Order == PixelOrder.RowMajor ? (inner, outer) : (outer, inner);
                var pixel = ((y * image.Width) + x) * 4;
                foreach (var channel in channels)
                {
                    if (bitIndex >= totalBits)
                    {
                        break;
                    }

                    var b = payload[bitIndex / 8];
                    var shift = config.BitOrder == BitOrder.MsbFirst ? 7 - (bitIndex % 8) : bitIndex % 8;
                    var bit = (b >> shift) & 1;
                    var mask = (byte)(1 << config.BitPlane);
                    pixels[pixel + channel] = (byte)((pixels[pixel + channel] & ~mask) | (bit << config.BitPlane));
                    bitIndex++;
                }
            }
        }

        if (bitIndex < totalBits)
        {
            throw new ArgumentException("Payload does not fit.", nameof(payload));
        }

        return image;
    }

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    public static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    public static byte[] U32BigEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    public static byte[] U32LittleEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    public static byte[] RandomBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    public static byte[] EncodePng(PixelBuffer image)
    {
        using var stream = new MemoryStream();
        Codec.EncodePng(image, stream);
        return stream.ToArray();
    }

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);
}
