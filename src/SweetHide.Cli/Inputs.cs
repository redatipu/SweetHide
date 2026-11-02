using SweetHide.Core;
using SweetHide.Core.Imaging;

namespace SweetHide.Cli;

/// <summary>Reads files or standard input ("-") with the same size limits everywhere.</summary>
internal static class Inputs
{
    public const string StandardStream = "-";
    public const long MaxFileBytes = 256L * 1024 * 1024;

    public static bool IsStandardStream(string path) => path == StandardStream;

    /// <summary>Fails early if more than one thing wants standard input.</summary>
    public static void RequireSingleStdinUse(params bool[] uses)
    {
        if (uses.Count(use => use) > 1)
        {
            throw new CliException("Only one input can come from standard input (\"-\" or --password-stdin).");
        }
    }

    public static byte[] ReadBytes(CliContext context, string path)
    {
        if (IsStandardStream(path))
        {
            using var stdin = context.OpenStandardInput();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = stdin.Read(chunk)) > 0)
            {
                if (buffer.Length + read > MaxFileBytes)
                {
                    throw new CliException($"Standard input is larger than {ByteSize.Format(MaxFileBytes)}.");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }

        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new CliException($"File not found: {file.FullName}");
        }

        return file.Length > MaxFileBytes
            ? throw new CliException($"{file.Name} is {ByteSize.Format(file.Length)}; the limit is {ByteSize.Format(MaxFileBytes)}.")
            : File.ReadAllBytes(file.FullName);
    }

    public static PixelBuffer ReadImage(CliContext context, string path, bool applyOrientation)
    {
        using var stream = new MemoryStream(ReadBytes(context, path), writable: false);
        return context.Codec.Decode(stream, applyOrientation);
    }

    public static string DisplayName(string path) => IsStandardStream(path) ? "stdin" : path;

    public static string Stem(string path) => IsStandardStream(path) ? "stdin" : Path.GetFileNameWithoutExtension(path);
}
