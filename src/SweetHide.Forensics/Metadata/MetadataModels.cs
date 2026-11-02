namespace SweetHide.Forensics.Metadata;

/// <summary>One text-bearing metadata value.</summary>
/// <param name="Source">Where it lives, e.g. "PNG tEXt", "JPEG COM", "EXIF IFD0", "EXIF GPS", "XMP", "GIF comment".</param>
/// <param name="Name">Keyword, tag or property name, e.g. "Comment", "ImageDescription", "dc:description".</param>
/// <param name="Value">The decoded text (cut at <see cref="MetadataOptions.MaxValueChars"/>).</param>
/// <param name="Offset">File offset of the raw value (best effort).</param>
/// <param name="Length">Raw length in bytes.</param>
/// <param name="IsUnusual">Unusually long, binary where text belongs, data hidden after a NUL, or a capped decompression.</param>
/// <param name="Note">Why it is unusual, or other remarks.</param>
public sealed record MetadataField(string Source, string Name, string Value, long Offset, int Length, bool IsUnusual, string? Note);

public sealed record MetadataOptions
{
    /// <summary>Text fields longer than this many characters are flagged (descriptions are normally short).</summary>
    public int LongFieldThreshold { get; init; } = 200;

    /// <summary>XMP packets are verbose by nature; flag only packets longer than this many characters.</summary>
    public int LongXmpThreshold { get; init; } = 16 * 1024;

    /// <summary>Longer values are cut for display; <see cref="MetadataField.Length"/> keeps the real size.</summary>
    public int MaxValueChars { get; init; } = 64 * 1024;

    /// <summary>Cap for zTXt/iTXt decompression (protects against zlib bombs).</summary>
    public int MaxDecompressedBytes { get; init; } = 1024 * 1024;

    public int MaxFields { get; init; } = 1000;

    public static MetadataOptions Default { get; } = new();
}
