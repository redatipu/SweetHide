using System.CommandLine;

using SweetHide.Core;
using SweetHide.Core.Crypto;
using SweetHide.Core.Imaging;

namespace SweetHide.Cli;

/// <summary>Builds the <c>sweethide</c> command tree.</summary>
internal static class CliApp
{
    public static RootCommand Create(CliContext context)
    {
        var root = new RootCommand(
            "SweetHide: hide encrypted text and files inside images (PNG output).\n" +
            $"Password: --password-stdin, the {ConsolePasswordProvider.EnvironmentVariable} environment variable, or an interactive prompt.\n" +
            "Use - as a file name to read from stdin or (for hide --output) write to stdout.\n" +
            "Exit codes: 0 ok, 1 error, 2 nothing found / wrong password, 3 not enough capacity, 4 data damaged,\n" +
            "5 made by a newer version, 6 image holds one split share, 7 not enough shares, 8 shares do not match.");

        var json = new Option<bool>("--json")
        {
            Description = "Print results as JSON on stdout (always an array, one element per input).",
            Recursive = true,
        };
        root.Options.Add(json);

        root.Add(HideCommand(context, json));
        root.Add(RevealCommand(context, json));
        root.Add(KeyCommand(context, json));
        root.Add(CapacityCommand(context, json));
        root.Add(AnalyzeCommand(context, json));
        root.Add(AuditCommand(context, json));
        root.Add(ScanCommand.Create(context, json));
        root.Add(CompletionCommand.Create(context, root));
        return root;
    }

    public static int ExitCodeFor(StegoException ex) => ex.Error switch
    {
        StegoError.NotFoundOrWrongPassword => ExitCodes.NotFoundOrWrongPassword,
        StegoError.InsufficientCapacity => ExitCodes.InsufficientCapacity,
        StegoError.Corrupted => ExitCodes.Corrupted,
        StegoError.UnsupportedFormat => ExitCodes.UnsupportedFormat,
        StegoError.SplitShare => ExitCodes.SplitShare,
        StegoError.NotEnoughShares => ExitCodes.NotEnoughShares,
        StegoError.SharesDoNotMatch => ExitCodes.SharesDoNotMatch,
        _ => ExitCodes.Error,
    };

    private static Command HideCommand(CliContext context, Option<bool> json)
    {
        var input = InputOption("Cover image(s): PNG, JPEG, BMP, GIF or WebP. Repeatable for batch mode (needs --output-dir).");
        var output = new Option<string>("--output", "-o") { Description = "Where to write the stego image (.png/.webp/.bmp/.tiff, or .jpg for --robust), or - for stdout." };
        var format = new Option<LosslessFormat>("--format")
        {
            Description = "Image format for --output-dir and stdout: Png (default), WebP (lossless), Bmp or Tiff.",
            DefaultValueFactory = _ => LosslessFormat.Png,
        };
        var outputDir = new Option<DirectoryInfo>("--output-dir") { Description = "Folder for batch output: one file per cover." };
        var text = new Option<string[]>("--text", "-t") { Description = "A text message to hide. Repeatable." };
        var textFile = new Option<string[]>("--text-file") { Description = "Hide the contents of a UTF-8 text file (or - for stdin) as a message. Repeatable." };
        var file = new Option<string[]>("--file", "-f") { Description = "A file to hide; a folder is hidden as a .zip. Repeatable." };
        var bits = BitsOption();
        var kdf = KdfOption();
        var keyFile = new Option<string>("--key-file") { Description = "A file whose bytes are a second factor; revealing needs the same file." };
        var adaptive = new Option<bool>("--adaptive") { Description = "Prefer textured regions (harder to detect, less room). Not for deniable/palette." };
        var trueColor = new Option<bool>("--truecolor") { Description = "Expand a palette PNG cover to true colour instead of hiding in the palette." };
        var keepExif = new Option<bool>("--keep-exif") { Description = "Copy the cover's EXIF (minus GPS) into the PNG so it looks like an exported photo." };
        var recipient = new Option<string[]>("--recipient") { Description = "Hide for a public key (sweethide1…, or @file); repeatable. No password is used." };
        var decoyText = new Option<string[]>("--decoy-text") { Description = "Deniable mode: a decoy message unlocked by a second password. Repeatable." };
        var decoyFile = new Option<string[]>("--decoy-file") { Description = "Deniable mode: a decoy file unlocked by a second password. Repeatable." };
        var decoyRandom = new Option<bool>("--decoy-random") { Description = "Deniable mode: fill the other half with noise (no second payload)." };
        var split = new Option<int?>("--split") { Description = "Split across the covers: K shares needed to rebuild (needs 2+ covers and --output-dir)." };
        var robust = new Option<string?>("--robust") { Description = "Make a JPEG that survives a chat app: line or telegram." };
        robust.AcceptOnlyFromAmong("line", "telegram");
        var force = new Option<bool>("--force") { Description = "Overwrite output files that already exist." };
        var passwordStdin = PasswordStdinOption();

        var command = new Command("hide", "Hide text and/or files in one or more images.")
        {
            input, output, outputDir, format, text, textFile, file, bits, kdf, keyFile, adaptive, trueColor, keepExif,
            recipient, decoyText, decoyFile, decoyRandom, split, robust, force, passwordStdin,
        };

        command.SetAction(parse => Run(context, parse.GetValue(json), () => new HideCommand(context).Execute(new HideOptions(
            parse.GetValue(input) ?? [],
            parse.GetValue(output),
            parse.GetValue(outputDir)?.FullName,
            parse.GetValue(text) ?? [],
            parse.GetValue(textFile) ?? [],
            parse.GetValue(file) ?? [],
            parse.GetValue(bits),
            parse.GetValue(force),
            parse.GetValue(passwordStdin),
            parse.GetValue(format))
        {
            Kdf = ParseKdf(parse.GetValue(kdf)),
            KeyFile = parse.GetValue(keyFile),
            Adaptive = parse.GetValue(adaptive),
            TrueColor = parse.GetValue(trueColor),
            KeepExif = parse.GetValue(keepExif),
            Recipients = parse.GetValue(recipient) ?? [],
            DecoyTexts = parse.GetValue(decoyText) ?? [],
            DecoyFiles = parse.GetValue(decoyFile) ?? [],
            DecoyRandom = parse.GetValue(decoyRandom),
            Split = parse.GetValue(split),
            Robust = parse.GetValue(robust),
        })));
        return command;
    }

    private static Command RevealCommand(CliContext context, Option<bool> json)
    {
        var input = InputOption("Image(s) that may contain hidden data, or - for stdin. Repeatable.");
        var outputDir = new Option<DirectoryInfo>("--output-dir", "-o")
        {
            Description = "Folder for revealed files (default: current folder; one sub-folder per image when several). Messages go to stdout.",
        };
        var list = new Option<bool>("--list", "-l") { Description = "Only list what is hidden; write no files." };
        var force = new Option<bool>("--force") { Description = "Overwrite existing files instead of picking a new name." };
        var keyFile = new Option<string>("--key-file") { Description = "The key file used when hiding, if any." };
        var identity = new Option<string>("--identity") { Description = "Your identity file (from 'key new'); reveals data hidden for your public key." };
        var passwordStdin = PasswordStdinOption();

        var command = new Command("reveal", "Reveal hidden text and files (falls back to robust mode and combines split shares).")
        {
            input, outputDir, list, force, keyFile, identity, passwordStdin,
        };
        command.SetAction(parse => Run(context, parse.GetValue(json), () => new RevealCommand(context).Execute(new RevealOptions(
            parse.GetValue(input) ?? [],
            parse.GetValue(outputDir) ?? new DirectoryInfo(Environment.CurrentDirectory),
            parse.GetValue(list),
            parse.GetValue(force),
            parse.GetValue(passwordStdin))
        {
            KeyFile = parse.GetValue(keyFile),
            Identity = parse.GetValue(identity),
        })));
        return command;
    }

    private static Command KeyCommand(CliContext context, Option<bool> json)
    {
        var passwordStdin = PasswordStdinOption();

        var newOutput = new Option<string>("--output", "-o") { Description = "Where to write the identity file.", Required = true };
        var newForce = new Option<bool>("--force") { Description = "Overwrite an existing identity file." };
        var newCommand = new Command("new", "Create a passphrase-protected identity file.") { newOutput, newForce, passwordStdin };
        newCommand.SetAction(parse => Run(context, parse.GetValue(json), () =>
            KeyCommands.New(context, parse.GetValue(newOutput)!, parse.GetValue(newForce), parse.GetValue(passwordStdin))));

        var publicPath = new Argument<string>("identity") { Description = "The identity file." };
        var publicCommand = new Command("public", "Print the sweethide1… recipient of an identity file.") { publicPath, passwordStdin };
        publicCommand.SetAction(parse => Run(context, parse.GetValue(json), () =>
            KeyCommands.Public(context, parse.GetValue(publicPath)!, parse.GetValue(passwordStdin))));

        return new Command("key", "Make and inspect public-key identities.") { newCommand, publicCommand };
    }

    private static Command CapacityCommand(CliContext context, Option<bool> json)
    {
        var input = InputOption("Cover image(s) to measure, or - for stdin.");
        var recipients = new Option<int?>("--recipients") { Description = "Also show the capacity for a public-key embed to this many recipients." };
        recipients.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int?>() is { } count && (count < 1 || count > StegoEngine.MaxRecipients))
            {
                result.AddError($"--recipients must be between 1 and {StegoEngine.MaxRecipients}.");
            }
        });
        var command = new Command("capacity", "Show how much an image can hold (normal, deniable, public-key and robust) and its cover score.") { input, recipients };
        command.SetAction(parse => Run(context, parse.GetValue(json), () => InfoCommands.Capacity(context, parse.GetValue(input) ?? [], parse.GetValue(recipients))));
        return command;
    }

    private static Command AnalyzeCommand(CliContext context, Option<bool> json)
    {
        var input = InputOption("Image(s) to analyse, or - for stdin.");
        var plane = new Option<FileInfo>("--bit-plane") { Description = "Also write the chosen bit plane as a black/white PNG." };
        var bit = new Option<int>("--bit") { Description = "Bit plane to render (0 = least significant).", DefaultValueFactory = _ => 0 };
        bit.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int>() is < 0 or > 7)
            {
                result.AddError("--bit must be between 0 and 7.");
            }
        });

        var command = new Command("analyze", "Steganalysis: LSB statistics and the chi-square attack.") { input, plane, bit };
        command.SetAction(parse => Run(context, parse.GetValue(json), () =>
            InfoCommands.Analyze(context, parse.GetValue(input) ?? [], parse.GetValue(plane), parse.GetValue(bit))));
        return command;
    }

    private static Command AuditCommand(CliContext context, Option<bool> json)
    {
        var image = new Argument<string>("image") { Description = "One of your own stego images." };
        image.Validators.Add(result =>
        {
            var path = result.GetValueOrDefault<string>();
            if (!string.IsNullOrEmpty(path) && !File.Exists(path))
            {
                result.AddError($"File does not exist: '{path}'.");
            }
        });
        var wordlist = new Option<string>("--wordlist", "-w") { Description = "A wordlist, one candidate password per line.", Required = true };
        var keyFile = new Option<string>("--key-file") { Description = "The key file used when hiding, if any." };

        var command = new Command("audit", "Check whether your image's password is in a wordlist (Ctrl+C to stop).") { image, wordlist, keyFile };
        command.SetAction(parse => Run(context, parse.GetValue(json), () => new AuditCommand(context).Execute(new AuditOptions(
            parse.GetValue(image)!,
            parse.GetValue(wordlist)!)
        {
            KeyFile = parse.GetValue(keyFile),
        })));
        return command;
    }

    internal static Option<string[]> InputOption(string description)
    {
        var option = new Option<string[]>("--input", "-i") { Description = description, Required = true };
        option.Validators.Add(result =>
        {
            foreach (var path in result.GetValueOrDefault<string[]>() ?? [])
            {
                if (!Inputs.IsStandardStream(path) && !File.Exists(path))
                {
                    result.AddError($"File does not exist: '{path}'.");
                }
            }
        });
        return option;
    }

    private static Option<int> BitsOption()
    {
        var option = new Option<int>("--bits", "-b")
        {
            Description = "Low bits used per colour channel: 1 (default, invisible) to 4 (more room, visible noise).",
            DefaultValueFactory = _ => StegoEngine.DefaultBitsPerChannel,
        };
        option.Validators.Add(result =>
        {
            if (!StegoEngine.SupportedBitsPerChannel.Contains(result.GetValueOrDefault<int>()))
            {
                result.AddError("--bits must be 1, 2, 3 or 4.");
            }
        });
        return option;
    }

    private static Option<string> KdfOption()
    {
        var option = new Option<string>("--kdf")
        {
            Description = "Password cost: fast, normal (default) or paranoid.",
            DefaultValueFactory = _ => "normal",
        };
        option.AcceptOnlyFromAmong("fast", "normal", "paranoid");
        return option;
    }

    private static KdfProfile ParseKdf(string? value) => value?.ToLowerInvariant() switch
    {
        "fast" => KdfProfile.Fast,
        "paranoid" => KdfProfile.Paranoid,
        _ => KdfProfile.Normal,
    };

    internal static Option<bool> PasswordStdinOption() =>
        new("--password-stdin") { Description = "Read the password from the first line of standard input." };

    /// <summary>Turns expected failures into a one-line message (and JSON when asked) plus an exit code.</summary>
    internal static int Run(CliContext context, bool json, Func<int> action)
    {
        context.Json = json;
        try
        {
            return action();
        }
        catch (CliException ex)
        {
            return Fail(context, ex.Message, ex.ExitCode);
        }
        catch (StegoException ex)
        {
            return Fail(context, ex.Message, ExitCodeFor(ex));
        }
        catch (Exception ex) when (ex is InvalidImageException or IOException or UnauthorizedAccessException)
        {
            return Fail(context, ex.Message, ExitCodes.Error);
        }
    }

    private static int Fail(CliContext context, string message, int exitCode)
    {
        context.Error.WriteLine($"error: {message}");
        if (context.Json)
        {
            JsonOutput.WriteError(context, message, exitCode);
        }

        return exitCode;
    }
}
