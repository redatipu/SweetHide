using System.Text;

namespace SweetHide.Cli;

internal interface IPasswordProvider
{
    /// <param name="confirm">Ask twice (when hiding) so a typo does not lock the data away forever.</param>
    /// <param name="fromStdin">Read the first line of standard input instead of prompting.</param>
    string GetPassword(bool confirm, bool fromStdin);
}

/// <summary>
/// Password sources in priority order: <c>--password-stdin</c>, the <c>SWEETHIDE_PASSWORD</c> environment variable,
/// then an interactive masked prompt. There is deliberately no <c>--password</c> option: command lines end up in
/// shell history and process listings.
/// </summary>
internal sealed class ConsolePasswordProvider : IPasswordProvider
{
    public const string EnvironmentVariable = "SWEETHIDE_PASSWORD";

    public string GetPassword(bool confirm, bool fromStdin)
    {
        if (fromStdin)
        {
            var line = Console.In.ReadLine();
            return string.IsNullOrEmpty(line) ? throw new CliException("No password on standard input.") : line;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            return fromEnvironment;
        }

        if (Console.IsInputRedirected)
        {
            throw new CliException($"No password given. Use --password-stdin or set {EnvironmentVariable}.");
        }

        var password = Prompt("Password: ");
        if (password.Length == 0)
        {
            throw new CliException("Password must not be empty.");
        }

        if (password.Length < 8)
        {
            Console.Error.WriteLine("warning: short passwords can be brute-forced; 12+ characters or a passphrase is better.");
        }

        if (confirm && Prompt("Confirm password: ") != password)
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
