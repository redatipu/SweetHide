using SweetHide.Core.Imaging;

namespace SweetHide.Core.Stego;

/// <summary>
/// The low bits of R, G and B of every pixel whose alpha is not 0, written with LSB matching.
/// </summary>
/// <remarks>
/// Slot <c>s</c> is channel <c>s % 3</c> of the <c>s / 3</c>-th usable pixel in raster order. Fully transparent pixels
/// are skipped (editors and optimisers often discard their colour) and alpha is never modified, so the same slots
/// are found again when revealing.
/// <para>
/// Texture for adaptive embedding uses a checkerboard: pixels with <c>x + y</c> odd are reference pixels and are never
/// written once textures are in use; a pixel with <c>x + y</c> even gets, per channel, the range (max - min) of its
/// usable up/down/left/right neighbours, all of which are reference pixels.
/// </para>
/// </remarks>
internal sealed class RgbLsbCarrier : IAdaptiveCarrier
{
    private const int ChannelsPerPixel = 3;

    private readonly byte[] _data;
    private readonly int _width;
    private readonly int _height;

    // Index of each usable pixel, or null when every pixel is usable (the common, opaque case).
    private readonly int[]? _pixelIndex;

    /// <param name="image">Writes go straight into this image's pixels.</param>
    public RgbLsbCarrier(PixelBuffer image)
    {
        ArgumentNullException.ThrowIfNull(image);
        _data = image.Data;
        _width = image.Width;
        _height = image.Height;
        var pixelCount = (int)image.PixelCount;

        var usable = 0;
        for (var i = 3; i < _data.Length; i += 4)
        {
            if (_data[i] != 0)
            {
                usable++;
            }
        }

        long texturedPixels = 0;
        if (usable != pixelCount)
        {
            _pixelIndex = new int[usable];
            var n = 0;
            for (var p = 0; p < pixelCount; p++)
            {
                if (_data[(p * 4) + 3] != 0)
                {
                    _pixelIndex[n++] = p;
                    if (IsTexturedPixel(p))
                    {
                        texturedPixels++;
                    }
                }
            }
        }
        else
        {
            // Pixels with x + y even: rows starting at an even y have one extra when the width is odd.
            for (var y = 0; y < _height; y++)
            {
                texturedPixels += (_width + (y % 2 == 0 ? 1 : 0)) / 2;
            }
        }

        UsablePixels = usable;
        SlotCount = (long)usable * ChannelsPerPixel;
        TexturedSlotCount = texturedPixels * ChannelsPerPixel;
    }

    public long UsablePixels { get; }

    public long SlotCount { get; }

    public int MaxBitsPerSlot => 4;

    public long TexturedSlotCount { get; }

    public int Read(long slot, int bits) => _data[ByteOffset(slot)] & ((1 << bits) - 1);

    public void Write(long slot, int value, int bits, bool coin)
    {
        var offset = ByteOffset(slot);
        _data[offset] = LsbMatching.Embed(_data[offset], value, bits, coin);
    }

    public int Texture(long slot)
    {
        var pixel = Pixel(slot);
        if (!IsTexturedPixel(pixel))
        {
            return -1;
        }

        var channel = (int)(slot % ChannelsPerPixel);
        var x = pixel % _width;
        var y = pixel / _width;
        int min = 255, max = 0, count = 0;
        Visit(x - 1, y);
        Visit(x + 1, y);
        Visit(x, y - 1);
        Visit(x, y + 1);
        return count < 2 ? 0 : max - min;

        void Visit(int nx, int ny)
        {
            if (nx < 0 || ny < 0 || nx >= _width || ny >= _height)
            {
                return;
            }

            var offset = (((ny * _width) + nx) * 4) + channel;
            if (_data[offset - channel + 3] == 0)
            {
                return; // transparent: its colour is not reliable after re-encoding
            }

            var value = _data[offset];
            min = Math.Min(min, value);
            max = Math.Max(max, value);
            count++;
        }
    }

    /// <summary>Byte offset into the RGBA array for a slot index.</summary>
    public int ByteOffset(long slot) => (Pixel(slot) * 4) + (int)(slot % ChannelsPerPixel);

    private int Pixel(long slot)
    {
        var pixel = slot / ChannelsPerPixel;
        return _pixelIndex is null ? (int)pixel : _pixelIndex[pixel];
    }

    private bool IsTexturedPixel(int pixel) => ((pixel % _width) + (pixel / _width)) % 2 == 0;
}
