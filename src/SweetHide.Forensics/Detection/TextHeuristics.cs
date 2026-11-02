using System.Globalization;
using System.Text;

namespace SweetHide.Forensics.Detection;

/// <summary>Byte-level tests for "does this look like human text?". Thai and other scripts count as text.</summary>
internal static class TextHeuristics
{
    private const char Placeholder = '·';

    /// <summary>Printable characters plus tab, CR and LF. Controls, format characters and unassigned code points are not text.</summary>
    public static bool IsText(Rune rune)
    {
        var value = rune.Value;
        if (value is '\t' or '\n' or '\r')
        {
            return true;
        }

        if (value < 0x20 || value is >= 0x7F and <= 0x9F || value == 0xFFFD)
        {
            return false;
        }

        if (value < 0x7F)
        {
            return true;
        }

        return Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned);
    }

    /// <summary>Thai block U+0E00–U+0E7F.</summary>
    public static bool IsThai(Rune rune) => rune.Value is >= 0x0E00 and <= 0x0E7F;

    /// <summary>Length in bytes (and characters) of the valid UTF-8 text at the start of <paramref name="data"/>.</summary>
    public static (int Bytes, int Chars) LeadingRun(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        var chars = 0;
        while (offset < data.Length)
        {
            if (Rune.DecodeFromUtf8(data[offset..], out var rune, out var consumed) != System.Buffers.OperationStatus.Done || !IsText(rune))
            {
                break;
            }

            offset += consumed;
            chars++;
        }

        return (offset, chars);
    }

    /// <summary>Share of bytes that belong to valid, printable UTF-8 characters.</summary>
    public static double TextRatio(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return 0;
        }

        var offset = 0;
        var textBytes = 0;
        while (offset < data.Length)
        {
            if (Rune.DecodeFromUtf8(data[offset..], out var rune, out var consumed) == System.Buffers.OperationStatus.Done && IsText(rune))
            {
                textBytes += consumed;
                offset += consumed;
            }
            else
            {
                offset += Math.Max(1, consumed);
            }
        }

        return (double)textBytes / data.Length;
    }

    /// <summary>Shannon entropy in bits per byte (0 for constant data, 8 for perfectly random data).</summary>
    public static double Entropy(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return 0;
        }

        Span<int> counts = stackalloc int[256];
        foreach (var b in data)
        {
            counts[b]++;
        }

        double entropy = 0;
        foreach (var count in counts)
        {
            if (count > 0)
            {
                var p = (double)count / data.Length;
                entropy -= p * Math.Log2(p);
            }
        }

        return entropy;
    }

    /// <summary>
    /// How much a decoded string looks like a real message rather than a repeating pixel pattern: 0 for "UUUU" or
    /// "I$I$", around 1 for prose, flags, Base64 or Thai.
    /// </summary>
    public static double Quality(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var counts = new Dictionary<int, int>();
        var total = 0;
        var wordLike = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            total++;
            counts[rune.Value] = counts.GetValueOrDefault(rune.Value) + 1;
            if (Rune.IsLetterOrDigit(rune) || rune.Value == ' ' || IsThai(rune)
                || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            {
                wordLike++;
            }
        }

        double entropy = 0;
        foreach (var count in counts.Values)
        {
            var p = (double)count / total;
            entropy -= p * Math.Log2(p);
        }

        // Normalise by the best entropy a string this short could have, so "flag{hi}" is not punished for being short.
        var maxEntropy = Math.Log2(Math.Clamp(total, 2, 64));
        var diversity = Math.Clamp(((entropy / maxEntropy) - 0.3) / 0.3, 0, 1);
        // Only punctuation-heavy strings ("$#!%&") are marked down; flags, Base64 and prose all clear 60 %.
        var words = 0.6 + (0.4 * Math.Clamp((((double)wordLike / total) - 0.3) / 0.3, 0, 1));
        return diversity * words;
    }

    /// <summary>Decodes for display: invalid UTF-8 becomes U+FFFD and control characters become a middle dot.</summary>
    public static string Preview(ReadOnlySpan<byte> data, int maxChars)
    {
        var builder = new StringBuilder(Math.Min(maxChars, data.Length));
        var offset = 0;
        while (offset < data.Length && builder.Length < maxChars)
        {
            var status = Rune.DecodeFromUtf8(data[offset..], out var rune, out var consumed);
            offset += Math.Max(1, consumed);
            if (status != System.Buffers.OperationStatus.Done)
            {
                builder.Append('�');
            }
            else if (rune.Value is '\n' or '\r' or '\t' || !IsText(rune))
            {
                builder.Append(rune.Value is '\n' or '\r' or '\t' ? ' ' : Placeholder);
            }
            else
            {
                builder.Append(rune.ToString());
            }
        }

        if (offset < data.Length)
        {
            builder.Append('…');
        }

        return builder.ToString();
    }

    /// <summary>"89 50 4E 47 …" for the first <paramref name="maxBytes"/> bytes.</summary>
    public static string HexPreview(ReadOnlySpan<byte> data, int maxBytes)
    {
        var shown = data[..Math.Min(maxBytes, data.Length)];
        var hex = Convert.ToHexString(shown);
        var builder = new StringBuilder(shown.Length * 3);
        for (var i = 0; i < shown.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(hex, i * 2, 2);
        }

        if (data.Length > shown.Length)
        {
            builder.Append(" …");
        }

        return builder.ToString();
    }
}
