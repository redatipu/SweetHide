using SweetHide.Core.Imaging;

namespace SweetHide.Core.Stego;

/// <summary>
/// Enumerates the colour channels that may carry data: R, G and B of every pixel whose alpha is not 0.
/// Fully transparent pixels are skipped (their colour is often discarded by editors and optimisers), and the
/// alpha channel itself is never modified, so the same slots are found again when revealing.
/// </summary>
internal sealed class SlotMap
{
    private const int ChannelsPerPixel = 3;

    // Index of each usable pixel, or null when every pixel is usable (the common, opaque case).
    private readonly int[]? _pixelIndex;

    public SlotMap(PixelBuffer image)
    {
        var data = image.Data;
        var pixelCount = (int)image.PixelCount;

        var usable = 0;
        for (var i = 3; i < data.Length; i += 4)
        {
            if (data[i] != 0)
            {
                usable++;
            }
        }

        if (usable != pixelCount)
        {
            _pixelIndex = new int[usable];
            var n = 0;
            for (var p = 0; p < pixelCount; p++)
            {
                if (data[(p * 4) + 3] != 0)
                {
                    _pixelIndex[n++] = p;
                }
            }
        }

        UsablePixels = usable;
        Count = (long)usable * ChannelsPerPixel;
    }

    public long UsablePixels { get; }

    /// <summary>Number of usable channel slots.</summary>
    public long Count { get; }

    /// <summary>Byte offset into the RGBA array for a slot index.</summary>
    public int ByteOffset(long slot)
    {
        var pixel = slot / ChannelsPerPixel;
        var channel = (int)(slot % ChannelsPerPixel);
        var p = _pixelIndex is null ? (int)pixel : _pixelIndex[pixel];
        return (p * 4) + channel;
    }
}
