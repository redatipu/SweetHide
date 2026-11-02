using Magi.Core;
using Magi.Core.Imaging;
using Magi.Core.Payloads;

namespace Magi.Cli;

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

            var path = WriteFile(options.OutputDirectory, safeName, entry.Content.Span, options.Force);
            context.Error.WriteLine($"Saved {path} ({Format.Bytes(entry.Length)})");
        }

        return ExitCodes.Success;
    }

    /// <summary>Writes inside <paramref name="directory"/> only, never following the stored name anywhere else.</summary>
    internal static string WriteFile(DirectoryInfo directory, string safeName, ReadOnlySpan<byte> content, bool overwrite)
    {
        directory.Create();
        var root = Path.GetFullPath(directory.FullName);

        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var name = attempt == 0
                ? safeName
                : $"{Path.GetFileNameWithoutExtension(safeName)} ({attempt}){Path.GetExtension(safeName)}";
            var path = Path.GetFullPath(Path.Combine(root, name));
            if (!string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(root), StringComparison.Ordinal))
            {
                throw new CliException($"Refusing to write outside the output folder: {name}");
            }

            try
            {
                using var stream = new FileStream(path, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write);
                stream.Write(content);
                return path;
            }
            catch (IOException) when (!overwrite && File.Exists(path))
            {
                // Name taken: try "name (1).ext", "name (2).ext", ...
            }
        }

        throw new CliException($"Could not find a free file name for {safeName}.");
    }
}
