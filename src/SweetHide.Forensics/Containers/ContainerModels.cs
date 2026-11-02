using SweetHide.Forensics.Detection;

namespace SweetHide.Forensics.Containers;

[Flags]
public enum StructureTraits
{
    None = 0,

    /// <summary>PNG critical chunk (decoders must understand it).</summary>
    Critical = 1,

    /// <summary>PNG private chunk (second letter lower case), or a vendor-specific segment.</summary>
    Private = 2,

    /// <summary>Not a standard chunk, segment or block type.</summary>
    Unknown = 4,

    /// <summary>The stored CRC does not match the chunk (edited by hand, or deliberately broken).</summary>
    CrcMismatch = 8,

    /// <summary>The declared length runs past the end of the file.</summary>
    Truncated = 16,

    /// <summary>Unusual in a way that often means hidden data (gaps, oversized fields, odd placement).</summary>
    Suspicious = 32,

    /// <summary>Carries text or metadata (tEXt, COM, APP1, comment blocks, EXIF/XMP chunks).</summary>
    Metadata = 64,
}

/// <summary>One chunk, segment or block of a container file.</summary>
/// <param name="Type">Chunk type or marker name, e.g. "IHDR", "APP1", "Comment".</param>
/// <param name="Offset">Start of the item, header included.</param>
/// <param name="Length">Total length, header and CRC included.</param>
/// <param name="DataOffset">Start of the item's payload.</param>
/// <param name="DataLength">Length of the payload.</param>
/// <param name="Description">Short human-readable summary.</param>
/// <param name="Traits">Anything notable about the item.</param>
public sealed record StructureItem(string Type, long Offset, long Length, long DataOffset, long DataLength, string Description, StructureTraits Traits);

/// <summary>Bytes after the logical end of the container (PNG IEND, JPEG EOI, GIF trailer, RIFF size, BMP pixels).</summary>
/// <param name="Offset">First byte after the container.</param>
/// <param name="Length">Number of trailing bytes.</param>
/// <param name="Content">What the trailing bytes look like.</param>
public sealed record TrailingData(long Offset, long Length, ContentAnalysis Content);

/// <summary>A file signature found inside another file (binwalk-style).</summary>
/// <param name="Offset">Where the signature starts.</param>
/// <param name="Length">Estimated length from the embedded file's own headers; null when it can only be carved to the end.</param>
/// <param name="Type">The recognised type.</param>
/// <param name="Location">Which structure item (or "trailing data") contains it; "start of file" at offset 0.</param>
public sealed record EmbeddedFile(long Offset, long? Length, FileTypeMatch Type, string? Location)
{
    /// <summary>The match at offset 0 is the file itself, not something hidden in it.</summary>
    public bool IsWholeFile => Offset == 0;
}

/// <summary>PNG dimensions that make a mismatching IHDR CRC valid: the classic "height was edited" CTF trick.</summary>
public sealed record CorrectedDimensions(int Width, int Height, int StoredWidth, int StoredHeight);

/// <param name="Format">Detected format of the whole file, or null.</param>
/// <param name="FileLength">Size of the inspected data.</param>
/// <param name="Structure">Chunks, segments or blocks in file order.</param>
/// <param name="Trailing">Data after the container's logical end, if any.</param>
/// <param name="Embedded">Known file signatures found anywhere in the file (including offset 0).</param>
/// <param name="Warnings">Structural problems: bad order, gaps, truncation, unparseable data.</param>
/// <param name="CorrectedDimensions">PNG only: the dimensions the IHDR CRC actually belongs to.</param>
public sealed record ContainerReport(
    FileTypeMatch? Format,
    long FileLength,
    IReadOnlyList<StructureItem> Structure,
    TrailingData? Trailing,
    IReadOnlyList<EmbeddedFile> Embedded,
    IReadOnlyList<string> Warnings,
    CorrectedDimensions? CorrectedDimensions);

public sealed record ContainerInspectorOptions
{
    /// <summary>Structure items listed before the walk stops recording (it still finds the end of the file).</summary>
    public int MaxStructureItems { get; init; } = 10_000;

    public bool ScanSignatures { get; init; } = true;

    public int MaxEmbeddedFiles { get; init; } = 256;

    /// <summary>When the PNG IHDR CRC mismatches, try every width and height up to this value to find the real one.</summary>
    public int MaxRepairDimension { get; init; } = 16_384;

    public static ContainerInspectorOptions Default { get; } = new();
}
