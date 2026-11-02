using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using SweetHide.Core;
using SweetHide.Core.Crypto;
using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;
using SweetHide.Core.Robust;
using SweetHide.Presentation.Imaging;
using SweetHide.Presentation.Services;

namespace SweetHide.Presentation.ViewModels;

/// <summary>One share of a split, collected from an image that was revealed earlier.</summary>
public sealed class CollectedShare
{
    internal CollectedShare(ShamirShare share, string imageName, HiddenDataInfo info)
    {
        Share = share;
        ImageName = imageName;
        Info = info;
    }

    public string ImageName { get; }

    public int Threshold => Share.Threshold;

    public int ShareCount => Share.ShareCount;

    public string Text => Strings.Format(Strings.ShareItemFormat, Share.Index, Share.ShareCount, ImageName);

    internal ShamirShare Share { get; }

    internal HiddenDataInfo Info { get; }
}

/// <summary>
/// The "Reveal" page: one or more images plus a password, key file or my key → messages and files. Falls back to
/// robust mode for photos that went through LINE or Telegram, and collects the shares of split images until enough
/// are there to rebuild the secret.
/// </summary>
public sealed partial class RevealViewModel : PageViewModel
{
    private readonly StegoEngine _engine;
    private readonly ImageLoader _loader;
    private readonly IFileDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly AutoRevealer? _autoRevealer;
    private readonly IKeyring? _keyring;
    private string? _lastPassword;
    private ChosenKeyFile? _keyFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage), nameof(ImageInfo))]
    [NotifyCanExecuteChangedFor(nameof(RevealCommand), nameof(TryVaultCommand), nameof(RevealWithMyKeyCommand), nameof(ScanWithFinderCommand))]
    private LoadedImage? _image;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RememberPasswordCommand))]
    private string _rememberName = string.Empty;

    [ObservableProperty]
    private bool _canRemember;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RevealCommand))]
    private string _password = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKeyFile), nameof(KeyFileText))]
    private string? _keyFileName;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanWithFinderCommand))]
    private bool _canScanWithFinder;

    public RevealViewModel(
        StegoEngine engine,
        ImageLoader loader,
        IFileDialogService dialogs,
        IClipboardService clipboard,
        IShellService shell,
        AutoRevealer? autoRevealer = null,
        TimeSpan? clipboardClearDelay = null,
        IKeyring? keyring = null)
        : base(shell)
    {
        _engine = engine;
        _loader = loader;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _autoRevealer = autoRevealer;
        _keyring = keyring;
        ClipboardClearDelay = clipboardClearDelay ?? TimeSpan.FromSeconds(30);
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(HasFiles));
            SaveAllFilesCommand.NotifyCanExecuteChanged();
        };
        AdditionalImages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAdditionalImages));
        Shares.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasShares));
            OnPropertyChanged(nameof(SharesText));
        };
        Details.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDetails));
        if (keyring is not null)
        {
            keyring.Changed += (_, _) =>
            {
                OnPropertyChanged(nameof(HasMyKey));
                RevealWithMyKeyCommand.NotifyCanExecuteChanged();
            };
        }
    }

    /// <summary>Raised by "Scan with Stego Finder" with the original bytes of the image, so the app can open the Scan page.</summary>
    public event EventHandler<ImageInput>? ScanRequested;

    public ObservableCollection<RevealedItemViewModel> Items { get; } = [];

    /// <summary>More images revealed together with <see cref="Image"/> (the other images of a split).</summary>
    public ObservableCollection<LoadedImage> AdditionalImages { get; } = [];

    /// <summary>Shares of a split collected so far; kept across images until the secret is rebuilt.</summary>
    public ObservableCollection<CollectedShare> Shares { get; } = [];

    /// <summary>How the revealed data was hidden.</summary>
    public ObservableCollection<DetailRow> Details { get; } = [];

    public TimeSpan ClipboardClearDelay { get; }

    /// <summary>The pending "clear the clipboard" task, awaited by tests.</summary>
    public Task ClipboardClearTask { get; private set; } = Task.CompletedTask;

    public bool HasVault => _autoRevealer is not null && _autoRevealer.Vault.Entries.Count > 0;

    public bool HasMyKey => _keyring?.HasIdentity == true;

    public bool HasImage => Image is not null;

    public string ImageInfo => Image is null ? string.Empty : Strings.Format(Strings.ImageInfoFormat, Image.Width, Image.Height, Image.Name);

    public bool HasAdditionalImages => AdditionalImages.Count > 0;

    public bool HasResults => Items.Count > 0;

    public bool HasFiles => Items.Any(item => item.IsFile);

    public bool HasDetails => Details.Count > 0;

    public bool HasShares => Shares.Count > 0;

    public string SharesText => Shares.Count == 0
        ? string.Empty
        : Strings.Format(Strings.ShareProgress, Shares.Count, Shares[0].Threshold, Shares[0].ShareCount);

    public bool HasKeyFile => KeyFileName is not null;

    public string KeyFileText => KeyFileName ?? Strings.KeyFileNone;

    public async Task LoadImageFromPathAsync(string path) => await LoadAsync(() => _loader.LoadFileAsync(path, ImagePurpose.Inspect));

    public async Task LoadImageFromBytesAsync(string name, byte[] bytes) => await LoadAsync(() => _loader.LoadBytesAsync(name, bytes, ImagePurpose.Inspect));

    /// <summary>Loads several images to reveal together; the first replaces the current image unless <paramref name="append"/>.</summary>
    public async Task LoadImagesFromPathsAsync(IReadOnlyList<string> paths, bool append = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        IsBusy = true;
        ClearStatus();
        try
        {
            var loaded = new List<LoadedImage>();
            foreach (var path in paths)
            {
                try
                {
                    loaded.Add(await _loader.LoadFileAsync(path, ImagePurpose.Inspect));
                }
                catch (Exception ex) when (ex is InvalidImageException or IOException or UnauthorizedAccessException)
                {
                    ShowStatus(StatusSeverity.Error, Strings.ErrorTitle, Strings.Format(Strings.ImageLoadFailed, ex.Message));
                }
            }

            if (loaded.Count == 0)
            {
                return;
            }

            if (!append || Image is null)
            {
                AdditionalImages.Clear();
                Image = loaded[0];
                loaded.RemoveAt(0);
            }

            loaded.ForEach(AdditionalImages.Add);
            ResetResults();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Shows what the watched folder (or anything else) already revealed with a vault password.</summary>
    public void ShowAutoRevealed(AutoRevealResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        AdditionalImages.Clear();
        Image = result.Image;
        ResetResults();
        ShowPayload(result.Payload.Entries, Strings.Format(Strings.RevealedWithVault, result.VaultName));
        if (result.Info is { } info)
        {
            ShowDetails(StegoText.InfoRows(info));
        }
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
        RevealWithMyKeyCommand.NotifyCanExecuteChanged();
        SaveAllFilesCommand.NotifyCanExecuteChanged();
        TryVaultCommand.NotifyCanExecuteChanged();
    }

    private static RevealOutcome Outcome(string imageName, RevealResult result) =>
        new(imageName, result.Info, result.Payload, result.Share, null, null);

    private void ResetResults()
    {
        Items.Clear();
        Details.Clear();
        CanScanWithFinder = false;
    }

    private void ShowPayload(IEnumerable<PayloadEntry> entries, string detail)
    {
        Items.Clear();
        var messageNumber = 0;
        foreach (var entry in entries)
        {
            Items.Add(new RevealedItemViewModel(entry, entry.Kind == PayloadEntryKind.Text ? ++messageNumber : 0));
        }

        ShowStatus(StatusSeverity.Success, Strings.Format(Strings.RevealSuccessTitle, Items.Count), detail);
    }

    private void ShowDetails(IEnumerable<DetailRow> rows)
    {
        Details.Clear();
        foreach (var row in rows)
        {
            Details.Add(row);
        }
    }

    private void ShowError(StegoException error)
    {
        var (severity, title, message) = StegoText.ForError(error);
        ShowStatus(severity, title, message);
    }

    private bool CanTryVault() => Image is not null && !IsBusy && HasVault;

    [RelayCommand(CanExecute = nameof(CanTryVault))]
    private async Task TryVaultAsync()
    {
        IsBusy = true;
        ClearStatus();
        ResetResults();
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
                ShowPayload(result.Payload.Entries, Strings.Format(Strings.RevealedWithVault, result.VaultName));
                if (result.Info is { } info)
                {
                    ShowDetails(StegoText.InfoRows(info));
                }
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

    private bool CanRevealWithMyKey() => Image is not null && !IsBusy && HasMyKey;

    private bool CanSaveAll() => HasFiles && !IsBusy;

    private bool CanScan() => CanScanWithFinder && Image is not null;

    private async Task LoadAsync(Func<Task<LoadedImage>> load)
    {
        IsBusy = true;
        ClearStatus();
        try
        {
            var image = await load();
            AdditionalImages.Clear();
            Image = image;
            ResetResults();
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
    private async Task AddImagesAsync()
    {
        var paths = await _dialogs.PickImagesAsync();
        if (paths.Count > 0)
        {
            await LoadImagesFromPathsAsync(paths, append: true);
        }
    }

    [RelayCommand]
    private void RemoveAdditionalImage(LoadedImage? image)
    {
        if (image is not null)
        {
            AdditionalImages.Remove(image);
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

    /// <summary>Several image files dropped at once: reveal them together (the images of a split).</summary>
    [RelayCommand]
    private Task LoadDroppedImagesAsync(IReadOnlyList<string>? paths) =>
        paths is { Count: > 0 } ? LoadImagesFromPathsAsync(paths) : Task.CompletedTask;

    [RelayCommand]
    private async Task ChooseKeyFileAsync()
    {
        if (await _dialogs.PickFileAsync([]) is not { } path)
        {
            return;
        }

        var (keyFile, error) = await KeyFiles.LoadAsync(path);
        if (keyFile is null)
        {
            ShowStatus(StatusSeverity.Error, Strings.ErrorTitle, error!);
            return;
        }

        _keyFile = keyFile;
        KeyFileName = keyFile.Name;
    }

    [RelayCommand]
    private void ClearKeyFile()
    {
        _keyFile = null;
        KeyFileName = null;
    }

    [RelayCommand]
    private void ClearShares()
    {
        Shares.Clear();
        ClearStatus();
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private void ScanWithFinder() => ScanRequested?.Invoke(this, new ImageInput(Image!.Name, Image.EncodedBytes.ToArray()));

    [RelayCommand(CanExecute = nameof(CanReveal))]
    private async Task RevealAsync()
    {
        var password = Password;
        var keyFile = _keyFile?.KeyFile;
        if (await RunRevealAsync(image => RevealWithPassword(image, password, keyFile), byMyKey: false))
        {
            Password = string.Empty;
            _lastPassword = password;

            // A password that also needs a key file could never open anything on its own, so don't offer to keep it.
            CanRemember = keyFile is null && _autoRevealer is not null && !_autoRevealer.Vault.Entries.Any(e => e.Password == password);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRevealWithMyKey))]
    private async Task RevealWithMyKeyAsync()
    {
        using var identity = _keyring!.OpenIdentity();
        if (identity is null)
        {
            ShowStatus(StatusSeverity.Error, Strings.ErrorTitle, Strings.MyKeyUnreadable);
            return;
        }

        await RunRevealAsync(image => RevealWithIdentity(image, identity), byMyKey: true);
    }

    /// <summary>Reveals every loaded image and shows what was found.</summary>
    /// <returns>True when a payload is shown.</returns>
    private async Task<bool> RunRevealAsync(Func<LoadedImage, RevealOutcome> reveal, bool byMyKey)
    {
        LoadedImage[] images = [Image!, .. AdditionalImages];
        IsBusy = true;
        ClearStatus();
        ResetResults();
        try
        {
            var outcomes = await Task.Run(() => images.Select(reveal).ToList());
            var found = outcomes.Where(o => o.Payload is not null).ToList();
            if (found.Count > 0)
            {
                ShowPayload(found.SelectMany(o => o.Payload!.Entries), Strings.FileSafetyNote);
                ShowDetails(found[0].Robust is { } robust ? StegoText.RobustRows(robust) : StegoText.InfoRows(found[0].Info!));
                return true;
            }

            var shares = outcomes.Where(o => o.Share is not null).ToList();
            if (shares.Count > 0)
            {
                shares.ForEach(AddShare);
                return await TryCombineSharesAsync();
            }

            ShowNothingFound(outcomes, byMyKey);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private RevealOutcome RevealWithPassword(LoadedImage image, string password, KeyFile? keyFile)
    {
        if (image.Indexed is { } palette)
        {
            // A 256-colour PNG is most likely a palette stego image; if not, it may still hold full-colour data.
            try
            {
                return Outcome(image.Name, _engine.Reveal(palette, password, keyFile));
            }
            catch (StegoException ex) when (ex.Error != StegoError.NotFoundOrWrongPassword)
            {
                return new RevealOutcome(image.Name, null, null, null, null, ex);
            }
            catch (Exception ex) when (ex is StegoException or ArgumentException)
            {
                // Nothing in the palette: carry on with the full-colour pixels.
            }
        }

        try
        {
            return Outcome(image.Name, _engine.Reveal(image.Pixels, password, keyFile));
        }
        catch (StegoException ex) when (ex.Error == StegoError.NotFoundOrWrongPassword)
        {
            // Not hidden the normal way with this password; it may be a photo that went through LINE or Telegram.
            try
            {
                var robust = _engine.RevealRobust(image.Pixels, password, keyFile);
                return robust.Status == RobustStatus.NotFound
                    ? new RevealOutcome(image.Name, null, null, null, null, ex)
                    : new RevealOutcome(image.Name, null, robust.Payload, null, robust, null);
            }
            catch (StegoException robustError)
            {
                return new RevealOutcome(image.Name, null, null, null, null, robustError);
            }
        }
        catch (StegoException ex)
        {
            return new RevealOutcome(image.Name, null, null, null, null, ex);
        }
        catch (ArgumentException)
        {
            // Not valid Unicode (a lone surrogate): no image could have been hidden with it.
            return new RevealOutcome(image.Name, null, null, null, null, new StegoException(StegoError.NotFoundOrWrongPassword, string.Empty));
        }
    }

    private RevealOutcome RevealWithIdentity(LoadedImage image, Identity identity)
    {
        try
        {
            return Outcome(image.Name, _engine.Reveal(image.Pixels, identity));
        }
        catch (StegoException ex)
        {
            return new RevealOutcome(image.Name, null, null, null, null, ex);
        }
    }

    private void AddShare(RevealOutcome outcome)
    {
        var share = outcome.Share!;
        if (Shares.Count > 0 && Shares[0].Share.SplitId != share.SplitId)
        {
            Shares.Clear(); // an image of another split: collect that one instead
        }

        if (Shares.Any(s => s.Share.Index == share.Index))
        {
            return;
        }

        var position = Shares.TakeWhile(s => s.Share.Index < share.Index).Count();
        Shares.Insert(position, new CollectedShare(share, outcome.ImageName, outcome.Info!));
    }

    private async Task<bool> TryCombineSharesAsync()
    {
        var first = Shares[0];
        if (Shares.Count < first.Threshold)
        {
            ShowStatus(StatusSeverity.Informational, Strings.ShareFoundTitle, SharesText);
            return false;
        }

        try
        {
            var shares = Shares.Select(s => s.Share).ToList();
            var payload = await Task.Run(() => StegoEngine.CombineShares(shares));
            var used = Shares.Count;
            Shares.Clear();
            ShowPayload(payload.Entries, Strings.Format(Strings.SharesCombined, used, first.ShareCount));
            ShowDetails(StegoText.InfoRows(first.Info, Strings.Format(Strings.ModeSplit, first.Threshold, first.ShareCount)));
            return true;
        }
        catch (StegoException ex)
        {
            ShowError(ex);
            return false;
        }
    }

    private void ShowNothingFound(IReadOnlyList<RevealOutcome> outcomes, bool byMyKey)
    {
        // The most useful explanation wins: damaged or too new beats "nothing here".
        if (outcomes.Select(o => o.Error).FirstOrDefault(e => e is { Error: not StegoError.NotFoundOrWrongPassword }) is { } error)
        {
            ShowError(error);
            return;
        }

        if (outcomes.Select(o => o.Robust).FirstOrDefault(r => r is { Status: RobustStatus.Damaged }) is { } damaged)
        {
            ShowStatus(
                StatusSeverity.Error,
                Strings.RobustDamagedTitle,
                Strings.Format(Strings.RobustDamaged, StegoText.ProfileName(damaged.Profile), StegoText.Percent(damaged.RawBitErrorRate)));
            return;
        }

        ShowStatus(StatusSeverity.Warning, Strings.RevealNotFoundTitle, byMyKey ? Strings.RevealNotForMyKey : Strings.RevealNotFound);
        CanScanWithFinder = true;
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

    /// <summary>What revealing one image gave: a payload, a share, a robust result, or an error.</summary>
    private sealed record RevealOutcome(
        string ImageName,
        HiddenDataInfo? Info,
        Payload? Payload,
        ShamirShare? Share,
        RobustRevealResult? Robust,
        StegoException? Error);
}
