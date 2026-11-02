using System.Text.RegularExpressions;

namespace SweetHide.Forensics.Detection;

/// <summary>Recognises CTF flags such as <c>flag{...}</c>, <c>CTF{...}</c> or <c>picoCTF{...}</c>.</summary>
public static partial class FlagPattern
{
    /// <summary>True when the text contains something shaped like <c>prefix{body}</c>, whatever the prefix.</summary>
    public static bool IsMatch(string? text) => !string.IsNullOrEmpty(text) && Shape().IsMatch(text);

    /// <summary>
    /// True when the text contains a flag with a well-known prefix (flag, CTF, picoCTF, HTB, THM, key, secret).
    /// Much stronger evidence than <see cref="IsMatch"/>.
    /// </summary>
    public static bool IsLikelyFlag(string? text) => !string.IsNullOrEmpty(text) && Known().IsMatch(text);

    /// <summary>Every flag-shaped substring, in order.</summary>
    public static IReadOnlyList<string> FindAll(string? text) =>
        string.IsNullOrEmpty(text) ? [] : [.. Shape().Matches(text).Select(m => m.Value)];

    // A short identifier prefix, then braces around a body with no whitespace or nested braces.
    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9_\-]{1,31}\{[^\s{}]{2,200}\}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Shape();

    [GeneratedRegex(@"[A-Za-z0-9_\-]{0,16}(?:flag|ctf|htb|thm|key|secret)[A-Za-z0-9_\-]{0,16}\{[^\s{}]{1,200}\}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Known();
}
