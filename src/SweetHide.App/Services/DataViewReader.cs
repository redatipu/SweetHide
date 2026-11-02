using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

using SweetHide.Presentation;
using SweetHide.Presentation.Services;

namespace SweetHide.App.Services;

/// <summary>Reads images and files out of a clipboard or drag-and-drop data package.</summary>
internal static class DataViewReader
{
    public static bool HasImageOrFiles(DataPackageView view) =>
        view.Contains(StandardDataFormats.StorageItems) || view.Contains(StandardDataFormats.Bitmap);

    public static async Task<ImageInput?> ReadImageAsync(DataPackageView view)
    {
        if (view.Contains(StandardDataFormats.StorageItems))
        {
            var items = await view.GetStorageItemsAsync();
            var file = items.OfType<StorageFile>().FirstOrDefault(IsImage);
            if (file is not null)
            {
                return new ImageInput(file.Name, await ReadAllBytesAsync(file));
            }
        }

        if (view.Contains(StandardDataFormats.Bitmap))
        {
            var reference = await view.GetBitmapAsync();
            using var stream = await reference.OpenReadAsync();
            using var input = stream.AsStreamForRead();
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer);
            return new ImageInput(Strings.ClipboardImageName, buffer.ToArray());
        }

        return null;
    }

    /// <summary>Paths of the image files being dropped (files without a path on disk are skipped).</summary>
    public static async Task<IReadOnlyList<string>> ReadImagePathsAsync(DataPackageView view)
    {
        if (!view.Contains(StandardDataFormats.StorageItems))
        {
            return [];
        }

        var items = await view.GetStorageItemsAsync();
        return [.. items.OfType<StorageFile>().Where(IsImage).Select(file => file.Path).Where(path => !string.IsNullOrEmpty(path))];
    }

    public static async Task<IReadOnlyList<string>> ReadFilePathsAsync(DataPackageView view)
    {
        if (!view.Contains(StandardDataFormats.StorageItems))
        {
            return [];
        }

        var items = await view.GetStorageItemsAsync();
        return [.. items.OfType<StorageFile>().Select(file => file.Path).Where(path => !string.IsNullOrEmpty(path))];
    }

    private static bool IsImage(StorageFile file) =>
        FileDialogService.ImageExtensions.Contains(file.FileType, StringComparer.OrdinalIgnoreCase);

    private static async Task<byte[]> ReadAllBytesAsync(StorageFile file)
    {
        if (!string.IsNullOrEmpty(file.Path))
        {
            return await File.ReadAllBytesAsync(file.Path);
        }

        using var stream = await file.OpenStreamForReadAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }
}
