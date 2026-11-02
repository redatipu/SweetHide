using System.Text;

namespace SweetHide.Cli;

/// <summary>One request for a password, so decoy passwords and identity passphrases read the same way as the main one.</summary>
/// <param name="Label">What the interactive prompt shows, e.g. "Password: ".</param>
/// <param name="EnvironmentVariable">Environment variable checked before prompting (null skips it).</param>
/// <param name="Confirm">Ask twice (when hiding) so a typo does not lock the data away forever.</param>
/// <param name="FromStdin">Read the first line of standard input instead of prompting.</param>
/// <param name="ConfirmLabel">The prompt shown for the second entry when <paramref name="Confirm"/> is set.</param>
internal sealed record PasswordPrompt(
    string Label,
    string? EnvironmentVariable = ConsolePasswordProvider.EnvironmentVariable,
    bool Confirm = false,
    bool FromStdin = false,
    string ConfirmLabel = "Confirm password: ");

internal interface IPasswordProvider
{
    string GetPassword(PasswordPrompt prompt);
}

/// <summary>
/// Password sources in priority order: <c>--password-stdin</c>, the prompt's environment variable (by default
/// <c>SWEETHIDE_PASSWORD</c>), then an interactive masked prompt. There is deliberately no <c>--password</c> option:
/// command lines end up in shell history and process listings.
/// </summary>
internal sealed class ConsolePasswordProvider : IPasswordProvider
{
    public const string EnvironmentVariable = "SWEETHIDE_PASSWORD";
    public const string DecoyEnvironmentVariable = "SWEETHIDE_DECOY_PASSWORD";
    public const string IdentityEnvironmentVariable = "SWEETHIDE_IDENTITY_PASSPHRASE";

    public string GetPassword(PasswordPrompt prompt)
    {
        if (prompt.FromStdin)
        {
            var line = Console.In.ReadLine();
            return string.IsNullOrEmpty(line) ? throw new CliException("No password on standard input.") : line;
        }

        if (prompt.EnvironmentVariable is { } name)
        {
            var fromEnvironment = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(fromEnvironment))
            {
                return fromEnvironment;
            }
        }

        if (Console.IsInputRedirected)
        {
            var hint = prompt.EnvironmentVariable is { } variable ? $"Use --password-stdin or set {variable}." : "No console is attached.";
            throw new CliException($"No password given. {hint}");
        }

        var password = Prompt(prompt.Label);
        if (password.Length == 0)
        {
            throw new CliException("Password must not be empty.");
        }

        if (password.Length < 8)
        {
            Console.Error.WriteLine("warning: short passwords can be brute-forced; 12+ characters or a passphrase is better.");
        }

        if (prompt.Confirm && Prompt(prompt.ConfirmLabel) != password)
        {
            throw new CliException("Passwords do not match.");
        }

        return password;
    }

    private static string Prompt(string label)
    {
        Console.Error.Write(label);
        var builder = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
            }
        }

        Console.Error.WriteLine();
        return builder.ToString();
    }
}
