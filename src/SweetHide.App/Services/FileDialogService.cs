using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;

using SweetHide.Presentation.Services;

namespace SweetHide.App.Services;

/// <summary>Windows App SDK pickers: they take the window id and return plain paths, which suits an unpackaged app.</summary>
internal sealed class FileDialogService(Window window) : IFileDialogService
{
    internal static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".heic", ".heif", ".avif", ".tif", ".tiff", ".jxr"];

    public async Task<string?> PickImageAsync()
    {
        var picker = new FileOpenPicker(window.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.Thumbnail,
        };
        foreach (var extension in ImageExtensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        var result = await picker.PickSingleFileAsync();
        return result?.Path;
    }

    public async Task<IReadOnlyList<string>> PickImagesAsync()
    {
        var picker = new FileOpenPicker(window.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.Thumbnail,
        };
        foreach (var extension in ImageExtensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        var results = await picker.PickMultipleFilesAsync();
        return results is null ? [] : [.. results.Select(result => result.Path)];
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var picker = new FileOpenPicker(window.AppWindow.Id) { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        var results = await picker.PickMultipleFilesAsync();
        return results is null ? [] : [.. results.Select(result => result.Path)];
    }

    public async Task<string?> PickFileAsync(IReadOnlyList<string> extensions)
    {
        var picker = new FileOpenPicker(window.AppWindow.Id) { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        if (extensions.Count == 0)
        {
            picker.FileTypeFilter.Add("*");
        }

        foreach (var extension in extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        var result = await picker.PickSingleFileAsync();
        return result?.Path;
    }

    public async Task<string?> PickSavePngAsync(string suggestedFileName)
    {
        var picker = new FileSavePicker(window.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedFileName),
            DefaultFileExtension = ".png",
        };
        picker.FileTypeChoices.Add("PNG", [".png"]);
        picker.FileTypeChoices.Add("WebP (lossless)", [".webp"]);
        picker.FileTypeChoices.Add("TIFF", [".tiff", ".tif"]);
        picker.FileTypeChoices.Add("BMP", [".bmp"]);
        var result = await picker.PickSaveFileAsync();
        return result?.Path;
    }

    public async Task<string?> PickSaveFileAsync(string suggestedFileName)
    {
        var extension = Path.GetExtension(suggestedFileName);
        var picker = new FileSavePicker(window.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedFileName),
            DefaultFileExtension = extension,
        };
        picker.FileTypeChoices.Add(extension.TrimStart('.').ToUpperInvariant(), [extension]);
        var result = await picker.PickSaveFileAsync();
        return result?.Path;
    }

    public async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker(window.AppWindow.Id) { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        var result = await picker.PickSingleFolderAsync();
        return result?.Path;
    }
}
