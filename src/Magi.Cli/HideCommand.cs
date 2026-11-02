using Magi.Core;
using Magi.Core.Imaging;
using Magi.Core.Payloads;

namespace Magi.Cli;

internal sealed record HideOptions(
    FileInfo Input,
    FileInfo Output,
    IReadOnlyList<string> Texts,
    IReadOnlyList<FileInfo> TextFiles,
    IReadOnlyList<FileInfo> Files,
    int Bits,
    bool Force,
    bool PasswordFromStdin);

internal sealed class HideCommand(CliContext context)
{
    private const long MaxFileBytes = 256L * 1024 * 1024;

    public int Execute(HideOptions options)
    {
        if (options.Texts.Count + options.TextFiles.Count + options.Files.Count == 0)
        {
            throw new CliException("Nothing to hide: give --text, --text-file and/or --file.");
        }

        if (!string.Equals(options.Output.Extension, ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException("Output must be a .png file. Lossy formats such as JPEG destroy the hidden bits.");
        }

        if (options.Output.Exists && !options.Force)
        {
            throw new CliException($"{options.Output.FullName} already exists. Use --force to overwrite.");
        }

        var payload = BuildPayload(options);

        PixelBuffer cover;
        using (var stream = options.Input.OpenRead())
        {
            cover = context.Codec.Decode(stream, applyOrientation: true);
        }

        // Check capacity before asking for the password, so the user isn't prompted for nothing.
        var capacity = StegoEngine.GetCapacity(cover, options.Bits);
        var containerSize = StegoEngine.GetContainerSize(payload);
        if (containerSize > capacity.ContainerBytes)
        {
            throw new CliException(CapacityAdvice(cover, containerSize, options.Bits), ExitCodes.InsufficientCapacity);
        }

        var password = context.Passwords.GetPassword(confirm: true, options.PasswordFromStdin);
        var stego = new StegoEngine(context.Kdf).Embed(cover, payload, password, options.Bits);

        WriteAtomically(options.Output, stego, options.Force);

        var diff = ImageMetrics.Compare(cover, stego);
        var usage = (double)containerSize / capacity.ContainerBytes;
        var texts = payload.Entries.Count(e => e.Kind == PayloadEntryKind.Text);
        var files = payload.Entries.Count - texts;
        context.Out.WriteLine($"Hidden {texts} message(s) and {files} file(s): {Format.Bytes(payload.ContentLength)} → {Format.Bytes(containerSize)} stored.");
        context.Out.WriteLine($"Used {Format.Percent(usage)} of {Format.Bytes(capacity.ContainerBytes)} capacity at {options.Bits} bit(s) per channel.");
        context.Out.WriteLine($"Changed {Format.Percent(diff.ChangedRatio)} of colour values (max ±{diff.MaxAbsoluteDelta}), PSNR {Format.Number(diff.Psnr, "0.0")} dB.");
        context.Out.WriteLine($"Wrote {options.Output.FullName}");

        if (usage > 0.5)
        {
            context.Error.WriteLine("warning: more than half the capacity is used; statistical detection gets easier. A larger or noisier photo helps.");
        }

        if (options.Bits >= 3)
        {
            context.Error.WriteLine("warning: 3-4 bits per channel can leave visible noise in smooth areas such as sky.");
        }

        context.Error.WriteLine("note: send the PNG as a file/document. Chat apps re-compress photos, which destroys hidden data.");
        return ExitCodes.Success;
    }

    private static Payload BuildPayload(HideOptions options)
    {
        var entries = new List<PayloadEntry>();
        entries.AddRange(options.Texts.Select(PayloadEntry.FromText));

        foreach (var textFile in options.TextFiles)
        {
            entries.Add(PayloadEntry.FromText(File.ReadAllText(RequireFile(textFile).FullName)));
        }

        foreach (var file in options.Files)
        {
            entries.Add(PayloadEntry.FromFile(file.Name, File.ReadAllBytes(RequireFile(file).FullName)));
        }

        return new Payload(entries);
    }

    private static FileInfo RequireFile(FileInfo file)
    {
        if (!file.Exists)
        {
            throw new CliException($"File not found: {file.FullName}");
        }

        return file.Length > MaxFileBytes
            ? throw new CliException($"{file.Name} is {Format.Bytes(file.Length)}; the limit is {Format.Bytes(MaxFileBytes)}.")
            : file;
    }

    private static string CapacityAdvice(PixelBuffer cover, long needed, int bits)
    {
        var options = StegoEngine.SupportedBitsPerChannel
            .Where(b => b > bits && StegoEngine.GetCapacity(cover, b).ContainerBytes >= needed)
            .ToList();
        var advice = options.Count > 0
            ? $" It would fit with --bits {options[0]}, or use a larger image."
            : " Use a larger image (more pixels) or hide less.";
        return $"Needs {Format.Bytes(needed)} but {cover.Width}x{cover.Height} holds {Format.Bytes(StegoEngine.GetCapacity(cover, bits).ContainerBytes)} at {bits} bit(s) per channel.{advice}";
    }

    private void WriteAtomically(FileInfo output, PixelBuffer image, bool overwrite)
    {
        var directory = output.Directory ?? throw new CliException("Invalid output path.");
        directory.Create();
        var temp = Path.Combine(directory.FullName, $".{output.Name}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                context.Codec.EncodePng(image, stream);
            }

            File.Move(temp, output.FullName, overwrite);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
