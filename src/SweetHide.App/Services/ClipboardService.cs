using Windows.ApplicationModel.DataTransfer;

using SweetHide.Presentation.Services;

namespace SweetHide.App.Services;

internal sealed class ClipboardService : IClipboardService
{
    public Task<ImageInput?> GetImageAsync() => DataViewReader.ReadImageAsync(Clipboard.GetContent());

    public void SetText(string text)
    {
        var package = new DataPackage();
        package.SetText(text.Replace("\n", "\r\n", StringComparison.Ordinal));
        Clipboard.SetContent(package);
    }
}
