using System.CommandLine;

using Magi.Core;
using Magi.Core.Imaging;

namespace Magi.Cli;

/// <summary>Builds the <c>magi</c> command tree.</summary>
internal static class CliApp
{
    public static RootCommand Create(CliContext context)
    {
        var root = new RootCommand(
            "Magi: hide encrypted text and files inside images (PNG output).\n" +
            $"Password: --password-stdin, the {ConsolePasswordProvider.EnvironmentVariable} environment variable, or an interactive prompt.\n" +
            "Exit codes: 0 ok, 1 error, 2 nothing found / wrong password, 3 not enough capacity, 4 data damaged.");

        root.Add(HideCommand(context));
        root.Add(RevealCommand(context));
        root.Add(CapacityCommand(context));
        root.Add(AnalyzeCommand(context));
        return root;
    }

    private static Command HideCommand(CliContext context)
    {
        var input = InputOption("Cover image (PNG, JPEG, BMP, GIF or WebP).");
        var output = new Option<FileInfo>("--output", "-o") { Description = "Where to write the stego image (.png).", Required = true };
        var text = new Option<string[]>("--text", "-t") { Description = "A text message to hide. Repeatable.", AllowMultipleArgumentsPerToken = false };
        var textFile = new Option<FileInfo[]>("--text-file") { Description = "Hide the contents of a UTF-8 text file as a message. Repeatable." };
        var file = new Option<FileInfo[]>("--file", "-f") { Description = "A file to hide. Repeatable." };
        var bits = BitsOption();
        var force = new Option<bool>("--force") { Description = "Overwrite the output file if it exists." };
        var passwordStdin = PasswordStdinOption();

        var command = new Command("hide", "Hide text and/or files in an image.")
        {
            input, output, text, textFile, file, bits, force, passwordStdin,
        };

        command.SetAction(parse => Run(context, () => new HideCommand(context).Execute(new HideOptions(
            parse.GetValue(input)!,
            parse.GetValue(output)!,
            parse.GetValue(text) ?? [],
            parse.GetValue(textFile) ?? [],
            parse.GetValue(file) ?? [],
            parse.GetValue(bits),
            parse.GetValue(force),
            parse.GetValue(passwordStdin)))));
        return command;
    }

    private static Command RevealCommand(CliContext context)
    {
        var input = InputOption("Image that may contain hidden data.");
        var outputDir = new Option<DirectoryInfo>("--output-dir", "-o")
        {
            Description = "Folder for revealed files (default: current folder). Messages are printed to stdout.",
        };
        var list = new Option<bool>("--list", "-l") { Description = "Only list what is hidden; write no files." };
        var force = new Option<bool>("--force") { Description = "Overwrite existing files instead of picking a new name." };
        var passwordStdin = PasswordStdinOption();

        var command = new Command("reveal", "Reveal hidden text and files.") { input, outputDir, list, force, passwordStdin };
        command.SetAction(parse => Run(context, () => new RevealCommand(context).Execute(new RevealOptions(
            parse.GetValue(input)!,
            parse.GetValue(outputDir) ?? new DirectoryInfo(Environment.CurrentDirectory),
            parse.GetValue(list),
            parse.GetValue(force),
            parse.GetValue(passwordStdin)))));
        return command;
    }

    private static Command CapacityCommand(CliContext context)
    {
        var input = InputOption("Cover image to measure.");
        var command = new Command("capacity", "Show how much an image can hold at each bit depth.") { input };
        command.SetAction(parse => Run(context, () => InfoCommands.Capacity(context, parse.GetValue(input)!)));
        return command;
    }

    private static Command AnalyzeCommand(CliContext context)
    {
        var input = InputOption("Image to analyse.");
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
        command.SetAction(parse => Run(context, () => InfoCommands.Analyze(context, parse.GetValue(input)!, parse.GetValue(plane), parse.GetValue(bit))));
        return command;
    }

    private static Option<FileInfo> InputOption(string description)
    {
        var option = new Option<FileInfo>("--input", "-i") { Description = description, Required = true };
        option.AcceptExistingOnly();
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

    /// <summary>Turns expected failures into a one-line message and an exit code instead of a stack trace.</summary>
    private static int Run(CliContext context, Func<int> action)
    {
        try
        {
            return action();
        }
        catch (CliException ex)
        {
            context.Error.WriteLine($"error: {ex.Message}");
            return ex.ExitCode;
        }
        catch (StegoException ex)
        {
            context.Error.WriteLine($"error: {ex.Message}");
            return ex.Error switch
            {
                StegoError.NotFoundOrWrongPassword => ExitCodes.NotFoundOrWrongPassword,
                StegoError.InsufficientCapacity => ExitCodes.InsufficientCapacity,
                StegoError.Corrupted => ExitCodes.Corrupted,
                _ => ExitCodes.Error,
            };
        }
        catch (Exception ex) when (ex is InvalidImageException or IOException or UnauthorizedAccessException)
        {
            context.Error.WriteLine($"error: {ex.Message}");
            return ExitCodes.Error;
        }
    }
}
