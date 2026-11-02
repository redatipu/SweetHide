using System.CommandLine;

using SweetHide.Core;
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
            "Exit codes: 0 ok, 1 error, 2 nothing found / wrong password, 3 not enough capacity, 4 data damaged.");

        var json = new Option<bool>("--json")
        {
            Description = "Print results as JSON on stdout (always an array, one element per input).",
            Recursive = true,
        };
        root.Options.Add(json);

        root.Add(HideCommand(context, json));
        root.Add(RevealCommand(context, json));
        root.Add(CapacityCommand(context, json));
        root.Add(AnalyzeCommand(context, json));
        root.Add(CompletionCommand.Create(context, root));
        return root;
    }

    public static int ExitCodeFor(StegoException ex) => ex.Error switch
    {
        StegoError.NotFoundOrWrongPassword => ExitCodes.NotFoundOrWrongPassword,
        StegoError.InsufficientCapacity => ExitCodes.InsufficientCapacity,
        StegoError.Corrupted => ExitCodes.Corrupted,
        _ => ExitCodes.Error,
    };

    private static Command HideCommand(CliContext context, Option<bool> json)
    {
        var input = InputOption("Cover image(s): PNG, JPEG, BMP, GIF or WebP. Repeatable for batch mode (needs --output-dir).");
        var output = new Option<string>("--output", "-o") { Description = "Where to write the stego image (.png), or - for stdout." };
        var outputDir = new Option<DirectoryInfo>("--output-dir") { Description = "Folder for batch output: one <cover name>.png per cover." };
        var text = new Option<string[]>("--text", "-t") { Description = "A text message to hide. Repeatable." };
        var textFile = new Option<string[]>("--text-file") { Description = "Hide the contents of a UTF-8 text file (or - for stdin) as a message. Repeatable." };
        var file = new Option<string[]>("--file", "-f") { Description = "A file to hide; a folder is hidden as a .zip. Repeatable." };
        var bits = BitsOption();
        var force = new Option<bool>("--force") { Description = "Overwrite output files that already exist." };
        var passwordStdin = PasswordStdinOption();

        var command = new Command("hide", "Hide text and/or files in one or more images.")
        {
            input, output, outputDir, text, textFile, file, bits, force, passwordStdin,
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
            parse.GetValue(passwordStdin)))));
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
        var passwordStdin = PasswordStdinOption();

        var command = new Command("reveal", "Reveal hidden text and files.") { input, outputDir, list, force, passwordStdin };
        command.SetAction(parse => Run(context, parse.GetValue(json), () => new RevealCommand(context).Execute(new RevealOptions(
            parse.GetValue(input) ?? [],
            parse.GetValue(outputDir) ?? new DirectoryInfo(Environment.CurrentDirectory),
            parse.GetValue(list),
            parse.GetValue(force),
            parse.GetValue(passwordStdin)))));
        return command;
    }

    private static Command CapacityCommand(CliContext context, Option<bool> json)
    {
        var input = InputOption("Cover image(s) to measure, or - for stdin.");
        var command = new Command("capacity", "Show how much an image can hold at each bit depth.") { input };
        command.SetAction(parse => Run(context, parse.GetValue(json), () => InfoCommands.Capacity(context, parse.GetValue(input) ?? [])));
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

    private static Option<string[]> InputOption(string description)
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

    private static Option<bool> PasswordStdinOption() =>
        new("--password-stdin") { Description = "Read the password from the first line of standard input." };

    /// <summary>Turns expected failures into a one-line message (and JSON when asked) plus an exit code.</summary>
    private static int Run(CliContext context, bool json, Func<int> action)
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
