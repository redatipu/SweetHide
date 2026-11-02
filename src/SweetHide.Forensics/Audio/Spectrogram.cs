using SweetHide.Core.Imaging;

namespace SweetHide.Forensics.Audio;

public enum SpectrogramPalette
{
    /// <summary>Black → red → yellow → white.</summary>
    Heat,

    Grayscale,
}

public sealed record SpectrogramOptions
{
    /// <summary>Window length; a power of two from 64 to 16384. The image is <c>FftSize / 2</c> pixels tall.</summary>
    public int FftSize { get; init; } = 1024;

    /// <summary>Samples between columns; null picks a quarter window, widened as needed to respect <see cref="MaxWidth"/>.</summary>
    public int? HopSize { get; init; }

    public int MaxWidth { get; init; } = 2048;

    /// <summary>Magnitudes this far below the loudest bin are drawn black.</summary>
    public double DynamicRangeDb { get; init; } = 90;

    public SpectrogramPalette Palette { get; init; } = SpectrogramPalette.Heat;

    public static SpectrogramOptions Default { get; } = new();
}

/// <summary>
/// Renders a spectrogram (time left to right, frequency bottom to top, log magnitude) as a PixelBuffer. Text and
/// pictures drawn into the spectrum are a CTF classic that no other check would notice.
/// </summary>
public static class Spectrogram
{
    public static PixelBuffer Render(WavAudio audio, SpectrogramOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        options ??= SpectrogramOptions.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(options.FftSize, 64);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.FftSize, 16384);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.DynamicRangeDb);

        var fft = new Fft(options.FftSize);
        var size = options.FftSize;
        var height = size / 2;
        var frames = audio.FrameCount;
        var span = Math.Max(0, frames - size);
        var hop = Math.Max(options.HopSize ?? size / 4, 1);
        if (span / hop + 1 > options.MaxWidth)
        {
            hop = (int)Math.Ceiling((double)span / Math.Max(1, options.MaxWidth - 1));
        }

        var width = (int)Math.Min(options.MaxWidth, (span / hop) + 1);
        var window = new double[size];
        for (var i = 0; i < size; i++)
        {
            window[i] = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / (size - 1))); // Hann
        }

        var decibels = new float[width * height];
        var real = new double[size];
        var imaginary = new double[size];
        var loudest = double.NegativeInfinity;
        for (var column = 0; column < width; column++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = (long)column * hop;
            for (var i = 0; i < size; i++)
            {
                var frame = start + i;
                real[i] = frame < frames ? Mix(audio, frame) * window[i] : 0;
                imaginary[i] = 0;
            }

            fft.Transform(real, imaginary);
            for (var bin = 0; bin < height; bin++)
            {
                var magnitude = Math.Sqrt((real[bin] * real[bin]) + (imaginary[bin] * imaginary[bin]));
                var db = 20 * Math.Log10(magnitude + 1e-12);
                decibels[(bin * width) + column] = (float)db;
                loudest = Math.Max(loudest, db);
            }
        }

        var floor = loudest - options.DynamicRangeDb;
        var pixels = new byte[width * height * 4];
        for (var bin = 0; bin < height; bin++)
        {
            var y = height - 1 - bin; // low frequencies at the bottom
            for (var column = 0; column < width; column++)
            {
                var level = Math.Clamp((decibels[(bin * width) + column] - floor) / options.DynamicRangeDb, 0, 1);
                Paint(pixels, ((y * width) + column) * 4, level, options.Palette);
            }
        }

        return new PixelBuffer(width, height, pixels);
    }

    private static double Mix(WavAudio audio, long frame)
    {
        double sum = 0;
        for (var channel = 0; channel < audio.Channels; channel++)
        {
            sum += audio.NormalizedSample(frame, channel);
        }

        return sum / audio.Channels;
    }

    private static void Paint(byte[] pixels, int i, double level, SpectrogramPalette palette)
    {
        if (palette == SpectrogramPalette.Grayscale)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)(255 * level);
        }
        else
        {
            pixels[i] = (byte)(255 * Math.Clamp(level * 3, 0, 1));
            pixels[i + 1] = (byte)(255 * Math.Clamp((level * 3) - 1, 0, 1));
            pixels[i + 2] = (byte)(255 * Math.Clamp((level * 3) - 2, 0, 1));
        }

        pixels[i + 3] = 255;
    }
}
