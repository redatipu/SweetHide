namespace SweetHide.Core.Imaging;

/// <summary>Reads common image formats into <see cref="PixelBuffer"/> and writes lossless PNG.</summary>
public interface IImageCodec
{
    /// <summary>Decodes PNG, JPEG, BMP, GIF (first frame) or WebP into unpremultiplied RGBA without colour management.</summary>
    /// <param name="input">The encoded image.</param>
    /// <param name="applyOrientation">
    /// Rotate/flip according to EXIF orientation. Use <c>true</c> for cover images (so the output looks the same as the
    /// photo did) and <c>false</c> when reading back a stego image, whose pixels must be read exactly as stored.
    /// </param>
    PixelBuffer Decode(Stream input, bool applyOrientation);

    /// <summary>Encodes losslessly as PNG with no metadata (no EXIF, no colour profile).</summary>
    void EncodePng(PixelBuffer image, Stream output);
}
