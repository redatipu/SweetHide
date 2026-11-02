using System.Globalization;
using System.Net;
using System.Text;

namespace SweetHide.Forensics.Text;

/// <summary>
/// The individual decoders. Each one is strict about its alphabet and returns null when the input is not in its
/// format, so the chain only follows decodings that could be right.
/// </summary>
internal static class Decoders
{
    private const int MinEncodedLength = 4;

    // Each entry is the character followed by its code.
    private static readonly Dictionary<string, char> Morse = new(
        """
        A.- B-... C-.-. D-.. E. F..-. G--. H.... I.. J.--- K-.- L.-.. M-- N-. O--- P.--. Q--.- R.-. S... T- U..- V...- W.--
        X-..- Y-.-- Z--.. 0----- 1.---- 2..--- 3...-- 4....- 5..... 6-.... 7--... 8---.. 9----. ..-.-.- ,--..-- ?..--..
        '.----. !-.-.-- /-..-. (-.--. )-.--.- &.-... :---... ;-.-.-. =-...- +.-.-. --....- _..--.- ".-..-. $...-..- @.--.-.
        """.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(e => KeyValuePair.Create(e[1..], e[0])),
        StringComparer.Ordinal);

    /// <summary>Name and decode function of every decoder, in the order they are tried.</summary>
    public static IReadOnlyList<(string Name, Func<string, byte[]?> Decode, bool IsCipher)> All { get; } =
    [
        ("Base64", Base64, false),
        ("Base64url", Base64Url, false),
        ("Base32", Base32, false),
        ("Hex", Hex, false),
        ("Binary", Binary, false),
        ("Decimal bytes", DecimalBytes, false),
        ("URL decode", UrlDecode, false),
        ("Morse", MorseCode, false),
        ("ROT13", Rot13, true),
        ("ROT47", Rot47, true),
    ];

    public static byte[]? Base64(string input)
    {
        var compact = StripWhitespace(input);
        if (compact.Length < 8 || compact.Length % 4 == 1 || !IsBase64(compact, '+', '/'))
        {
            return null;
        }

        var padded = Pad(compact);
        var buffer = new byte[padded.Length / 4 * 3];
        return Convert.TryFromBase64String(padded, buffer, out var written) ? buffer[..written] : null;
    }

    public static byte[]? Base64Url(string input)
    {
        var compact = StripWhitespace(input);
        if (compact.Length < 8 || compact.Length % 4 == 1 || !compact.Any(c => c is '-' or '_') || !IsBase64(compact, '-', '_'))
        {
            return null; // without - or _ it is plain Base64, already tried
        }

        return Base64(compact.Replace('-', '+').Replace('_', '/'));
    }

    public static byte[]? Base32(string input)
    {
        var compact = StripWhitespace(input).TrimEnd('=').ToUpperInvariant();
        if (compact.Length < 8 || (compact.Length % 8) is 1 or 3 or 6)
        {
            return null;
        }

        var output = new byte[compact.Length * 5 / 8];
        var buffer = 0;
        var bits = 0;
        var index = 0;
        foreach (var c in compact)
        {
            int value = c switch
            {
                >= 'A' and <= 'Z' => c - 'A',
                >= '2' and <= '7' => c - '2' + 26,
                _ => -1,
            };
            if (value < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output[index++] = (byte)(buffer >> bits);
                buffer &= (1 << bits) - 1;
            }
        }

        return output;
    }

    public static byte[]? Hex(string input)
    {
        var compact = new StringBuilder(input.Length);
        var span = input.AsSpan().Trim();
        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];
            if ((c == '0' && i + 1 < span.Length && span[i + 1] is 'x' or 'X') || (c == '\\' && i + 1 < span.Length && span[i + 1] == 'x'))
            {
                i++; // "0x" or "\x" prefix
            }
            else if (char.IsAsciiHexDigit(c))
            {
                compact.Append(c);
            }
            else if (c is not (' ' or ':' or ',' or '-' or '\n' or '\r' or '\t'))
            {
                return null;
            }
        }

        if (compact.Length < MinEncodedLength || compact.Length % 2 != 0)
        {
            return null;
        }

        return Convert.FromHexString(compact.ToString());
    }

    public static byte[]? Binary(string input)
    {
        var groups = input.Split([' ', '\n', '\r', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
        if (groups.Length == 0 || groups.Any(g => g.Any(c => c is not ('0' or '1'))))
        {
            return null;
        }

        if (groups.Length == 1)
        {
            var bits = groups[0];
            if (bits.Length < 8 || bits.Length % 8 != 0)
            {
                return null;
            }

            groups = [.. Enumerable.Range(0, bits.Length / 8).Select(i => bits.Substring(i * 8, 8))];
        }

        if (groups.Length < 2 || groups.Any(g => g.Length > 8 || g.Length < 5))
        {
            return null; // 7- or 8-bit groups; a lone "101" is not binary-encoded text
        }

        return [.. groups.Select(g => Convert.ToByte(g, 2))];
    }

    public static byte[]? DecimalBytes(string input)
    {
        var groups = input.Split([' ', ',', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (groups.Length < 3)
        {
            return null;
        }

        var bytes = new byte[groups.Length];
        for (var i = 0; i < groups.Length; i++)
        {
            if (groups[i].Length > 3 || !byte.TryParse(groups[i], NumberStyles.None, CultureInfo.InvariantCulture, out bytes[i]))
            {
                return null;
            }
        }

        return bytes;
    }

    public static byte[]? UrlDecode(string input)
    {
        if (!input.Contains('%', StringComparison.Ordinal))
        {
            return null;
        }

        var decoded = WebUtility.UrlDecode(input);
        return decoded == input ? null : Encoding.UTF8.GetBytes(decoded);
    }

    public static byte[]? MorseCode(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length < 3 || trimmed.Any(c => c is not ('.' or '-' or '_' or ' ' or '/' or '|' or '\n')) || !trimmed.Any(c => c is '.' or '-' or '_'))
        {
            return null;
        }

        var builder = new StringBuilder();
        var known = 0;
        var total = 0;
        foreach (var word in trimmed.Replace('_', '-').Split(['/', '|', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            foreach (var letter in word.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                total++;
                if (Morse.TryGetValue(letter, out var c))
                {
                    known++;
                    builder.Append(c);
                }
                else
                {
                    builder.Append('?');
                }
            }
        }

        return total >= 2 && known >= total * 0.8 ? Encoding.UTF8.GetBytes(builder.ToString()) : null;
    }

    public static byte[]? Rot13(string input)
    {
        var output = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            output.Append(c switch
            {
                >= 'a' and <= 'z' => (char)('a' + ((c - 'a' + 13) % 26)),
                >= 'A' and <= 'Z' => (char)('A' + ((c - 'A' + 13) % 26)),
                _ => c,
            });
        }

        return Encoding.UTF8.GetBytes(output.ToString());
    }

    public static byte[]? Rot47(string input)
    {
        var output = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            output.Append(c is >= '!' and <= '~' ? (char)('!' + ((c - '!' + 47) % 94)) : c);
        }

        return Encoding.UTF8.GetBytes(output.ToString());
    }

    private static string StripWhitespace(string input)
    {
        var builder = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (!char.IsWhiteSpace(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static bool IsBase64(string compact, char c62, char c63)
    {
        var padding = compact.Length - compact.TrimEnd('=').Length;
        if (padding > 2)
        {
            return false;
        }

        for (var i = 0; i < compact.Length - padding; i++)
        {
            var c = compact[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != c62 && c != c63)
            {
                return false;
            }
        }

        return true;
    }

    private static string Pad(string compact) => compact.TrimEnd('=') + new string('=', (4 - (compact.TrimEnd('=').Length % 4)) % 4);
}
