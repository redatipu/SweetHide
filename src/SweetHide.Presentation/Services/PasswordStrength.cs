using System.Globalization;

namespace SweetHide.Presentation.Services;

public enum PasswordStrength
{
    None = 0,
    Weak = 1,
    Fair = 2,
    Good = 3,
    Strong = 4,
}

/// <summary>
/// A deliberately simple strength hint: length matters most, character variety a little, and well-known passwords
/// are always weak. It nudges people towards long passphrases (which work well in Thai too).
/// </summary>
public static class PasswordStrengthMeter
{
    private static readonly string[] CommonPasswords =
    [
        "password", "123456", "12345678", "123456789", "qwerty", "abc123", "111111", "iloveyou", "admin", "letmein",
        "welcome", "sweethide", "football", "monkey", "dragon",
    ];

    public static PasswordStrength Evaluate(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return PasswordStrength.None;
        }

        var length = new StringInfo(password).LengthInTextElements; // Thai vowels/tone marks don't inflate the count
        var lower = password.ToLowerInvariant();
        if ((length < 16 && CommonPasswords.Any(common => lower.Contains(common, StringComparison.Ordinal)))
            || password.Distinct().Count() <= 2)
        {
            return PasswordStrength.Weak;
        }

        var classes = 0;
        classes += password.Any(char.IsAsciiLetterLower) ? 1 : 0;
        classes += password.Any(char.IsAsciiLetterUpper) ? 1 : 0;
        classes += password.Any(char.IsAsciiDigit) ? 1 : 0;
        classes += password.Any(c => char.IsPunctuation(c) || char.IsSymbol(c) || char.IsWhiteSpace(c)) ? 1 : 0;
        classes += password.Any(c => c > 127 && char.IsLetter(c)) ? 1 : 0;

        return length switch
        {
            < 8 => PasswordStrength.Weak,
            < 12 => classes >= 3 ? PasswordStrength.Fair : PasswordStrength.Weak,
            < 16 => classes >= 2 ? PasswordStrength.Good : PasswordStrength.Fair,
            _ => classes >= 2 ? PasswordStrength.Strong : PasswordStrength.Good,
        };
    }
}
