namespace SweetHide.Core.Imaging;

public static class ImageCodecExtensions
{
    /// <summary>
    /// Encodes to a temporary file next to <paramref name="path"/> and then moves it into place, so a failure never
    /// leaves a half-written image (or destroys the existing one).
    /// </summary>
    public static void SavePng(this IImageCodec codec, PixelBuffer image, string path, bool overwrite)
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
                codec.EncodePng(image, stream);
            }

            File.Move(temp, full, overwrite);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
