using System.Text;

namespace SweetHide.Core;

/// <summary>
/// Turns an untrusted file name (from a revealed payload) into a single, harmless file name that is valid on
/// Windows, Linux and macOS. Never returns anything containing a directory separator or "..".
/// </summary>
public static class SafeFileName
{
    private const int MaxLength = 200;
    private const string Fallback = "file";

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
        "CONIN$", "CONOUT$",
    };

    public static string Sanitize(string? name)
    {
        var component = LastComponent(name ?? string.Empty);

        var builder = new StringBuilder(component.Length);
        foreach (var c in component)
        {
            builder.Append(IsForbidden(c) ? '_' : c);
        }

        // Windows silently drops trailing dots/spaces, which can make two names collide or hide an extension.
        var result = builder.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length == 0 || result.All(c => c == '.'))
        {
            return Fallback;
        }

        var stem = result.Split('.')[0].TrimEnd(' ');
        if (ReservedWindowsNames.Contains(stem))
        {
            result = "_" + result;
        }

        return Truncate(result);
    }

    /// <summary>Text after the last '/' or '\', whichever platform the name came from.</summary>
    internal static string LastComponent(string name)
    {
        var index = name.LastIndexOfAny(['/', '\\']);
        return index >= 0 ? name[(index + 1)..] : name;
    }

    private static bool IsForbidden(char c) =>
        char.IsControl(c) || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*';

    private static string Truncate(string name)
    {
        if (name.Length <= MaxLength)
        {
            return name;
        }

        var dot = name.LastIndexOf('.');
        var extension = dot > 0 && name.Length - dot <= 16 ? name[dot..] : string.Empty;
        var stem = name[..(MaxLength - extension.Length)];
        if (char.IsHighSurrogate(stem[^1]))
        {
            stem = stem[..^1];
        }

        return stem + extension;
    }
}
