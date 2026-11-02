using SweetHide.Core;
using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;

namespace SweetHide.Cli;

internal sealed record RevealOptions(FileInfo Input, DirectoryInfo OutputDirectory, bool ListOnly, bool Force, bool PasswordFromStdin);

internal sealed class RevealCommand(CliContext context)
{
    public int Execute(RevealOptions options)
    {
        PixelBuffer image;
        using (var stream = options.Input.OpenRead())
        {
            image = context.Codec.Decode(stream, applyOrientation: false);
        }

        var password = context.Passwords.GetPassword(confirm: false, options.PasswordFromStdin);
        var payload = new StegoEngine(context.Kdf).Extract(image, password);

        var index = 0;
        foreach (var entry in payload.Entries)
        {
            index++;
            if (entry.Kind == PayloadEntryKind.Text)
            {
                if (options.ListOnly)
                {
                    context.Out.WriteLine($"[{index}] message, {Format.Bytes(entry.Length)}");
                }
                else
                {
                    context.Out.WriteLine(entry.GetText());
                }

                continue;
            }

            var safeName = SafeFileName.Sanitize(entry.Name);
            if (options.ListOnly)
            {
                context.Out.WriteLine($"[{index}] file \"{safeName}\", {Format.Bytes(entry.Length)}");
                continue;
            }

            var path = SafeFileWriter.Write(options.OutputDirectory.FullName, entry.Name, entry.Content.Span, options.Force);
            context.Error.WriteLine($"Saved {path} ({Format.Bytes(entry.Length)})");
        }

        return ExitCodes.Success;
    }
}
