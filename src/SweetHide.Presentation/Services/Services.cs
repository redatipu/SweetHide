namespace SweetHide.Presentation.Services;

/// <summary>An image (or any file) received from the clipboard or a drag and drop.</summary>
public sealed record ImageInput(string Name, byte[] Bytes);

/// <summary>Native file dialogs. Every method returns null (or empty) when the user cancels.</summary>
public interface IFileDialogService
{
    Task<string?> PickImageAsync();

    Task<IReadOnlyList<string>> PickFilesAsync();

    Task<string?> PickSavePngAsync(string suggestedFileName);

    Task<string?> PickSaveFileAsync(string suggestedFileName);

    Task<string?> PickFolderAsync();
}

public interface IClipboardService
{
    /// <summary>An image on the clipboard (bitmap or a copied image file), or null.</summary>
    Task<ImageInput?> GetImageAsync();

    void SetText(string text);
}

public interface IShellService
{
    /// <summary>Opens Explorer with the file selected, or the folder itself.</summary>
    void ShowInFolder(string path);
}

public interface IThemeService
{
    void Apply(AppTheme theme);
}
