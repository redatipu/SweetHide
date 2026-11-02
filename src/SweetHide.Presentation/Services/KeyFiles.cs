using SweetHide.Core.Crypto;

namespace SweetHide.Presentation.Services;

/// <summary>A key file the user picked: its name for the UI and the digest the engine needs.</summary>
/// <param name="Name">File name only (never shown with the full path).</param>
/// <param name="KeyFile">The hashed content.</param>
public sealed record ChosenKeyFile(string Name, KeyFile KeyFile);

internal static class KeyFiles
{
    /// <summary>Hashes the file off the UI thread.</summary>
    /// <returns>The key file, or a message saying why it can't be used.</returns>
    public static async Task<(ChosenKeyFile? KeyFile, string? Error)> LoadAsync(string path)
    {
        try
        {
            var keyFile = await Task.Run(() => KeyFile.FromFile(path));
            return (new ChosenKeyFile(Path.GetFileName(path), keyFile), null);
        }
        catch (ArgumentException)
        {
            return (null, Strings.KeyFileEmpty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, ex.Message);
        }
    }
}
