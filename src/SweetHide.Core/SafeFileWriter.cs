namespace SweetHide.Core;

/// <summary>Writes revealed files using untrusted names without ever leaving the chosen folder.</summary>
public static class SafeFileWriter
{
    private const int MaxAttempts = 1000;

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="directory"/> under a sanitized version of
    /// <paramref name="untrustedName"/>. Unless <paramref name="overwrite"/> is set, existing files are kept and
    /// "name (1).ext", "name (2).ext", ... is used instead.
    /// </summary>
    /// <returns>The full path written.</returns>
    public static string Write(string directory, string untrustedName, ReadOnlySpan<byte> content, bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        var safeName = SafeFileName.Sanitize(untrustedName);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        Directory.CreateDirectory(root);

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var name = attempt == 0
                ? safeName
                : $"{Path.GetFileNameWithoutExtension(safeName)} ({attempt}){Path.GetExtension(safeName)}";
            var path = Path.GetFullPath(Path.Combine(root, name));
            if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.Ordinal))
            {
                throw new IOException($"Refusing to write outside the output folder: {name}");
            }

            try
            {
                using var stream = new FileStream(path, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write);
                stream.Write(content);
                return path;
            }
            catch (IOException) when (!overwrite && File.Exists(path))
            {
                // Name taken; try the next numbered one.
            }
        }

        throw new IOException($"Could not find a free file name for {safeName}.");
    }
}
