using System.Diagnostics;

using SweetHide.Presentation.Services;

namespace SweetHide.App.Services;

internal sealed class ShellService : IShellService
{
    public void ShowInFolder(string path)
    {
        // Explorer only; never launches the file itself (revealed files are untrusted).
        var arguments = Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"";
        using var process = Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = false });
    }

    public void OpenUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Only https links are opened.", nameof(uri));
        }

        using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
