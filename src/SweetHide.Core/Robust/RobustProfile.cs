namespace SweetHide.Core.Robust;

/// <summary>
/// Tuning for one target app: how far to shrink the cover so the app does not resize it again, how strong the
/// embedding is, and how much redundancy to spend. Embedding and extraction must use identical values, so the
/// built-in profiles are frozen; changing any number makes older images unreadable.
/// </summary>
/// <remarks>See docs/ROBUST.md for where the numbers come from.</remarks>
public sealed record RobustProfile
{
    /// <summary>
    /// LINE, photo sent normally (not as a file, not "Original"). Third-party measurements suggest LINE caps the pixel
    /// count at about 1280×1280 for "Standard" and 2048×2048 for "High" and always re-encodes as JPEG; the quality is
    /// not published. The cover is shrunk well under the Standard limit and the embedding is tuned to survive JPEG
    /// quality 50 and up (4:2:0 or 4:4:4), with extra repetition because LINE's encoder settings are unknown.
    /// </summary>
    public static RobustProfile Line { get; } = new()
    {
        Name = "line",
        MaxLongSide = 1440,
        MaxPixels = 1_440_000,
        Step = 34,
        CoefficientsPerBlock = 1,
        HeaderRepetition = 9,
        MinRepetition = 5,
        ParityPercent = 60,
        OutputJpegQuality = 92,
        MinimumQuality = 50,
    };

    /// <summary>
    /// Telegram, "send as photo" without HD. The official clients shrink photos to 1280 px on the long side and
    /// re-encode them (Android quality 80, desktop 87, iOS 0.6 on Apple's scale), unless the desktop app can pass a
    /// small JPEG through untouched. Tuned to survive JPEG quality 50 and up (4:2:0 or 4:4:4).
    /// </summary>
    public static RobustProfile Telegram { get; } = new()
    {
        Name = "telegram",
        MaxLongSide = 1280,
        MaxPixels = 1280L * 1280,
        Step = 34,
        CoefficientsPerBlock = 1,
        HeaderRepetition = 9,
        MinRepetition = 4,
        ParityPercent = 60,
        OutputJpegQuality = 90,
        MinimumQuality = 50,
    };

    /// <summary>The built-in profiles, in the order extraction tries them.</summary>
    public static IReadOnlyList<RobustProfile> All { get; } = [Line, Telegram];

    /// <summary>Short identifier, e.g. for a CLI option.</summary>
    public required string Name { get; init; }

    /// <summary>The cover is shrunk so that neither side exceeds this.</summary>
    public required int MaxLongSide { get; init; }

    /// <summary>The cover is shrunk so that width × height does not exceed this.</summary>
    public required long MaxPixels { get; init; }

    /// <summary>
    /// Quantization index modulation step in DCT units (orthonormal 8x8 DCT of 0–255 luminance). Larger survives
    /// coarser recompression but is more visible; an app's JPEG survives cleanly while its quantizer step on the
    /// chosen coefficients stays under about half of this.
    /// </summary>
    public required float Step { get; init; }

    /// <summary>Mid-frequency luminance coefficients used in each 8x8 block (1–6).</summary>
    public required int CoefficientsPerBlock { get; init; }

    /// <summary>How many times each sync and header bit is repeated.</summary>
    public required int HeaderRepetition { get; init; }

    /// <summary>Fewest repetitions of each payload bit; this sets the capacity.</summary>
    public required int MinRepetition { get; init; }

    /// <summary>Reed–Solomon parity symbols as a percentage of data symbols in each codeword.</summary>
    public required int ParityPercent { get; init; }

    /// <summary>Quality for <see cref="RobustStego.WriteJpeg"/>.</summary>
    public required int OutputJpegQuality { get; init; }

    /// <summary>Lowest JPEG quality (libjpeg scale, 4:2:0) the profile is tested to survive. Informational.</summary>
    public int MinimumQuality { get; init; }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        if (MaxLongSide < 64 || MaxPixels < 64 * 64 || Step is not (> 0 and < 512) || CoefficientsPerBlock is < 1 or > 6
            || HeaderRepetition < 1 || MinRepetition < 1 || ParityPercent is < 10 or > 300 || OutputJpegQuality is < 1 or > 100)
        {
            throw new ArgumentException($"Robust profile '{Name}' has out-of-range settings.");
        }
    }
}
