using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

using SweetHide.Presentation.Services;

namespace SweetHide.App.Services;

internal sealed class ClipboardService : IClipboardService
{
    public Task<ImageInput?> GetImageAsync() => DataViewReader.ReadImageAsync(Clipboard.GetContent());

    public void SetText(string text)
    {
        var package = new DataPackage();
        package.SetText(ToWindowsNewlines(text));
        Clipboard.SetContent(package);
    }

    public async Task CopyFilesAsync(IReadOnlyList<string> paths)
    {
        var files = new List<IStorageItem>();
        foreach (var path in paths)
        {
            files.Add(await StorageFile.GetFileFromPathAsync(path));
        }

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetStorageItems(files);
        Clipboard.SetContent(package);
    }

    public async void ClearIfText(string text)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text) && await content.GetTextAsync() == ToWindowsNewlines(text))
            {
                Clipboard.Clear();
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard busy (another app holds it); leave it.
        }
    }

    private static string ToWindowsNewlines(string text) => text.Replace("\n", "\r\n", StringComparison.Ordinal);
}
