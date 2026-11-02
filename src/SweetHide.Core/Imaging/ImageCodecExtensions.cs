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

    public static void SaveLossless(this IImageCodec codec, PixelBuffer image, string path, LosslessFormat format, bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full) ?? throw new IOException($"Invalid output path: {path}");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                LosslessFormats.Encode(codec, image, format, stream);
            }

            File.Move(temp, full, overwrite);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
