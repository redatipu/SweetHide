using System.Runtime.InteropServices;

using SkiaSharp;

namespace Magi.Core.Imaging;

/// <summary><see cref="IImageCodec"/> backed by SkiaSharp (MIT). Works on Windows, Linux and macOS.</summary>
public sealed class SkiaImageCodec : IImageCodec
{
    /// <summary>64 megapixels (e.g. 8000x8000). Guards against decompression bombs.</summary>
    public const long DefaultMaxPixels = 64L * 1024 * 1024;

    private readonly long _maxPixels;

    public SkiaImageCodec(long maxPixels = DefaultMaxPixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPixels);
        _maxPixels = maxPixels;
    }

    public PixelBuffer Decode(Stream input, bool applyOrientation)
    {
        ArgumentNullException.ThrowIfNull(input);

        using var data = SKData.Create(input) ?? throw new InvalidImageException("The file is empty or could not be read.");
        using var codec = SKCodec.Create(data) ?? throw new InvalidImageException("Unsupported or corrupt image file.");

        var width = codec.Info.Width;
        var height = codec.Info.Height;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidImageException("The image has no pixels.");
        }

        if ((long)width * height > _maxPixels)
        {
            throw new InvalidImageException($"The image is {width}x{height}; the limit is {_maxPixels:N0} pixels.");
        }

        // No colour space on the destination => Skia performs no colour conversion, so stored values are read verbatim.
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pixels = new byte[info.BytesSize64];
        var result = codec.GetPixels(info, pixels);
        if (result != SKCodecResult.Success)
        {
            throw new InvalidImageException($"Could not decode the image ({result}).");
        }

        var buffer = new PixelBuffer(width, height, pixels);
        return applyOrientation ? Orientation.Apply(buffer, codec.EncodedOrigin) : buffer;
    }

    public void EncodePng(PixelBuffer image, Stream output)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(output);

        // Opaque images are written as RGB (smaller, and a stray alpha channel would look unusual).
        var alphaType = image.IsOpaque ? SKAlphaType.Opaque : SKAlphaType.Unpremul;
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, alphaType);

        var handle = GCHandle.Alloc(image.Data, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), info.RowBytes);
            using var encoded = pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 9))
                ?? throw new InvalidImageException("PNG encoding failed.");
            encoded.SaveTo(output);
        }
        finally
        {
            handle.Free();
        }
    }
}
