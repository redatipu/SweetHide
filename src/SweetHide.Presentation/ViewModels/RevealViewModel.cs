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
    private const int MaxPreviewBytes = 64 * 1024;
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];
    private static readonly string[] TextExtensions = [".txt", ".md", ".csv", ".json", ".log", ".xml", ".yaml", ".yml", ".ini", ".conf", ".cfg", ".srt"];

    internal RevealedItemViewModel(PayloadEntry entry, int messageNumber)
    {
        IsText = entry.Kind == PayloadEntryKind.Text;
        Content = entry.Content;
        Text = IsText ? entry.GetText() : string.Empty;
        FileName = IsText ? string.Empty : SafeFileName.Sanitize(entry.Name);
        Title = IsText ? Strings.Format(Strings.MessageItemFormat, messageNumber) : FileName;
        SizeText = ByteSize.Format(entry.Length);

        var extension = Path.GetExtension(FileName);
        IsImagePreview = !IsText && ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        PreviewText = !IsText && TextExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ? TextPreview(entry.Content.Span) : string.Empty;
    }

    /// <summary>The file is an image the UI may preview (decoded in-app, never opened externally).</summary>
    public bool IsImagePreview { get; }

    /// <summary>The start of a text file, or empty when it isn't valid UTF-8 text.</summary>
    public string PreviewText { get; }

    public bool HasTextPreview => PreviewText.Length > 0;

    /// <summary>Bytes for the in-app image preview (empty unless <see cref="IsImagePreview"/>).</summary>
    public byte[] PreviewImageBytes => IsImagePreview ? Content.ToArray() : [];

    public bool IsText { get; }

    public bool IsFile => !IsText;

    public string Title { get; }

    /// <summary>Sanitized file name (empty for messages).</summary>
    public string FileName { get; }

    public string Text { get; }

    public string SizeText { get; }

    public ReadOnlyMemory<byte> Content { get; }

    private static string TextPreview(ReadOnlySpan<byte> content)
    {
        var slice = content.Length > MaxPreviewBytes ? content[..MaxPreviewBytes] : content;
        try
        {
            var text = new System.Text.UTF8Encoding(false, true).GetString(slice);
            return content.Length > MaxPreviewBytes ? text + "\n…" : text;
        }
        catch (System.Text.DecoderFallbackException)
        {
            // Cut mid-character, or not text at all: no preview.
            return string.Empty;
        }
    }
}

/// <summary>The "Reveal" page: stego image + password → messages and files.</summary>
public sealed partial class RevealViewModel : PageViewModel
{
    private readonly StegoEngine _engine;
    private readonly ImageLoader _loader;
    private readonly IFileDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly AutoRevealer? _autoRevealer;
    private string? _lastPassword;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage), nameof(ImageInfo))]
    [NotifyCanExecuteChangedFor(nameof(RevealCommand), nameof(TryVaultCommand))]
    private LoadedImage? _image;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RememberPasswordCommand))]
    private string _rememberName = string.Empty;

    [ObservableProperty]
    private bool _canRemember;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RevealCommand))]
    private string _password = string.Empty;

    public RevealViewModel(
        StegoEngine engine,
        ImageLoader loader,
        IFileDialogService dialogs,
        IClipboardService clipboard,
        IShellService shell,
        AutoRevealer? autoRevealer = null,
        TimeSpan? clipboardClearDelay = null)
        : base(shell)
    {
        _engine = engine;
        _loader = loader;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _autoRevealer = autoRevealer;
        ClipboardClearDelay = clipboardClearDelay ?? TimeSpan.FromSeconds(30);
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(HasFiles));
            SaveAllFilesCommand.NotifyCanExecuteChanged();
        };
    }

    public ObservableCollection<RevealedItemViewModel> Items { get; } = [];

    public TimeSpan ClipboardClearDelay { get; }

    /// <summary>The pending "clear the clipboard" task, awaited by tests.</summary>
    public Task ClipboardClearTask { get; private set; } = Task.CompletedTask;

    public bool HasVault => _autoRevealer is not null && _autoRevealer.Vault.Entries.Count > 0;

    public bool HasImage => Image is not null;

    public string ImageInfo => Image is null ? string.Empty : Strings.Format(Strings.ImageInfoFormat, Image.Width, Image.Height, Image.Name);

    public bool HasResults => Items.Count > 0;

    public bool HasFiles => Items.Any(item => item.IsFile);

    public async Task LoadImageFromPathAsync(string path) => await LoadAsync(() => _loader.LoadFileAsync(path, ImagePurpose.Inspect));

    public async Task LoadImageFromBytesAsync(string name, byte[] bytes) => await LoadAsync(() => _loader.LoadBytesAsync(name, bytes, ImagePurpose.Inspect));

    /// <summary>Shows what the watched folder (or anything else) already revealed with a vault password.</summary>
    public void ShowAutoRevealed(AutoRevealResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Image = result.Image;
        ShowPayload(result.Payload, Strings.Format(Strings.RevealedWithVault, result.VaultName));
    }

    /// <summary>Loads the built-in sample for the first-run tour; its password is <see cref="SampleFactory.Password"/>.</summary>
    public async Task LoadSampleAsync(IImageCodec codec)
    {
        var png = await Task.Run(() => SampleFactory.CreateSamplePng(_engine, codec));
        await LoadImageFromBytesAsync(SampleFactory.FileName, png);
        ShowStatus(StatusSeverity.Informational, Strings.SampleTitle, Strings.Format(Strings.SampleHint, SampleFactory.Password));
    }

    protected override void OnBusyChanged()
    {
        RevealCommand.NotifyCanExecuteChanged();
        SaveAllFilesCommand.NotifyCanExecuteChanged();
        TryVaultCommand.NotifyCanExecuteChanged();
    }

    private void ShowPayload(Payload payload, string detail)
    {
        Items.Clear();
        var messageNumber = 0;
        foreach (var entry in payload.Entries)
        {
            Items.Add(new RevealedItemViewModel(entry, entry.Kind == PayloadEntryKind.Text ? ++messageNumber : 0));
        }

        ShowStatus(StatusSeverity.Success, Strings.Format(Strings.RevealSuccessTitle, Items.Count), detail);
    }

    private bool CanTryVault() => Image is not null && !IsBusy && HasVault;

    [RelayCommand(CanExecute = nameof(CanTryVault))]
    private async Task TryVaultAsync()
    {
        IsBusy = true;
        ClearStatus();
        Items.Clear();
        try
        {
            var progress = new Progress<(int Tried, int Total)>(p => StatusMessage = Strings.Format(Strings.TryingVault, p.Tried, p.Total));
            var result = await _autoRevealer!.TryImageAsync(Image!, progress);
            if (result is null)
            {
                ShowStatus(StatusSeverity.Warning, Strings.RevealNotFoundTitle, Strings.VaultNoMatch);
            }
            else
            {
                ShowPayload(result.Payload, Strings.Format(Strings.RevealedWithVault, result.VaultName));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRememberPassword() => CanRemember && _autoRevealer is not null && RememberName.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanRememberPassword))]
    private void RememberPassword()
    {
        _autoRevealer!.Vault.Save(RememberName.Trim(), _lastPassword!);
        _lastPassword = null;
        CanRemember = false;
        ShowStatus(StatusSeverity.Success, Strings.VaultLabel, Strings.Format(Strings.VaultSaved, RememberName.Trim()));
        RememberName = string.Empty;
        OnPropertyChanged(nameof(HasVault));
    }

    partial void OnCanRememberChanged(bool value) => RememberPasswordCommand.NotifyCanExecuteChanged();

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
            ShowPayload(payload, Strings.FileSafetyNote);
            Password = string.Empty;
            _lastPassword = password;
            CanRemember = _autoRevealer is not null && !_autoRevealer.Vault.Entries.Any(e => e.Password == password);
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
            ShowStatus(StatusSeverity.Informational, Strings.CopyText, Strings.Format(Strings.CopiedWillClear, (int)ClipboardClearDelay.TotalSeconds));
            ClipboardClearTask = ClearClipboardLaterAsync(item.Text);
        }
    }

    private async Task ClearClipboardLaterAsync(string text)
    {
        await Task.Delay(ClipboardClearDelay);
        _clipboard.ClearIfText(text);
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
