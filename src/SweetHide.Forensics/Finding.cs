using System.Text.Json.Serialization;

namespace SweetHide.Forensics;

/// <summary>Which check produced a finding.</summary>
public enum FindingKind
{
    /// <summary>Text or a file read out of pixel bit planes.</summary>
    LsbPayload,

    /// <summary>Bytes after the end of the image (after PNG IEND, JPEG EOI, ...).</summary>
    TrailingData,

    /// <summary>Another file's signature inside this one.</summary>
    EmbeddedFile,

    /// <summary>Broken CRCs, unknown chunks, gaps, edited dimensions, truncation.</summary>
    StructureAnomaly,

    /// <summary>A metadata field (EXIF, XMP, PNG text, comments, ...).</summary>
    Metadata,

    /// <summary>A flag-shaped string anywhere in the file.</summary>
    InterestingString,

    /// <summary>Text that decodes (Base64, hex, ROT13, ...) into something readable.</summary>
    EncodedText,

    /// <summary>A QR code or barcode, in the image or in a bit plane.</summary>
    Barcode,

    /// <summary>Statistical evidence of LSB embedding (chi-square, RS, SPA, randomness map).</summary>
    Steganalysis,

    /// <summary>Text or a file read out of audio sample bits.</summary>
    AudioLsb,

    /// <summary>A rendered spectrogram to look at (text drawn into audio).</summary>
    Spectrogram,
}

/// <summary>One thing worth a look, ready to show in a list or print as JSON.</summary>
/// <param name="Kind">Which check produced it.</param>
/// <param name="Title">One line, e.g. "Text in LSBs (bit0,rgb,msb,xy)".</param>
/// <param name="Detail">What was found: a preview, numbers, the reason it is suspicious.</param>
/// <param name="Confidence">0–1: how likely this is deliberately hidden content. Findings are sorted by it.</param>
public sealed record Finding(FindingKind Kind, string Title, string Detail, double Confidence)
{
    /// <summary>Where it was found: an LSB configuration, a file offset, a metadata field, a bit plane, ...</summary>
    public string? Source { get; init; }

    /// <summary>File offset, when the finding is a byte range of the input.</summary>
    public long? Offset { get; init; }

    /// <summary>
    /// Bytes the user can save (extracted payload, carved file, decoded text, rendered PNG). Not written to JSON;
    /// <see cref="PayloadLength"/> and <see cref="SuggestedFileName"/> are.
    /// </summary>
    [JsonIgnore]
    public byte[]? Payload { get; init; }

    public int? PayloadLength => Payload?.Length;

    /// <summary>A safe-looking name for <see cref="Payload"/> (still pass it through SafeFileName before writing).</summary>
    public string? SuggestedFileName { get; init; }
}

/// <param name="FileName">The name passed in, if any.</param>
/// <param name="FileSize">Bytes analysed.</param>
/// <param name="Format">Detected format name, e.g. "PNG image", or "unknown".</param>
/// <param name="Width">Image width when the file decoded as an image.</param>
/// <param name="Height">Image height when the file decoded as an image.</param>
/// <param name="Findings">Best first.</param>
/// <param name="Errors">Checks that could not run, and why. The other checks still ran.</param>
/// <param name="ElapsedMilliseconds">Total analysis time.</param>
public sealed record StegoFinderReport(
    string? FileName,
    long FileSize,
    string Format,
    int? Width,
    int? Height,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<string> Errors,
    long ElapsedMilliseconds);
