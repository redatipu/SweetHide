using System.Security.Cryptography;

using SweetHide.Core.Crypto;

namespace SweetHide.Cli;

/// <summary>Loads the key material the commands accept: key files, recipient keys and identity files.</summary>
internal static class CryptoInputs
{
    /// <summary>A <see cref="KeyFile"/> from a path, or null when none was given.</summary>
    public static KeyFile? LoadKeyFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (!File.Exists(path))
        {
            throw new CliException($"Key file not found: {path}");
        }

        try
        {
            return KeyFile.FromFile(path);
        }
        catch (ArgumentException ex)
        {
            throw new CliException($"Key file '{path}': {ex.Message}");
        }
    }

    /// <summary>
    /// Parses the repeatable <c>--recipient</c> values. A plain value is one <c>sweethide1…</c> key; an <c>@file</c>
    /// value reads one key per line (blank lines and <c>#</c> comments are ignored).
    /// </summary>
    public static IReadOnlyList<Recipient> ParseRecipients(IReadOnlyList<string> values)
    {
        var recipients = new List<Recipient>();
        foreach (var value in values)
        {
            if (value.StartsWith('@'))
            {
                var path = value[1..];
                if (!File.Exists(path))
                {
                    throw new CliException($"Recipient file not found: {path}");
                }

                foreach (var line in File.ReadLines(path))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                    {
                        continue;
                    }

                    recipients.Add(ParseRecipient(trimmed));
                }
            }
            else
            {
                recipients.Add(ParseRecipient(value));
            }
        }

        if (recipients.Count == 0)
        {
            throw new CliException("No recipients found. Give at least one --recipient (a sweethide1… key, or @file).");
        }

        return recipients;
    }

    /// <summary>Reads and decrypts an identity file, prompting for its passphrase.</summary>
    public static Identity LoadIdentity(CliContext context, string path, bool fromStdin)
    {
        if (!File.Exists(path))
        {
            throw new CliException($"Identity file not found: {path}");
        }

        var text = File.ReadAllText(path);
        var passphrase = context.Passwords.GetPassword(new PasswordPrompt(
            "Identity passphrase: ",
            ConsolePasswordProvider.IdentityEnvironmentVariable,
            FromStdin: fromStdin));
        try
        {
            return Identity.Import(text, passphrase);
        }
        catch (FormatException ex)
        {
            throw new CliException($"'{path}' is not a SweetHide identity file: {ex.Message}");
        }
        catch (CryptographicException)
        {
            throw new CliException("Wrong passphrase, or the identity file was modified.", ExitCodes.NotFoundOrWrongPassword);
        }
    }

    private static Recipient ParseRecipient(string value)
    {
        try
        {
            return Recipient.Parse(value);
        }
        catch (FormatException ex)
        {
            throw new CliException($"Invalid recipient '{value}': {ex.Message}");
        }
    }
}
