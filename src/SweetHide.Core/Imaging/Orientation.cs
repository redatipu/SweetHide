using SkiaSharp;

namespace SweetHide.Core.Imaging;

/// <summary>Applies an EXIF orientation to raw pixels (pure index remapping, no resampling).</summary>
internal static class Orientation
{
    public static PixelBuffer Apply(PixelBuffer source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return source;
        }

        var sw = source.Width;
        var sh = source.Height;
        var swapsAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var ow = swapsAxes ? sh : sw;
        var oh = swapsAxes ? sw : sh;

        var src = source.Data;
        var dst = new byte[src.Length];
        for (var oy = 0; oy < oh; oy++)
        {
            for (var ox = 0; ox < ow; ox++)
            {
                var (sx, sy) = origin switch
                {
                    SKEncodedOrigin.TopRight => (sw - 1 - ox, oy),             // mirror horizontal
                    SKEncodedOrigin.BottomRight => (sw - 1 - ox, sh - 1 - oy), // rotate 180
                    SKEncodedOrigin.BottomLeft => (ox, sh - 1 - oy),           // mirror vertical
                    SKEncodedOrigin.LeftTop => (oy, ox),                       // transpose
                    SKEncodedOrigin.RightTop => (oy, sh - 1 - ox),             // rotate 90 CW
                    SKEncodedOrigin.RightBottom => (sw - 1 - oy, sh - 1 - ox), // transverse
                    SKEncodedOrigin.LeftBottom => (sw - 1 - oy, ox),           // rotate 90 CCW
                    _ => (ox, oy),
                };

                var s = ((sy * sw) + sx) * 4;
                var d = ((oy * ow) + ox) * 4;
                dst[d] = src[s];
                dst[d + 1] = src[s + 1];
                dst[d + 2] = src[s + 2];
                dst[d + 3] = src[s + 3];
            }
        }

        return new PixelBuffer(ow, oh, dst);
    }
}
