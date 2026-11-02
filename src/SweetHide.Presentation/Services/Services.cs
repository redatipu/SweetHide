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

    /// <summary>Puts files on the clipboard as files, so chat apps send them untouched instead of as photos.</summary>
    Task CopyFilesAsync(IReadOnlyList<string> paths);

    /// <summary>Clears the clipboard if it still holds <paramref name="text"/> (we don't wipe what the user copied since).</summary>
    void ClearIfText(string text);
}

/// <summary>"Hide with SweetHide" / "Reveal with SweetHide" in Explorer's right-click menu.</summary>
public interface IExplorerIntegration
{
    bool IsEnabled { get; }

    void SetEnabled(bool enabled);
}

public interface IShellService
{
    /// <summary>Opens Explorer with the file selected, or the folder itself.</summary>
    void ShowInFolder(string path);

    /// <summary>Opens a web page in the default browser (only ever our own release pages).</summary>
    void OpenUri(Uri uri);
}

public interface IThemeService
{
    void Apply(AppTheme theme);
}
