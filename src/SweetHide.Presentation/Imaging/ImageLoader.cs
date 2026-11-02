using SweetHide.Core;
using SweetHide.Core.Imaging;

namespace SweetHide.Presentation.Imaging;

/// <summary>A decoded image plus what the UI needs to show it.</summary>
public sealed class LoadedImage
{
    private readonly long[] _capacity;

    internal LoadedImage(string name, string? filePath, byte[] encodedBytes, PixelBuffer pixels)
    {
        Name = name;
        FilePath = filePath;
        EncodedBytes = encodedBytes;
        Pixels = pixels;
        _capacity = [.. StegoEngine.SupportedBitsPerChannel.Select(bits => StegoEngine.GetCapacity(pixels, bits).ContainerBytes)];
    }

    public string Name { get; }

    /// <summary>Where it was loaded from; null for clipboard images.</summary>
    public string? FilePath { get; }

    /// <summary>The original file bytes, for the preview.</summary>
    public ReadOnlyMemory<byte> EncodedBytes { get; }

    public PixelBuffer Pixels { get; }

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

/// <summary>Reads and decodes images on a background thread.</summary>
public sealed class ImageLoader(IImageCodec codec)
{
    public const long MaxFileBytes = 256L * 1024 * 1024;

    public Task<LoadedImage> LoadFileAsync(string path, ImagePurpose purpose) => Task.Run(() =>
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes)
        {
            throw new InvalidImageException(Strings.Format(Strings.FileTooLarge, info.Name, ByteSize.Format(MaxFileBytes)));
        }

        return Decode(info.Name, info.FullName, File.ReadAllBytes(info.FullName), purpose);
    });

    public Task<LoadedImage> LoadBytesAsync(string name, byte[] bytes, ImagePurpose purpose) =>
        Task.Run(() => Decode(name, null, bytes, purpose));

    private LoadedImage Decode(string name, string? path, byte[] bytes, ImagePurpose purpose)
    {
        PixelBuffer pixels;
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            pixels = codec.Decode(stream, applyOrientation: purpose == ImagePurpose.Cover);
        }

        MakeOpaqueIfFullyTransparent(pixels);
        return new LoadedImage(name, path, bytes, pixels);
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
