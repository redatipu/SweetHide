namespace Magi.Core.Imaging;

/// <summary>
/// An 8-bit RGBA image held in memory: unpremultiplied, row-major, 4 bytes per pixel, no row padding.
/// </summary>
public sealed class PixelBuffer
{
    private readonly byte[] _rgba;

    public PixelBuffer(int width, int height, byte[] rgba)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(rgba);
        if ((long)width * height * 4 != rgba.LongLength)
        {
            throw new ArgumentException($"Expected {(long)width * height * 4} bytes for a {width}x{height} RGBA image, got {rgba.LongLength}.", nameof(rgba));
        }

        Width = width;
        Height = height;
        _rgba = rgba;
    }

    public int Width { get; }

    public int Height { get; }

    public long PixelCount => (long)Width * Height;

    /// <summary>The raw RGBA bytes. Writes go straight into this image.</summary>
    public Span<byte> Pixels => _rgba;

    internal byte[] Data => _rgba;

    /// <summary>True when every pixel has alpha 255.</summary>
    public bool IsOpaque
    {
        get
        {
            for (var i = 3; i < _rgba.Length; i += 4)
            {
                if (_rgba[i] != 255)
                {
                    return false;
                }
            }

            return true;
        }
    }

    public PixelBuffer Clone() => new(Width, Height, (byte[])_rgba.Clone());
}
