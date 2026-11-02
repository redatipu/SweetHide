using SweetHide.Core;
using SweetHide.Core.Imaging;

namespace SweetHide.Presentation.Imaging;

/// <summary>A decoded image plus what the UI needs to show it.</summary>
public sealed class LoadedImage
{
    private readonly long[] _capacity;

    internal LoadedImage(string name, string? filePath, byte[] encodedBytes, PixelBuffer pixels, IndexedImage? indexed = null)
    {
        Name = name;
        FilePath = filePath;
        EncodedBytes = encodedBytes;
        Pixels = pixels;
        Indexed = indexed;
        HasExif = ExifMetadata.Extract(encodedBytes) is not null;
        _capacity = [.. StegoEngine.SupportedBitsPerChannel.Select(bits => StegoEngine.GetCapacity(pixels, bits).ContainerBytes)];
    }

    public string Name { get; }

    /// <summary>Where it was loaded from; null for clipboard images.</summary>
    public string? FilePath { get; }

    /// <summary>The original file bytes, for the preview.</summary>
    public ReadOnlyMemory<byte> EncodedBytes { get; }

    public PixelBuffer Pixels { get; }

    /// <summary>The palette version of a 256-colour PNG (hiding keeps it a palette image), or null.</summary>
    public IndexedImage? Indexed { get; }

    public bool IsPalette => Indexed is not null;

    /// <summary>The file has EXIF (camera, date, maybe location) that hiding could carry over without GPS.</summary>
    public bool HasExif { get; }

    public int Width => Pixels.Width;

    public int Height => Pixels.Height;

    /// <summary>Container bytes available at the given bits per channel (precomputed off the UI thread).</summary>
    public long CapacityFor(int bitsPerChannel) => _capacity[bitsPerChannel - 1];
}

public enum ImagePurpose
{
    /// <summary>A cover to hide data in: EXIF orientation is applied so the result looks like the photo.</summary>
    Cover,

    /// <summary>An image to reveal from or analyse: pixels are read exactly as stored.</summary>
    Inspect,
}

/// <summary>
/// A second decoder for formats SkiaSharp can't read (HEIC/HEIF from iPhones, AVIF, TIFF, JPEG XR). On Windows this is
/// WIC, which uses whatever codecs the PC has installed.
/// </summary>
public interface IFallbackImageDecoder
{
    /// <returns>Unpremultiplied RGBA pixels, or null if this decoder can't read the bytes either.</returns>
    Task<PixelBuffer?> TryDecodeAsync(byte[] bytes, bool applyOrientation);
}

/// <summary>Reads and decodes images on a background thread.</summary>
public sealed class ImageLoader(IImageCodec codec, IFallbackImageDecoder? fallback = null)
{
    public const long MaxFileBytes = 256L * 1024 * 1024;

    public async Task<LoadedImage> LoadFileAsync(string path, ImagePurpose purpose)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes)
        {
            throw new InvalidImageException(Strings.Format(Strings.FileTooLarge, info.Name, ByteSize.Format(MaxFileBytes)));
        }

        var bytes = await File.ReadAllBytesAsync(info.FullName);
        return await DecodeAsync(info.Name, info.FullName, bytes, purpose);
    }

    public Task<LoadedImage> LoadBytesAsync(string name, byte[] bytes, ImagePurpose purpose) => DecodeAsync(name, null, bytes, purpose);

    private async Task<LoadedImage> DecodeAsync(string name, string? path, byte[] bytes, ImagePurpose purpose)
    {
        var applyOrientation = purpose == ImagePurpose.Cover;
        PixelBuffer pixels;
        try
        {
            pixels = await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes, writable: false);
                return codec.Decode(stream, applyOrientation);
            });
        }
        catch (InvalidImageException) when (fallback is not null)
        {
            pixels = await fallback.TryDecodeAsync(bytes, applyOrientation)
                ?? throw new InvalidImageException(Strings.Format(Strings.ImageLoadFailed, name));
        }

        return await Task.Run(() =>
        {
            MakeOpaqueIfFullyTransparent(pixels);
            return new LoadedImage(name, path, bytes, pixels, ReadPalette(bytes));
        });
    }

    /// <summary>The palette image inside a 256-colour PNG, or null (also for palette PNGs we can't read, e.g. interlaced).</summary>
    private static IndexedImage? ReadPalette(byte[] bytes)
    {
        if (!IndexedPng.IsIndexed(bytes))
        {
            return null;
        }

        try
        {
            return IndexedPng.Read(bytes);
        }
        catch (Exception ex) when (ex is InvalidImageException or ArgumentException)
        {
            return null; // the full-colour pixels still work
        }
    }

    /// <summary>
    /// Bitmaps pasted from the Windows clipboard are often 32-bit DIBs whose alpha is all zero. Treated literally
    /// that is an invisible image with no capacity, so an image with no visible pixel at all is taken as opaque.
    /// </summary>
    internal static void MakeOpaqueIfFullyTransparent(PixelBuffer pixels)
    {
        var data = pixels.Pixels;
        for (var i = 3; i < data.Length; i += 4)
        {
            if (data[i] != 0)
            {
                return;
            }
        }

        for (var i = 3; i < data.Length; i += 4)
        {
            data[i] = 255;
        }
    }
}
