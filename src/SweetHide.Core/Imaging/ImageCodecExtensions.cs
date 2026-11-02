namespace SweetHide.Core.Imaging;

public static class ImageCodecExtensions
{
    /// <summary>
    /// Encodes to a temporary file next to <paramref name="path"/> and then moves it into place, so a failure never
    /// leaves a half-written image (or destroys the existing one).
    /// </summary>
    public static void SavePng(this IImageCodec codec, PixelBuffer image, string path, bool overwrite) =>
        codec.SaveLossless(image, path, LosslessFormat.Png, overwrite);

    /// <summary>
    /// Atomic save in the lossless format implied by the extension (.png, .webp, .bmp, .tif/.tiff).
    /// </summary>
    /// <exception cref="ArgumentException">The extension isn't one of the lossless formats.</exception>
    public static void SaveImage(this IImageCodec codec, PixelBuffer image, string path, bool overwrite)
    {
        var format = LosslessFormats.FromPath(path)
            ?? throw new ArgumentException($"{Path.GetExtension(path)} is not a lossless format; use .png, .webp, .bmp or .tiff.", nameof(path));
        codec.SaveLossless(image, path, format, overwrite);
    }

    /// <summary>
    /// Like <see cref="SaveImage(IImageCodec, PixelBuffer, string, bool)"/>, and when <paramref name="exif"/> is
    /// given (see <see cref="ExifMetadata.ForOutput"/>) it goes into the file as well. Only PNG carries EXIF here.
    /// </summary>
    /// <exception cref="ArgumentException">Not a lossless extension, or EXIF requested for a format other than PNG.</exception>
    public static void SaveImage(this IImageCodec codec, PixelBuffer image, string path, bool overwrite, byte[]? exif)
    {
        if (exif is null)
        {
            codec.SaveImage(image, path, overwrite);
            return;
        }

        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(image);
        if (LosslessFormats.FromPath(path) is not { } format || !LosslessFormats.SupportsExif(format))
        {
            throw new ArgumentException("EXIF can only be kept in PNG output.", nameof(path));
        }

        using var png = new MemoryStream();
        codec.EncodePng(image, png);
        var bytes = ExifMetadata.AddToPng(png.GetBuffer().AsSpan(0, (int)png.Length), exif);
        WriteAtomically(path, overwrite, stream => stream.Write(bytes));
    }

    public static void SaveLossless(this IImageCodec codec, PixelBuffer image, string path, LosslessFormat format, bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(image);
        WriteAtomically(path, overwrite, stream => LosslessFormats.Encode(codec, image, format, stream));
    }

    /// <summary>Writes to a temporary file next to <paramref name="path"/>, then moves it into place.</summary>
    public static void WriteAtomically(string path, bool overwrite, Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(write);

        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full) ?? throw new IOException($"Invalid output path: {path}");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                write(stream);
            }

            File.Move(temp, full, overwrite);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
