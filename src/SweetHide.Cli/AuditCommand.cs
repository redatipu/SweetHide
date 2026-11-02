using SweetHide.Core;

namespace SweetHide.Cli;

internal sealed record AuditOptions(string Image, string Wordlist)
{
    public string? KeyFile { get; init; }
}

/// <summary>
/// <c>audit</c>: tries a wordlist against one of your own stego images, so you can check the password you chose is not
/// in a common list. Progress goes to stderr; Ctrl+C stops between candidates.
/// </summary>
internal sealed class AuditCommand(CliContext context)
{
    public int Execute(AuditOptions options)
    {
        if (!File.Exists(options.Image))
        {
            throw new CliException($"Image not found: {options.Image}");
        }

        if (!File.Exists(options.Wordlist))
        {
            throw new CliException($"Wordlist not found: {options.Wordlist}");
        }

        var image = Inputs.ReadImage(context, options.Image, applyOrientation: false);
        var keyFile = CryptoInputs.LoadKeyFile(options.KeyFile);
        var engine = new StegoEngine(context.Kdf);
        var probe = engine.CreatePasswordProbe(image);
        if (!probe.CanHoldPasswordData)
        {
            throw new CliException($"{Inputs.DisplayName(options.Image)} is too small to hold password-protected data.");
        }

        if (!context.Json)
        {
            context.Error.WriteLine($"Auditing with the {probe.Profile} KDF profile. Press Ctrl+C to stop.");
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true; // don't kill the process; cancel the probe so partial progress is reported
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;

        long tested = 0;
        var lastReport = 0L;
        var progress = new Progress<long>(count =>
        {
            tested = count;
            if (!context.Json && count - lastReport >= 50)
            {
                lastReport = count;
                context.Error.Write($"\rtested {count:N0}…");
            }
        });

        string? found = null;
        var cancelled = false;
        try
        {
            found = probe.FindPassword(ReadWords(options.Wordlist), keyFile, progress, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }

        if (!context.Json && lastReport > 0)
        {
            context.Error.WriteLine();
        }

        var report = new AuditReport(Inputs.DisplayName(options.Image), tested, found is not null, found, cancelled);
        if (context.Json)
        {
            JsonOutput.Write<AuditReport>(context, [report]);
            return found is not null ? ExitCodes.Success : ExitCodes.NotFoundOrWrongPassword;
        }

        if (found is not null)
        {
            context.Out.WriteLine($"Password found in the list after {tested:N0} candidate(s): {found}");
            context.Error.WriteLine("warning: this password is in the wordlist, so an attacker with the image could find it too. Choose a stronger one.");
            return ExitCodes.Success;
        }

        if (cancelled)
        {
            context.Out.WriteLine($"Stopped after {tested:N0} candidate(s); no match so far.");
            return ExitCodes.Error;
        }

        context.Out.WriteLine($"Not in the wordlist ({tested:N0} candidate(s) tried).");
        return ExitCodes.NotFoundOrWrongPassword;
    }

    private static IEnumerable<string> ReadWords(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            var word = line.TrimEnd('\r', '\n');
            if (word.Length > 0)
            {
                yield return word;
            }
        }
    }
}
