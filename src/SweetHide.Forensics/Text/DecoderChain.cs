using System.Text;
using System.Text.Json.Serialization;
using System.Text.Unicode;

using SweetHide.Forensics.Detection;

namespace SweetHide.Forensics.Text;

/// <param name="Decoder">Decoder name, e.g. "Base64".</param>
/// <param name="Output">Preview of what it produced (text, or the detected file type).</param>
public sealed record DecodeStep(string Decoder, string Output);

/// <param name="Steps">The decoders applied, in order.</param>
/// <param name="Text">Final text, when the chain ends in text.</param>
/// <param name="FileType">Final file type, when the chain ends in a recognised file.</param>
/// <param name="Score">0–1. Flag-shaped text and files score highest, then readable language.</param>
public sealed record DecodeResult(IReadOnlyList<DecodeStep> Steps, string? Text, FileTypeMatch? FileType, double Score)
{
    /// <summary>The decoded bytes when the chain ends in a file (null for text results). Not written to JSON.</summary>
    [JsonIgnore]
    public byte[]? FileBytes { get; init; }

    /// <summary>"Base64 → Hex → ROT13".</summary>
    public string Chain => string.Join(" → ", Steps.Select(s => s.Decoder));
}

public sealed record DecoderOptions
{
    public int MaxDepth { get; init; } = 6;

    /// <summary>Total decodings tried before the search stops (guards against branching blow-up).</summary>
    public int MaxNodes { get; init; } = 256;

    public int MaxResults { get; init; } = 16;

    /// <summary>Inputs longer than this are not decoded at all.</summary>
    public int MaxInputChars { get; init; } = 4 * 1024 * 1024;

    public static DecoderOptions Default { get; } = new();
}

/// <summary>
/// CyberChef-lite "magic": tries Base64, Base64url, Base32, hex, binary, decimal bytes, URL encoding, Morse, ROT13 and
/// ROT47, and keeps going while the result stays plausible (readable text or a recognised file).
/// </summary>
public static class DecoderChain
{
    private const int PreviewChars = 200;

    /// <summary>Below this many letters, letter frequencies say too little to justify following a rotation.</summary>
    private const int MinLettersForLanguage = 8;

    // a–z frequencies in English text, in percent.
    private static readonly double[] EnglishFrequencies =
    [
        8.167, 1.492, 2.782, 4.253, 12.702, 2.228, 2.015, 6.094, 6.966, 0.153, 0.772, 4.025, 2.406,
        6.749, 7.507, 1.929, 0.095, 5.987, 6.327, 9.056, 2.758, 0.978, 2.360, 0.150, 1.974, 0.074,
    ];

    /// <summary>Names of the decoders, in the order they are tried.</summary>
    public static IReadOnlyList<string> DecoderNames { get; } = [.. Decoders.All.Select(d => d.Name)];

    /// <summary>Plausible decodings, best first. Empty when nothing decodes to readable text or a file.</summary>
    public static IReadOnlyList<DecodeResult> Decode(string input, DecoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        options ??= DecoderOptions.Default;
        if (input.Trim().Length < 4 || input.Length > options.MaxInputChars)
        {
            return [];
        }

        var results = new List<DecodeResult>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { input };
        var queue = new Queue<(string Text, List<DecodeStep> Steps)>();
        queue.Enqueue((input, []));
        var nodes = 0;
        while (queue.Count > 0 && nodes < options.MaxNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (text, steps) = queue.Dequeue();
            foreach (var (name, decode, isCipher) in Decoders.All)
            {
                if (++nodes > options.MaxNodes)
                {
                    break;
                }

                var bytes = decode(text);
                if (bytes is null || bytes.Length == 0)
                {
                    continue;
                }

                if (FileSignatures.Identify(bytes) is { } file)
                {
                    var fileSteps = new List<DecodeStep>(steps) { new(name, $"{file.Name}, {bytes.Length} bytes") };
                    results.Add(new DecodeResult(fileSteps, null, file, 0.9) { FileBytes = bytes });
                    continue;
                }

                if (!IsPlausibleText(bytes, out var decoded) || !visited.Add(decoded))
                {
                    continue;
                }

                // A rotation always "succeeds"; only follow it when it reveals a flag or makes longer text more readable.
                if (isCipher && !(FlagPattern.IsLikelyFlag(decoded) && !FlagPattern.IsLikelyFlag(text))
                    && (LetterCount(text) < MinLettersForLanguage || Language(decoded) < Language(text) + 0.15))
                {
                    continue;
                }

                var nextSteps = new List<DecodeStep>(steps) { new(name, Preview(decoded)) };
                results.Add(new DecodeResult(nextSteps, decoded, null, Score(decoded, nextSteps.Count)));
                if (nextSteps.Count < options.MaxDepth)
                {
                    queue.Enqueue((decoded, nextSteps));
                }
            }
        }

        return [.. results.OrderByDescending(r => r.Score).ThenByDescending(r => r.Steps.Count).Take(options.MaxResults)];
    }

    /// <summary>
    /// How much a text reads like natural language: letter frequencies compared with English, plus a bonus for Thai.
    /// 0 for symbol soup, around 0.9 for English prose.
    /// </summary>
    internal static double Language(string text)
    {
        Span<double> counts = stackalloc double[26];
        var letters = 0;
        var thai = 0;
        var visible = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                continue;
            }

            visible++;
            if (rune.Value < 128 && char.IsAsciiLetter((char)rune.Value))
            {
                counts[char.ToLowerInvariant((char)rune.Value) - 'a']++;
                letters++;
            }
            else if (TextHeuristics.IsThai(rune))
            {
                thai++;
            }
        }

        if (visible == 0)
        {
            return 0;
        }

        if (thai * 2 >= visible)
        {
            return 0.9;
        }

        if (letters < 3)
        {
            return 0;
        }

        double dot = 0, a = 0, b = 0;
        for (var i = 0; i < 26; i++)
        {
            dot += counts[i] * EnglishFrequencies[i];
            a += counts[i] * counts[i];
            b += EnglishFrequencies[i] * EnglishFrequencies[i];
        }

        var cosine = dot / Math.Sqrt(a * b);
        var letterShare = (double)letters / visible;
        return cosine * Math.Clamp(letterShare / 0.7, 0, 1);
    }

    private static int LetterCount(string text) => text.Count(char.IsAsciiLetter);

    private static bool IsPlausibleText(byte[] bytes, out string text)
    {
        text = string.Empty;
        if (!Utf8.IsValid(bytes) || TextHeuristics.TextRatio(bytes) < 0.95)
        {
            return false;
        }

        text = Encoding.UTF8.GetString(bytes);
        return text.Trim().Length >= 2 && TextHeuristics.Quality(text) >= 0.4;
    }

    private static double Score(string text, int depth)
    {
        if (FlagPattern.IsLikelyFlag(text))
        {
            return 1.0;
        }

        var score = 0.25 + (0.45 * Language(text) * TextHeuristics.Quality(text)) + Math.Min(0.08, 0.02 * depth);
        return FlagPattern.IsMatch(text) ? Math.Max(score, 0.8) : Math.Min(score, 0.79);
    }

    private static string Preview(string text) => text.Length <= PreviewChars ? text : string.Concat(text.AsSpan(0, PreviewChars), "…");
}
