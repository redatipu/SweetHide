using System.Buffers.Binary;
using System.Text;

namespace SweetHide.Forensics.Detection;

/// <summary>What a stream of extracted bits most likely holds.</summary>
public enum ContentKind
{
    /// <summary>Nothing to look at.</summary>
    Empty = 0,

    /// <summary>Every byte is the same (e.g. a flat area of the image).</summary>
    Uniform,

    /// <summary>Structured but unrecognised bytes.</summary>
    Binary,

    /// <summary>Close to random: normal for the lowest bit plane, or encrypted/compressed data.</summary>
    HighEntropy,

    /// <summary>Readable text from the first byte.</summary>
    Text,

    /// <summary>Readable text that ends in a NUL or a known delimiter.</summary>
    TerminatedText,

    /// <summary>A length field followed by that many readable bytes.</summary>
    LengthPrefixedText,

    /// <summary>A length field followed by a recognised file.</summary>
    LengthPrefixedFile,

    /// <summary>A recognised file starts at the first byte.</summary>
    File,
}

/// <summary>Scoring result for a byte stream.</summary>
/// <param name="Kind">Best explanation of the content.</param>
/// <param name="Score">0–1: how likely this is something deliberately hidden. Above ~0.5 is worth a look.</param>
/// <param name="DetectedType">Name of a recognised file type, if any.</param>
/// <param name="Preview">Readable text, or a hex dump for binary content.</param>
/// <param name="PayloadOffset">Where the payload starts within the stream (after any length field).</param>
/// <param name="PayloadLength">Declared or detected payload length; null when it runs to the end (or is unknown).</param>
/// <param name="Framing">How the payload is delimited, e.g. "u32 big-endian length" or "NUL terminator".</param>
/// <param name="TextRatio">Share of bytes in the analysed sample that are printable UTF-8.</param>
/// <param name="Entropy">Shannon entropy of the sample in bits per byte.</param>
public sealed record ContentAnalysis(
    ContentKind Kind,
    double Score,
    string? DetectedType,
    string Preview,
    int PayloadOffset,
    long? PayloadLength,
    string? Framing,
    double TextRatio,
    double Entropy)
{
    /// <summary>The recognised file type, when <see cref="Kind"/> is a file kind.</summary>
    public FileType FileType { get; init; }
}

public sealed record ContentAnalysisOptions
{
    /// <summary>Delimiters used by simple LSB tools in addition to NUL and the 0xFF 0xFE ("1111111111111110") marker.</summary>
    public IReadOnlyList<string> Terminators { get; init; } = ["$t3g0", "#####", "*^*^*"];

    /// <summary>Maximum characters of text returned in <see cref="ContentAnalysis.Preview"/>.</summary>
    public int PreviewChars { get; init; } = 160;

    public static ContentAnalysisOptions Default { get; } = new();
}

/// <summary>
/// Scores an extracted byte stream: file magic at the start, a 32-bit length prefix, a "123:" decimal length prefix
/// (Stegano), readable UTF-8 text (Thai included) ending in NUL or a delimiter, or none of the above.
/// </summary>
public static class ContentAnalyzer
{
    private const double FileScore = 0.95;
    private const double PrefixedFileScore = 0.97;
    private const double MaxTextScore = 0.9;
    private const double MinTextScore = 0.1;
    private const int MaxDecimalDigits = 9;

    private static readonly byte[] FfFeMarker = [0xFF, 0xFE];

    /// <param name="sample">The first bytes of the stream (a few KiB is plenty).</param>
    /// <param name="streamCapacity">Total bytes the full stream can hold; bounds plausible length fields.</param>
    /// <param name="options">Delimiters and preview size.</param>
    public static ContentAnalysis Analyze(ReadOnlySpan<byte> sample, long streamCapacity, ContentAnalysisOptions? options = null)
    {
        options ??= ContentAnalysisOptions.Default;
        if (sample.IsEmpty)
        {
            return new ContentAnalysis(ContentKind.Empty, 0, null, string.Empty, 0, null, null, 0, 0);
        }

        var entropy = TextHeuristics.Entropy(sample);
        var textRatio = TextHeuristics.TextRatio(sample);
        var best = Baseline(sample, entropy, textRatio);

        if (FileSignatures.Identify(sample) is { } file)
        {
            best = Better(best, new ContentAnalysis(ContentKind.File, FileScore, file.Name, file.Name, 0, null, null, textRatio, entropy) { FileType = file.Type });
        }

        if (sample.Length > 4)
        {
            best = Better(best, LengthPrefixed(sample, streamCapacity, bigEndian: true, options, textRatio, entropy));
            best = Better(best, LengthPrefixed(sample, streamCapacity, bigEndian: false, options, textRatio, entropy));
        }

        best = Better(best, DecimalPrefixed(sample, streamCapacity, options, textRatio, entropy));
        best = Better(best, LeadingText(sample, options, textRatio, entropy));
        return best;
    }

    /// <summary>Length factor for text found with no framing: 4 bytes is noise, 20+ is convincing.</summary>
    internal static double PlainLengthFactor(long length) => Math.Clamp((length - 4) / 16.0, 0, 1);

    /// <summary>Length factor when a matching length field already vouches for the text.</summary>
    internal static double FramedLengthFactor(long length) => Math.Clamp((length - 2) / 14.0, 0, 1);

    /// <summary>A valid length field is good evidence, but repetitive "text" behind it is still pixel noise.</summary>
    private static double FramedTextScore(string text, int length) =>
        Math.Min(0.93, TextHeuristics.Quality(text) * (0.4 + (0.55 * FramedLengthFactor(length))));

    private static ContentAnalysis Better(ContentAnalysis current, ContentAnalysis? candidate) =>
        candidate is not null && candidate.Score > current.Score ? candidate : current;

    private static ContentAnalysis Baseline(ReadOnlySpan<byte> sample, double entropy, double textRatio)
    {
        var hex = TextHeuristics.HexPreview(sample, 16);
        if (sample.IndexOfAnyExcept(sample[0]) < 0)
        {
            return new ContentAnalysis(ContentKind.Uniform, 0, null, hex, 0, null, null, textRatio, entropy);
        }

        // Natural LSB planes are close to random, so high entropy alone is weak evidence.
        var highEntropy = entropy >= Math.Min(7.5, Math.Log2(sample.Length) - 0.5);
        return new ContentAnalysis(highEntropy ? ContentKind.HighEntropy : ContentKind.Binary, highEntropy ? 0.05 : 0, null, hex, 0, null, null, textRatio, entropy);
    }

    private static ContentAnalysis? LengthPrefixed(ReadOnlySpan<byte> sample, long capacity, bool bigEndian, ContentAnalysisOptions options, double textRatio, double entropy)
    {
        var length = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(sample) : BinaryPrimitives.ReadUInt32LittleEndian(sample);
        if (length == 0 || length > capacity - 4)
        {
            return null;
        }

        var framing = bigEndian ? "u32 big-endian length" : "u32 little-endian length";
        var body = sample.Slice(4, (int)Math.Min(length, sample.Length - 4));
        if (FileSignatures.Identify(body) is { } file)
        {
            return new ContentAnalysis(ContentKind.LengthPrefixedFile, PrefixedFileScore, file.Name, file.Name, 4, length, framing, textRatio, entropy) { FileType = file.Type };
        }

        var (runBytes, _) = TextHeuristics.LeadingRun(body);
        if (runBytes < body.Length)
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(body);
        var score = FramedTextScore(text, body.Length);
        return new ContentAnalysis(ContentKind.LengthPrefixedText, score, null, TextHeuristics.Preview(body, options.PreviewChars), 4, length, framing, textRatio, entropy);
    }

    private static ContentAnalysis? DecimalPrefixed(ReadOnlySpan<byte> sample, long capacity, ContentAnalysisOptions options, double textRatio, double entropy)
    {
        var digits = 0;
        while (digits < sample.Length && digits < MaxDecimalDigits && sample[digits] is >= (byte)'0' and <= (byte)'9')
        {
            digits++;
        }

        if (digits == 0 || digits >= sample.Length || sample[digits] != ':' || sample[0] == '0')
        {
            return null;
        }

        var length = long.Parse(Encoding.ASCII.GetString(sample[..digits]), System.Globalization.CultureInfo.InvariantCulture);
        var start = digits + 1;
        if (length > capacity - start)
        {
            return null;
        }

        var body = sample.Slice(start, (int)Math.Min(length, sample.Length - start));
        var (runBytes, runChars) = TextHeuristics.LeadingRun(sample[start..]);
        if (runBytes < body.Length && runChars < length)
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(body);
        var score = FramedTextScore(text, body.Length);
        return new ContentAnalysis(ContentKind.LengthPrefixedText, score, null, TextHeuristics.Preview(body, options.PreviewChars), start, length, "decimal length + ':' (Stegano)", textRatio, entropy);
    }

    private static ContentAnalysis? LeadingText(ReadOnlySpan<byte> sample, ContentAnalysisOptions options, double textRatio, double entropy)
    {
        var (runBytes, _) = TextHeuristics.LeadingRun(sample);
        var (terminatorAt, terminatorName) = FindTerminator(sample, runBytes, options);
        var payloadLength = terminatorAt >= 0 ? terminatorAt : runBytes;

        if (payloadLength < 4)
        {
            // Text that does not start at byte 0 (for example after a short binary header) still deserves a mention.
            return textRatio >= 0.85 && sample.Length >= 64
                ? new ContentAnalysis(ContentKind.Text, 0.6 * TextHeuristics.Quality(Encoding.UTF8.GetString(sample)), null, TextHeuristics.Preview(sample, options.PreviewChars), 0, null, null, textRatio, entropy)
                : null;
        }

        var body = sample[..payloadLength];
        var score = PlainLengthFactor(payloadLength) * TextHeuristics.Quality(Encoding.UTF8.GetString(body));
        if (terminatorAt >= 0)
        {
            score += (1 - score) * 0.3 * Math.Clamp((payloadLength - 3) / 5.0, 0, 1);
        }

        if (score < MinTextScore)
        {
            return null; // a few printable bytes happen by chance all the time
        }

        var kind = terminatorAt >= 0 ? ContentKind.TerminatedText : ContentKind.Text;
        long? length = terminatorAt >= 0 || runBytes < sample.Length ? payloadLength : null;
        return new ContentAnalysis(kind, Math.Min(score, MaxTextScore), null, TextHeuristics.Preview(body, options.PreviewChars), 0, length, terminatorName, textRatio, entropy);
    }

    /// <summary>Earliest delimiter that ends the leading text (text delimiters can sit inside the run).</summary>
    private static (int Offset, string? Name) FindTerminator(ReadOnlySpan<byte> sample, int runBytes, ContentAnalysisOptions options)
    {
        var best = -1;
        string? name = null;
        if (runBytes < sample.Length && sample[runBytes] == 0)
        {
            (best, name) = (runBytes, "NUL terminator");
        }
        else if (runBytes + 1 < sample.Length && sample[runBytes..].StartsWith(FfFeMarker))
        {
            (best, name) = (runBytes, "0xFFFE marker");
        }

        foreach (var terminator in options.Terminators)
        {
            if (string.IsNullOrEmpty(terminator))
            {
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(terminator);
            var window = sample[..Math.Min(sample.Length, runBytes + bytes.Length)];
            var index = window.IndexOf(bytes);
            if (index > 0 && (best < 0 || index < best))
            {
                (best, name) = (index, $"\"{terminator}\" delimiter");
            }
        }

        return (best, name);
    }
}
