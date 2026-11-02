using System.Buffers;
using System.Text;

using SweetHide.Forensics.Detection;

namespace SweetHide.Forensics.Text;

public enum StringEncoding
{
    /// <summary>Printable 7-bit ASCII.</summary>
    Ascii,

    /// <summary>Valid UTF-8 with at least one non-ASCII character (Thai, accented Latin, ...).</summary>
    Utf8,

    /// <summary>UTF-16 little-endian (Windows "wide" strings), Latin, Greek, Cyrillic and Thai ranges.</summary>
    Utf16LE,
}

/// <param name="Offset">Byte offset of the first character.</param>
/// <param name="ByteLength">Length in bytes.</param>
/// <param name="Encoding">How the bytes were decoded.</param>
/// <param name="Value">The text (cut at <see cref="StringsOptions.MaxStringChars"/>).</param>
public sealed record ExtractedString(long Offset, int ByteLength, StringEncoding Encoding, string Value);

public sealed record StringsOptions
{
    /// <summary>Shortest run reported, in characters (GNU strings uses 4).</summary>
    public int MinLength { get; init; } = 4;

    public bool IncludeAscii { get; init; } = true;

    public bool IncludeUtf8 { get; init; } = true;

    public bool IncludeUtf16LE { get; init; } = true;

    public int MaxResults { get; init; } = 10_000;

    /// <summary>Longer strings are still found but their <see cref="ExtractedString.Value"/> is cut.</summary>
    public int MaxStringChars { get; init; } = 4096;

    public static StringsOptions Default { get; } = new();
}

/// <summary>Like GNU strings, plus UTF-8 (Thai included) and UTF-16LE, with offsets.</summary>
public static class StringsExtractor
{
    private const int CancellationCheckInterval = 1 << 16;

    public static IReadOnlyList<ExtractedString> Extract(ReadOnlyMemory<byte> data, StringsOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= StringsOptions.Default;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MinLength);
        var results = new List<ExtractedString>();
        if (options.IncludeAscii || options.IncludeUtf8)
        {
            ExtractUtf8(data.Span, options, results, cancellationToken);
        }

        if (options.IncludeUtf16LE)
        {
            ExtractUtf16(data.Span, 0, options, results, cancellationToken);
            ExtractUtf16(data.Span, 1, options, results, cancellationToken);
        }

        results.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return results.Count > options.MaxResults ? results.GetRange(0, options.MaxResults) : results;
    }

    private static void ExtractUtf8(ReadOnlySpan<byte> data, StringsOptions options, List<ExtractedString> results, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var nextCheck = CancellationCheckInterval;
        var i = 0;
        while (i < data.Length && results.Count < options.MaxResults)
        {
            if (i >= nextCheck)
            {
                cancellationToken.ThrowIfCancellationRequested();
                nextCheck = i + CancellationCheckInterval;
            }

            var start = i;
            var chars = 0;
            var nonAscii = false;
            builder.Clear();
            while (i < data.Length)
            {
                if (Rune.DecodeFromUtf8(data[i..], out var rune, out var consumed) != OperationStatus.Done || !IsStringRune(rune))
                {
                    break;
                }

                if (rune.Value > 0x7F)
                {
                    if (!options.IncludeUtf8)
                    {
                        break;
                    }

                    nonAscii = true;
                }

                if (builder.Length < options.MaxStringChars)
                {
                    builder.Append(rune.ToString());
                }

                i += consumed;
                chars++;
            }

            if (chars >= options.MinLength && (nonAscii ? options.IncludeUtf8 : options.IncludeAscii))
            {
                results.Add(new ExtractedString(start, i - start, nonAscii ? StringEncoding.Utf8 : StringEncoding.Ascii, builder.ToString()));
            }

            if (i == start)
            {
                i++;
            }
        }
    }

    private static void ExtractUtf16(ReadOnlySpan<byte> data, int alignment, StringsOptions options, List<ExtractedString> results, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var nextCheck = CancellationCheckInterval;
        var i = alignment;
        while (i + 1 < data.Length && results.Count < options.MaxResults)
        {
            if (i >= nextCheck)
            {
                cancellationToken.ThrowIfCancellationRequested();
                nextCheck = i + CancellationCheckInterval;
            }

            var start = i;
            builder.Clear();
            var chars = 0;
            while (i + 1 < data.Length)
            {
                var c = (char)(data[i] | (data[i + 1] << 8));
                if (!IsUtf16StringChar(c))
                {
                    break;
                }

                if (builder.Length < options.MaxStringChars)
                {
                    builder.Append(c);
                }

                i += 2;
                chars++;
            }

            if (chars >= options.MinLength)
            {
                results.Add(new ExtractedString(start, i - start, StringEncoding.Utf16LE, builder.ToString()));
            }

            if (i == start)
            {
                i += 2;
            }
        }
    }

    /// <summary>Printable characters and tab; line breaks end a string, as in GNU strings.</summary>
    private static bool IsStringRune(Rune rune) => rune.Value is not ('\n' or '\r') && TextHeuristics.IsText(rune);

    /// <summary>
    /// Deliberately narrow (Latin, Greek, Cyrillic, Thai, punctuation): misaligned ASCII decodes to CJK ideographs,
    /// which would otherwise flood the output.
    /// </summary>
    private static bool IsUtf16StringChar(char c) => c switch
    {
        '\t' => true,
        >= ' ' and <= '~' => true,
        >= ' ' and <= 'ɏ' => true,
        >= 'Ͱ' and <= 'ӿ' => true,
        >= 'ก' and <= '๛' => true,
        >= '‐' and <= '‧' => true,
        >= '‰' and <= '⁞' => true,
        _ => false,
    };
}
