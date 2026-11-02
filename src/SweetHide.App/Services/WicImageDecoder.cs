using SweetHide.Core.Imaging;
using SweetHide.Presentation.Imaging;

using Windows.Graphics.Imaging;

namespace SweetHide.App.Services;

/// <summary>
/// Falls back to Windows Imaging Component for HEIC/HEIF, AVIF, TIFF, JPEG XR and anything else Windows can read
/// (HEIC needs the free "HEIF Image Extensions" from the Store, which most Windows 11 PCs already have).
/// </summary>
internal sealed class WicImageDecoder(long maxPixels = SkiaImageCodec.DefaultMaxPixels) : IFallbackImageDecoder
{
    public async Task<PixelBuffer?> TryDecodeAsync(byte[] bytes, bool applyOrientation)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false).AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var width = applyOrientation ? decoder.OrientedPixelWidth : decoder.PixelWidth;
            var height = applyOrientation ? decoder.OrientedPixelHeight : decoder.PixelHeight;
            if (width == 0 || height == 0 || (long)width * height > maxPixels)
            {
                return null;
            }

            var data = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Rgba8,
                BitmapAlphaMode.Straight,
                new BitmapTransform(),
                applyOrientation ? ExifOrientationMode.RespectExifOrientation : ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            return new PixelBuffer((int)width, (int)height, data.DetachPixelData());
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or OverflowException)
        {
            return null; // no codec for this format, or not an image
        }
    }
}
