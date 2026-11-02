using SweetHide.Core;
using SweetHide.Core.Payloads;

namespace SweetHide.Cli;

internal sealed record RevealOptions(IReadOnlyList<string> Inputs, DirectoryInfo OutputDirectory, bool ListOnly, bool Force, bool PasswordFromStdin);

internal sealed class RevealCommand(CliContext context)
{
    public int Execute(RevealOptions options)
    {
        Inputs.RequireSingleStdinUse(options.Inputs.Any(Inputs.IsStandardStream), options.PasswordFromStdin);
        if (options.Inputs.Count(Inputs.IsStandardStream) > 1)
        {
            throw new CliException("Only one input can come from standard input.");
        }

        var images = options.Inputs.Select(input => (Input: input, Pixels: Inputs.ReadImage(context, input, applyOrientation: false))).ToList();
        var password = context.Passwords.GetPassword(confirm: false, options.PasswordFromStdin);
        var engine = new StegoEngine(context.Kdf);
        var reports = new List<RevealReport>();
        var failures = new List<(string Input, StegoException Error)>();

        foreach (var (input, pixels) in images)
        {
            Payload payload;
            try
            {
                payload = engine.Extract(pixels, password);
            }
            catch (StegoException ex) when (images.Count > 1)
            {
                failures.Add((input, ex)); // keep going with the other images
                context.Error.WriteLine($"{Inputs.DisplayName(input)}: {ex.Message}");
                continue;
            }

            // Several images: one sub-folder each, so same-named files don't collide.
            var directory = images.Count > 1
                ? Path.Combine(options.OutputDirectory.FullName, SafeFileName.Sanitize(Inputs.Stem(input)))
                : options.OutputDirectory.FullName;
            reports.Add(Reveal(Inputs.DisplayName(input), payload, directory, options, images.Count > 1));
        }

        if (context.Json)
        {
            JsonOutput.Write<RevealReport>(context, reports);
        }

        if (failures.Count == 0)
        {
            return ExitCodes.Success;
        }

        // Batch: success if anything was revealed; otherwise report the first failure's code.
        return reports.Count > 0 ? ExitCodes.Success : CliApp.ExitCodeFor(failures[0].Error);
    }

    private RevealReport Reveal(string input, Payload payload, string directory, RevealOptions options, bool labelOutput)
    {
        var messages = new List<string>();
        var files = new List<RevealedFile>();
        var index = 0;
        if (labelOutput && !context.Json)
        {
            context.Out.WriteLine($"== {input}");
        }

        foreach (var entry in payload.Entries)
        {
            index++;
            if (entry.Kind == PayloadEntryKind.Text)
            {
                var text = entry.GetText();
                messages.Add(text);
                if (!context.Json)
                {
                    context.Out.WriteLine(options.ListOnly ? $"[{index}] message, {Format.Bytes(entry.Length)}" : text);
                }

                continue;
            }

            var safeName = SafeFileName.Sanitize(entry.Name);
            string? savedTo = null;
            if (options.ListOnly)
            {
                if (!context.Json)
                {
                    context.Out.WriteLine($"[{index}] file \"{safeName}\", {Format.Bytes(entry.Length)}");
                }
            }
            else
            {
                savedTo = SafeFileWriter.Write(directory, entry.Name, entry.Content.Span, options.Force);
                context.Error.WriteLine($"Saved {savedTo} ({Format.Bytes(entry.Length)})");
            }

            files.Add(new RevealedFile(safeName, entry.Length, savedTo));
        }

        return new RevealReport(input, messages, files);
    }
}
