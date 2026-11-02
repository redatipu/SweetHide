using System.Reflection;
using System.Security.Cryptography;

namespace SweetHide.Presentation.Services;

/// <summary>
/// Diceware-style passphrases from built-in Thai and English word lists, picked with a CSPRNG. Enough words are
/// used for at least ~52 bits of entropy, which Argon2id makes very expensive to guess.
/// </summary>
public static class PassphraseGenerator
{
    public const double TargetBits = 52;
    private static readonly Lazy<string[]> English = new(() => Load("en"));
    private static readonly Lazy<string[]> Thai = new(() => Load("th"));

    public static IReadOnlyList<string> Words(bool thai) => thai ? Thai.Value : English.Value;

    /// <summary>Number of words needed for <see cref="TargetBits"/> with the given list.</summary>
    public static int WordCount(bool thai) => (int)Math.Ceiling(TargetBits / Math.Log2(Words(thai).Count));

    public static double EntropyBits(bool thai) => WordCount(thai) * Math.Log2(Words(thai).Count);

    public static string Generate(bool thai)
    {
        var words = Words(thai);
        var picked = new string[WordCount(thai)];
        for (var i = 0; i < picked.Length; i++)
        {
            picked[i] = words[RandomNumberGenerator.GetInt32(words.Count)];
        }

        return string.Join('-', picked);
    }

    private static string[] Load(string language)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"SweetHide.Words.{language}.txt")
            ?? throw new InvalidOperationException($"Word list '{language}' is missing.");
        using var reader = new StreamReader(stream);
        return [.. reader.ReadToEnd().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal)];
    }
}
