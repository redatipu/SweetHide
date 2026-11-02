using SweetHide.Core;
using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;

namespace SweetHide.Presentation.Services;

/// <summary>Builds a sample stego image on the fly for the first-run tour (no binary sample in the repo).</summary>
public static class SampleFactory
{
    public const string Password = "sweethide";
    public const string FileName = "sweethide-sample.png";

    public static byte[] CreateSamplePng(StegoEngine engine, IImageCodec codec)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(codec);
        var payload = new Payload(
        [
            PayloadEntry.FromText(Strings.SampleMessage),
            PayloadEntry.FromFile("hello.txt", System.Text.Encoding.UTF8.GetBytes(Strings.SampleFile)),
        ]);
        var stego = engine.Embed(SunsetCover(640, 400), payload, Password);
        using var stream = new MemoryStream();
        codec.EncodePng(stego, stream);
        return stream.ToArray();
    }

    /// <summary>A soft pink-to-violet sky with a sun and a little noise, so it behaves like a photo.</summary>
    internal static PixelBuffer SunsetCover(int width, int height)
    {
        var random = new Random(2026);
        var data = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var t = (double)y / height;
            for (var x = 0; x < width; x++)
            {
                var sun = Math.Max(0, 1 - (Math.Sqrt(Math.Pow(x - (width * 0.68), 2) + Math.Pow(y - (height * 0.58), 2)) / (height * 0.22)));
                var i = ((y * width) + x) * 4;
                data[i] = Clamp((255 * (1 - (t * 0.35))) + (sun * 40) + random.Next(-5, 6));
                data[i + 1] = Clamp(110 + (t * 50) + (sun * 120) + random.Next(-5, 6));
                data[i + 2] = Clamp(180 - (t * 40) + (sun * 10) + random.Next(-5, 6));
                data[i + 3] = 255;
            }
        }

        return new PixelBuffer(width, height, data);
    }

    private static byte Clamp(double value) => (byte)Math.Clamp((int)value, 0, 255);
}
