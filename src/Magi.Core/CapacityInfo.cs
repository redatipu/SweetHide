namespace Magi.Core;

/// <param name="Width">Image width in pixels.</param>
/// <param name="Height">Image height in pixels.</param>
/// <param name="UsablePixels">Pixels that are not fully transparent.</param>
/// <param name="BitsPerChannel">Low bits used in each R, G and B value.</param>
/// <param name="ContainerBytes">Total bytes the image can carry, including encryption overhead.</param>
public sealed record CapacityInfo(int Width, int Height, long UsablePixels, int BitsPerChannel, long ContainerBytes)
{
    /// <summary>Bytes left for the (compressed) payload once fixed overhead is subtracted.</summary>
    public long PayloadBytes => Math.Max(0, ContainerBytes - StegoEngine.ContainerOverhead);
}
