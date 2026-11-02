using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using SweetHide.Core;
using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;
using SweetHide.Presentation.Imaging;
using SweetHide.Presentation.Services;

namespace SweetHide.Presentation.ViewModels;

/// <summary>One revealed message or file.</summary>
public sealed class RevealedItemViewModel
{
    internal RevealedItemViewModel(PayloadEntry entry, int messageNumber)
    {
        IsText = entry.Kind == PayloadEntryKind.Text;
        Content = entry.Content;
        Text = IsText ? entry.GetText() : string.Empty;
        FileName = IsText ? string.Empty : SafeFileName.Sanitize(entry.Name);
        Title = IsText ? Strings.Format(Strings.MessageItemFormat, messageNumber) : FileName;
        SizeText = ByteSize.Format(entry.Length);
    }

    public bool IsText { get; }

    public bool IsFile => !IsText;

    public string Title { get; }

    /// <summary>Sanitized file name (empty for messages).</summary>
    public string FileName { get; }

    public string Text { get; }

    public string SizeText { get; }

    public ReadOnlyMemory<byte> Content { get; }
}

/// <summary>The "Reveal" page: stego image + password → messages and files.</summary>
public sealed partial class RevealViewModel : PageViewModel
{
    private readonly StegoEngine _engine;
    private readonly ImageLoader _loader;
    private readonly IFileDialogService _dialogs;
    private readonly IClipboardService _clipboard;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage), nameof(ImageInfo))]
    [NotifyCanExecuteChangedFor(nameof(RevealCommand))]
    private LoadedImage? _image;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RevealCommand))]
    private string _password = string.Empty;

    public RevealViewModel(StegoEngine engine, ImageLoader loader, IFileDialogService dialogs, IClipboardService clipboard, IShellService shell)
        : base(shell)
    {
        _engine = engine;
        _loader = loader;
        _dialogs = dialogs;
        _clipboard = clipboard;
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(HasFiles));
            SaveAllFilesCommand.NotifyCanExecuteChanged();
        };
    }

    public ObservableCollection<RevealedItemViewModel> Items { get; } = [];

    public bool HasImage => Image is not null;

    public string ImageInfo => Image is null ? string.Empty : Strings.Format(Strings.ImageInfoFormat, Image.Width, Image.Height, Image.Name);

    public bool HasResults => Items.Count > 0;

    public bool HasFiles => Items.Any(item => item.IsFile);

    public async Task LoadImageFromPathAsync(string path) => await LoadAsync(() => _loader.LoadFileAsync(path, ImagePurpose.Inspect));

    public async Task LoadImageFromBytesAsync(string name, byte[] bytes) => await LoadAsync(() => _loader.LoadBytesAsync(name, bytes, ImagePurpose.Inspect));

    protected override void OnBusyChanged()
    {
        RevealCommand.NotifyCanExecuteChanged();
        SaveAllFilesCommand.NotifyCanExecuteChanged();
    }

    private bool CanReveal() => Image is not null && Password.Length > 0 && !IsBusy;

    private bool CanSaveAll() => HasFiles && !IsBusy;

    private async Task LoadAsync(Func<Task<LoadedImage>> load)
    {
        IsBusy = true;
        ClearStatus();
        try
        {
            Image = await load();
            Items.Clear();
        }
        catch (Exception ex) when (ex is InvalidImageException or IOException or UnauthorizedAccessException)
        {
            ShowStatus(StatusSeverity.Error, Strings.ErrorTitle, Strings.Format(Strings.ImageLoadFailed, ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task BrowseImageAsync()
    {
        if (await _dialogs.PickImageAsync() is { } path)
        {
            await LoadImageFromPathAsync(path);
        }
    }

    [RelayCommand]
    private async Task PasteImageAsync()
    {
        var image = await _clipboard.GetImageAsync();
        if (image is null)
        {
            ShowStatus(StatusSeverity.Warning, Strings.PasteImage, Strings.ClipboardNoImage);
            return;
        }

        await LoadImageFromBytesAsync(image.Name, image.Bytes);
    }

    [RelayCommand]
    private Task LoadDroppedImageAsync(ImageInput? input) =>
        input is null ? Task.CompletedTask : LoadImageFromBytesAsync(input.Name, input.Bytes);

    [RelayCommand(CanExecute = nameof(CanReveal))]
    private async Task RevealAsync()
    {
        var pixels = Image!.Pixels;
        var password = Password;
        IsBusy = true;
        ClearStatus();
        Items.Clear();
        try
        {
            var payload = await Task.Run(() => _engine.Extract(pixels, password));
            var messageNumber = 0;
            foreach (var entry in payload.Entries)
            {
                Items.Add(new RevealedItemViewModel(entry, entry.Kind == PayloadEntryKind.Text ? ++messageNumber : 0));
            }

            Password = string.Empty;
            ShowStatus(StatusSeverity.Success, Strings.Format(Strings.RevealSuccessTitle, Items.Count), Strings.FileSafetyNote);
        }
        catch (StegoException ex) when (ex.Error == StegoError.Corrupted)
        {
            ShowStatus(StatusSeverity.Error, Strings.ErrorTitle, Strings.RevealCorrupted);
        }
        catch (StegoException)
        {
            ShowStatus(StatusSeverity.Warning, Strings.RevealNotFoundTitle, Strings.RevealNotFound);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CopyText(RevealedItemViewModel? item)
    {
        if (item is { IsText: true })
        {
            _clipboard.SetText(item.Text);
            ShowStatus(StatusSeverity.Informational, Strings.CopyText, Strings.Copied);
        }
    }

    [RelayCommand]
    private async Task SaveFileAsync(RevealedItemViewModel? item)
    {
        if (item is not { IsFile: true })
        {
            return;
        }

        try
        {
            string? path;
            if (Path.HasExtension(item.FileName) && await _dialogs.PickSaveFileAsync(item.FileName) is { } picked)
            {
                path = picked;
                await File.WriteAllBytesAsync(path, item.Content.ToArray());
            }
            else if (!Path.HasExtension(item.FileName) && await _dialogs.PickFolderAsync() is { } folder)
            {
                // A save dialog needs a file type; for names without one, pick a folder instead.
                path = SafeFileWriter.Write(folder, item.FileName, item.Content.Span);
            }
            else
            {
                return;
            }

            ShowStatus(StatusSeverity.Success, Strings.HideSuccessTitle, Strings.Format(Strings.SavedFileMessage, Path.GetFileName(path)), path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowStatus(StatusSeverity.Error, Strings.ErrorTitle, ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveAll))]
    private async Task SaveAllFilesAsync()
    {
        if (await _dialogs.PickFolderAsync() is not { } folder)
        {
            return;
        }

        try
        {
            var count = 0;
            foreach (var item in Items.Where(item => item.IsFile))
            {
                SafeFileWriter.Write(folder, item.FileName, item.Content.Span);
                count++;
            }

            ShowStatus(StatusSeverity.Success, Strings.HideSuccessTitle, Strings.Format(Strings.SavedFilesMessage, count, folder), folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowStatus(StatusSeverity.Error, Strings.ErrorTitle, ex.Message);
        }
    }
}
