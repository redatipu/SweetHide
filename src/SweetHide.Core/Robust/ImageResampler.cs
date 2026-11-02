using SweetHide.Core.Imaging;

namespace SweetHide.Core.Robust;

/// <summary>
/// Separable bicubic (Catmull–Rom) resampling whose kernel widens when shrinking, like Pillow's, so downscaled
/// photos are antialiased rather than aliased. Opaque output; alpha is composited over white first.
/// </summary>
internal static class ImageResampler
{
    private const double Support = 2.0;

    public static PixelBuffer Resize(PixelBuffer source, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var flat = source.IsOpaque ? source : Flatten(source);
        if (width == source.Width && height == source.Height)
        {
            return ReferenceEquals(flat, source) ? source.Clone() : flat;
        }

        var horizontal = Weights(source.Width, width);
        var vertical = Weights(source.Height, height);
        var src = flat.Data;
        var srcWidth = source.Width;

        // Horizontal pass into a float buffer (3 channels), then vertical pass into bytes.
        var temp = new float[(long)width * source.Height * 3];
        Parallel.For(0, source.Height, y =>
        {
            var rowStart = (long)y * srcWidth * 4;
            for (var x = 0; x < width; x++)
            {
                var (first, w) = horizontal[x];
                float r = 0, g = 0, b = 0;
                for (var k = 0; k < w.Length; k++)
                {
                    var p = rowStart + ((first + k) * 4L);
                    r += w[k] * src[p];
                    g += w[k] * src[p + 1];
                    b += w[k] * src[p + 2];
                }

                var t = ((long)y * width + x) * 3;
                temp[t] = r;
                temp[t + 1] = g;
                temp[t + 2] = b;
            }
        });

        var output = new byte[(long)width * height * 4];
        Parallel.For(0, height, y =>
        {
            var (first, w) = vertical[y];
            for (var x = 0; x < width; x++)
            {
                float r = 0, g = 0, b = 0;
                for (var k = 0; k < w.Length; k++)
                {
                    var t = ((long)(first + k) * width + x) * 3;
                    r += w[k] * temp[t];
                    g += w[k] * temp[t + 1];
                    b += w[k] * temp[t + 2];
                }

                var o = ((long)y * width + x) * 4;
                output[o] = ToByte(r);
                output[o + 1] = ToByte(g);
                output[o + 2] = ToByte(b);
                output[o + 3] = 255;
            }
        });

        return new PixelBuffer(width, height, output);
    }

    /// <summary>Composites over white so transparent areas don't turn into whatever colour was hidden under them.</summary>
    private static PixelBuffer Flatten(PixelBuffer source)
    {
        var result = source.Clone();
        var data = result.Data;
        for (var i = 0; i < data.Length; i += 4)
        {
            var alpha = data[i + 3];
            if (alpha == 255)
            {
                continue;
            }

            for (var c = 0; c < 3; c++)
            {
                data[i + c] = (byte)(((data[i + c] * alpha) + (255 * (255 - alpha)) + 127) / 255);
            }

            data[i + 3] = 255;
        }

        return result;
    }

    /// <summary>For each output sample: the first input index and the normalised filter weights.</summary>
    private static (int First, float[] Weights)[] Weights(int inputSize, int outputSize)
    {
        var scale = (double)inputSize / outputSize;
        var filterScale = Math.Max(scale, 1.0);
        var support = Support * filterScale;
        var result = new (int, float[])[outputSize];
        for (var i = 0; i < outputSize; i++)
        {
            var center = (i + 0.5) * scale;
            var first = Math.Max(0, (int)Math.Floor(center - support));
            var last = Math.Min(inputSize - 1, (int)Math.Ceiling(center + support));
            var weights = new double[last - first + 1];
            var total = 0.0;
            for (var j = first; j <= last; j++)
            {
                var w = CatmullRom((j + 0.5 - center) / filterScale);
                weights[j - first] = w;
                total += w;
            }

            result[i] = (first, [.. weights.Select(w => (float)(w / total))]);
        }

        return result;
    }

    private static double CatmullRom(double x)
    {
        x = Math.Abs(x);
        return x < 1 ? ((1.5 * x) - 2.5) * x * x + 1
            : x < 2 ? (((-0.5 * x) + 2.5) * x - 4) * x + 2
            : 0;
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);
}
