using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace SweetHide.Forensics.Lsb;

/// <summary>Which channels of each pixel contribute a bit, in the order they are read.</summary>
public enum LsbChannels
{
    R,
    G,
    B,
    A,
    Rgb,
    Bgr,
    Rgba,
    Argb,
}

/// <summary>The order pixels are visited in.</summary>
public enum PixelOrder
{
    /// <summary>Left to right, then top to bottom (zsteg "xy").</summary>
    RowMajor,

    /// <summary>Top to bottom, then left to right (zsteg "yx").</summary>
    ColumnMajor,
}

/// <summary>How extracted bits are packed into bytes.</summary>
public enum BitOrder
{
    /// <summary>The first bit read becomes bit 7 of the byte (most tools).</summary>
    MsbFirst,

    /// <summary>The first bit read becomes bit 0 of the byte.</summary>
    LsbFirst,
}

/// <summary>One way of reading hidden bits out of an image.</summary>
/// <param name="BitPlane">0 = least significant bit, 7 = most significant.</param>
/// <param name="Channels">Channels read from each pixel, in order.</param>
/// <param name="Order">Pixel traversal order.</param>
/// <param name="BitOrder">Bit packing order.</param>
public sealed record LsbConfig(int BitPlane, LsbChannels Channels, PixelOrder Order, BitOrder BitOrder)
{
    /// <summary>Short zsteg-like name, e.g. "bit0,rgb,msb,xy". <see cref="Parse"/> accepts it back.</summary>
    public string Name => string.Create(
        CultureInfo.InvariantCulture,
        $"bit{BitPlane},{Channels.ToString().ToLowerInvariant()},{(BitOrder == BitOrder.MsbFirst ? "msb" : "lsb")},{(Order == PixelOrder.RowMajor ? "xy" : "yx")}");

    /// <summary>Number of channels read from each pixel.</summary>
    public int ChannelCount => ChannelOffsets(Channels).Length;

    public override string ToString() => Name;

    /// <summary>Parses "bit0,rgb,msb,xy" (bit order and pixel order may be omitted: msb and xy).</summary>
    /// <exception cref="FormatException">The text is not a valid configuration.</exception>
    public static LsbConfig Parse(string text) =>
        TryParse(text, out var config) ? config : throw new FormatException($"Not an LSB configuration: '{text}'. Expected e.g. bit0,rgb,msb,xy.");

    public static bool TryParse(string? text, [NotNullWhen(true)] out LsbConfig? config)
    {
        config = null;
        var parts = (text ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 4
            || !parts[0].StartsWith("bit", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(parts[0].AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var plane)
            || plane is < 0 or > 7
            || !parts[1].All(char.IsAsciiLetter)
            || !Enum.TryParse<LsbChannels>(parts[1], ignoreCase: true, out var channels))
        {
            return false;
        }

        var bitOrder = BitOrder.MsbFirst;
        var order = PixelOrder.RowMajor;
        foreach (var part in parts.Skip(2))
        {
            switch (part.ToLowerInvariant())
            {
                case "msb": bitOrder = BitOrder.MsbFirst; break;
                case "lsb": bitOrder = BitOrder.LsbFirst; break;
                case "xy": order = PixelOrder.RowMajor; break;
                case "yx": order = PixelOrder.ColumnMajor; break;
                default: return false;
            }
        }

        config = new LsbConfig(plane, channels, order, bitOrder);
        return true;
    }

    /// <summary>Byte offsets within an RGBA pixel, in reading order.</summary>
    internal static ReadOnlySpan<int> ChannelOffsets(LsbChannels channels) => channels switch
    {
        LsbChannels.R => [0],
        LsbChannels.G => [1],
        LsbChannels.B => [2],
        LsbChannels.A => [3],
        LsbChannels.Rgb => [0, 1, 2],
        LsbChannels.Bgr => [2, 1, 0],
        LsbChannels.Rgba => [0, 1, 2, 3],
        LsbChannels.Argb => [3, 0, 1, 2],
        _ => throw new ArgumentOutOfRangeException(nameof(channels), channels, null),
    };

    internal static bool UsesAlpha(LsbChannels channels) => channels is LsbChannels.A or LsbChannels.Rgba or LsbChannels.Argb;
}
