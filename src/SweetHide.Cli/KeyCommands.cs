using SweetHide.Core.Crypto;

namespace SweetHide.Cli;

/// <summary>The <c>key</c> command group: make a password-protected identity, and print its public recipient.</summary>
internal static class KeyCommands
{
    public static int New(CliContext context, string output, bool force, bool passphraseFromStdin)
    {
        if (string.IsNullOrEmpty(output))
        {
            throw new CliException("Give an output path with -o, e.g. key new -o me.key.");
        }

        if (!force && File.Exists(output))
        {
            throw new CliException($"{Path.GetFullPath(output)} already exists. Use --force to overwrite.");
        }

        var passphrase = context.Passwords.GetPassword(new PasswordPrompt(
            "Identity passphrase: ", ConsolePasswordProvider.IdentityEnvironmentVariable, Confirm: true, FromStdin: passphraseFromStdin,
            ConfirmLabel: "Confirm passphrase: "));

        using var identity = Identity.Generate();
        var text = identity.Export(passphrase, context.Kdf);
        var full = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(full) ?? Environment.CurrentDirectory);
        File.WriteAllText(full, text);

        var recipient = identity.Recipient.ToString();
        var report = new KeyReport(full, recipient);
        if (context.Json)
        {
            JsonOutput.Write<KeyReport>(context, [report]);
        }
        else
        {
            context.Out.WriteLine($"Wrote identity to {full}");
            context.Out.WriteLine($"Recipient (share this): {recipient}");
            context.Error.WriteLine("note: keep the identity file and its passphrase safe. Anyone with both can read everything hidden for you.");
        }

        return ExitCodes.Success;
    }

    public static int Public(CliContext context, string path, bool passphraseFromStdin)
    {
        if (!File.Exists(path))
        {
            throw new CliException($"Identity file not found: {path}");
        }

        var text = File.ReadAllText(path);
        var recipient = PublicLine(text) ?? FromImport(context, text, passphraseFromStdin);
        var report = new KeyReport(null, recipient);
        if (context.Json)
        {
            JsonOutput.Write<KeyReport>(context, [report]);
        }
        else
        {
            context.Out.WriteLine(recipient);
        }

        return ExitCodes.Success;
    }

    /// <summary>The informational <c>Public-Key:</c> line, when it is present and parses as a recipient.</summary>
    private static string? PublicLine(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            const string Label = "Public-Key: ";
            if (line.StartsWith(Label, StringComparison.Ordinal) && Recipient.TryParse(line[Label.Length..], out var recipient))
            {
                return recipient.ToString();
            }
        }

        return null;
    }

    private static string FromImport(CliContext context, string text, bool passphraseFromStdin)
    {
        var passphrase = context.Passwords.GetPassword(new PasswordPrompt(
            "Identity passphrase: ", ConsolePasswordProvider.IdentityEnvironmentVariable, FromStdin: passphraseFromStdin));
        try
        {
            using var identity = Identity.Import(text, passphrase);
            return identity.Recipient.ToString();
        }
        catch (FormatException ex)
        {
            throw new CliException($"Not a SweetHide identity file: {ex.Message}");
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw new CliException("Wrong passphrase, or the identity file was modified.", ExitCodes.NotFoundOrWrongPassword);
        }
    }
}
