using SweetHide.Core.Imaging;

namespace SweetHide.Core.Jpeg;

/// <summary>
/// The quantized DCT coefficients of a baseline JPEG, exactly as stored in the file, plus what is needed to write
/// them back (quantization tables, sampling factors, dimensions). Huffman tables are not kept: they only affect
/// file size, and <see cref="JpegEncoder.Write"/> builds new ones.
/// </summary>
/// <remarks>
/// Changing a coefficient here and writing the file again changes exactly that coefficient in the output, which
/// is what coefficient-domain steganography (see <see cref="JpegCoefficientSlots"/>) relies on.
/// </remarks>
public sealed class JpegCoefficients
{
    private readonly ushort[]?[] _quantizationTables;

    internal JpegCoefficients(int width, int height, IReadOnlyList<JpegComponent> components, ushort[]?[] quantizationTables)
    {
        Width = width;
        Height = height;
        Components = components;
        _quantizationTables = quantizationTables;
        MaxHorizontalSampling = components.Max(c => c.HorizontalSampling);
        MaxVerticalSampling = components.Max(c => c.VerticalSampling);
        McusWide = DivideRoundingUp(width, 8 * MaxHorizontalSampling);
        McusHigh = DivideRoundingUp(height, 8 * MaxVerticalSampling);
        foreach (var component in components)
        {
            component.Attach(this);
        }
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>One component (grayscale) or three (Y, Cb, Cr), in frame order.</summary>
    public IReadOnlyList<JpegComponent> Components { get; }

    public int MaxHorizontalSampling { get; }

    public int MaxVerticalSampling { get; }

    /// <summary>Number of MCU columns of an interleaved scan.</summary>
    public int McusWide { get; }

    /// <summary>Number of MCU rows of an interleaved scan.</summary>
    public int McusHigh { get; }

    /// <summary>True if a quantization table is defined in slot <paramref name="index"/> (0–3).</summary>
    public bool HasQuantizationTable(int index) => index is >= 0 and < 4 && _quantizationTables[index] is not null;

    /// <summary>A quantization table in natural (row-major) order.</summary>
    public ReadOnlySpan<ushort> GetQuantizationTable(int index) =>
        HasQuantizationTable(index)
            ? _quantizationTables[index]
            : throw new ArgumentOutOfRangeException(nameof(index), index, "No quantization table in this slot.");

    /// <summary>Deep copy: coefficients can be changed independently of the original.</summary>
    public JpegCoefficients Clone() =>
        new(
            Width,
            Height,
            [.. Components.Select(c => c.Clone())],
            [.. _quantizationTables.Select(t => t is null ? null : (ushort[])t.Clone())]);

    /// <summary>Decodes to RGBA for previews: float IDCT, triangle ("fancy") chroma upsampling, JFIF YCbCr→RGB.</summary>
    public PixelBuffer ToPixels() => JpegDecoder.ToPixels(this);

    internal static int DivideRoundingUp(int value, int divisor) => (value + divisor - 1) / divisor;
}

/// <summary>One colour component of a <see cref="JpegCoefficients"/>.</summary>
/// <remarks>
/// Blocks are stored for the whole MCU-padded grid (<see cref="BlocksWide"/> × <see cref="BlocksHigh"/>), 64
/// coefficients each in natural (row-major) order. Only the first <see cref="EncodedBlocksWide"/> ×
/// <see cref="EncodedBlocksHigh"/> blocks are guaranteed to be written by every encoder; the rest exist only in
/// interleaved scans and cover pixels outside the image.
/// </remarks>
public sealed class JpegComponent
{
    private readonly short[] _coefficients;
    private JpegCoefficients? _owner;

    internal JpegComponent(byte id, int horizontalSampling, int verticalSampling, int quantizationTableIndex, int blocksWide, int blocksHigh)
    {
        Id = id;
        HorizontalSampling = horizontalSampling;
        VerticalSampling = verticalSampling;
        QuantizationTableIndex = quantizationTableIndex;
        BlocksWide = blocksWide;
        BlocksHigh = blocksHigh;
        _coefficients = new short[(long)blocksWide * blocksHigh * JpegTables.BlockSize];
    }

    private JpegComponent(JpegComponent source)
        : this(source.Id, source.HorizontalSampling, source.VerticalSampling, source.QuantizationTableIndex, source.BlocksWide, source.BlocksHigh)
    {
        source._coefficients.CopyTo(_coefficients, 0);
    }

    public byte Id { get; }

    public int HorizontalSampling { get; }

    public int VerticalSampling { get; }

    public int QuantizationTableIndex { get; }

    /// <summary>Width of this component's sample grid: ⌈imageWidth · H / Hmax⌉.</summary>
    public int Width => JpegCoefficients.DivideRoundingUp(Owner.Width * HorizontalSampling, Owner.MaxHorizontalSampling);

    /// <summary>Height of this component's sample grid: ⌈imageHeight · V / Vmax⌉.</summary>
    public int Height => JpegCoefficients.DivideRoundingUp(Owner.Height * VerticalSampling, Owner.MaxVerticalSampling);

    /// <summary>Blocks per row as stored (padded to whole MCUs).</summary>
    public int BlocksWide { get; }

    /// <summary>Block rows as stored (padded to whole MCUs).</summary>
    public int BlocksHigh { get; }

    /// <summary>Blocks per row that cover image pixels (⌈<see cref="Width"/> / 8⌉).</summary>
    public int EncodedBlocksWide => JpegCoefficients.DivideRoundingUp(Width, 8);

    /// <summary>Block rows that cover image pixels (⌈<see cref="Height"/> / 8⌉).</summary>
    public int EncodedBlocksHigh => JpegCoefficients.DivideRoundingUp(Height, 8);

    /// <summary>All stored coefficients: block (bx, by) starts at <c>((by · BlocksWide) + bx) · 64</c>.</summary>
    public Span<short> Coefficients => _coefficients;

    /// <summary>The 64 coefficients of one block, natural order (index = vertical frequency · 8 + horizontal).</summary>
    public Span<short> Block(int blockX, int blockY)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockX);
        ArgumentOutOfRangeException.ThrowIfNegative(blockY);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(blockX, BlocksWide);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(blockY, BlocksHigh);
        return _coefficients.AsSpan(((blockY * BlocksWide) + blockX) * JpegTables.BlockSize, JpegTables.BlockSize);
    }

    internal short[] Data => _coefficients;

    private JpegCoefficients Owner => _owner ?? throw new InvalidOperationException("The component is not part of an image.");

    internal JpegComponent Clone() => new(this);

    internal void Attach(JpegCoefficients owner)
    {
        if (_owner is not null && !ReferenceEquals(_owner, owner))
        {
            throw new InvalidOperationException("The component already belongs to another image.");
        }

        _owner = owner;
    }
}
