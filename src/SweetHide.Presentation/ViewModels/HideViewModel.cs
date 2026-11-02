using System.Collections.ObjectModel;
using System.Text;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using SweetHide.Core;
using SweetHide.Core.Imaging;
using SweetHide.Core.Payloads;
using SweetHide.Presentation.Imaging;
using SweetHide.Presentation.Services;

namespace SweetHide.Presentation.ViewModels;

public enum CapacityState
{
    NoImage,
    Empty,
    Measuring,
    Ok,
    Warning,
    Over,
}

public sealed record BitsOption(int Bits, string Description);

public sealed partial class AttachmentViewModel(string name, byte[] content)
{
    public string Name { get; } = name;

    public ReadOnlyMemory<byte> Content { get; } = content;

    public string SizeText => ByteSize.Format(Content.Length);
}

/// <summary>The "Hide" page: cover image + message/files + password → stego PNG.</summary>
public sealed partial class HideViewModel : PageViewModel, IDisposable
{
    public const long MaxAttachmentBytes = 256L * 1024 * 1024;

    private readonly StegoEngine _engine;
    private readonly IImageCodec _codec;
    private readonly ImageLoader _loader;
    private readonly IFileDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly TimeSpan _measureDelay;
    private CancellationTokenSource? _measureCancellation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover), nameof(CoverInfo))]
    private LoadedImage? _cover;

    [ObservableProperty]
    private string _messageText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Strength), nameof(StrengthLevel), nameof(StrengthText), nameof(ShowStrength), nameof(ShowPasswordMismatch))]
    [NotifyCanExecuteChangedFor(nameof(HideCommand))]
    private string _password = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPasswordMismatch))]
    [NotifyCanExecuteChangedFor(nameof(HideCommand))]
    private string _confirmPassword = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BitsIndex))]
    private int _bitsPerChannel = StegoEngine.DefaultBitsPerChannel;

    [ObservableProperty]
    private long? _requiredBytes = 0;

    [ObservableProperty]
    private bool _isMeasuring;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGeneratedPassphrase))]
    [NotifyCanExecuteChangedFor(nameof(CopyGeneratedPassphraseCommand))]
    private string _generatedPassphrase = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSavedFile))]
    [NotifyCanExecuteChangedFor(nameof(CopySavedFileCommand))]
    private string? _lastSavedPath;

    [ObservableProperty]
    private bool _saveToVault;

    [ObservableProperty]
    private string _vaultName = string.Empty;

    private readonly IPasswordVault? _vault;

    public HideViewModel(
        StegoEngine engine,
        IImageCodec codec,
        ImageLoader loader,
        IFileDialogService dialogs,
        IClipboardService clipboard,
        IShellService shell,
        TimeSpan? measureDelay = null,
        TimeSpan? clipboardClearDelay = null,
        IPasswordVault? vault = null)
        : base(shell)
    {
        _vault = vault;
        ClipboardClearDelay = clipboardClearDelay ?? TimeSpan.FromSeconds(30);
        _engine = engine;
        _codec = codec;
        _loader = loader;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _measureDelay = measureDelay ?? TimeSpan.FromMilliseconds(300);
        BitsOptions = [.. StegoEngine.SupportedBitsPerChannel.Select(bits => new BitsOption(bits, Strings.BitsDescription(bits)))];
        Attachments.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAttachments));
            OnPropertyChanged(nameof(HasNoAttachments));
            OnContentChanged();
        };
    }

    public ObservableCollection<AttachmentViewModel> Attachments { get; } = [];

    public IReadOnlyList<BitsOption> BitsOptions { get; }

    public IReadOnlyList<string> BitsDescriptions => [.. BitsOptions.Select(option => option.Description)];

    /// <summary>The running (debounced) size measurement; awaited by tests.</summary>
    public Task MeasureTask { get; private set; } = Task.CompletedTask;

    /// <summary>How long copied secrets stay on the clipboard.</summary>
    public TimeSpan ClipboardClearDelay { get; }

    /// <summary>The pending "clear the clipboard" task, awaited by tests.</summary>
    public Task ClipboardClearTask { get; private set; } = Task.CompletedTask;

    public bool HasGeneratedPassphrase => GeneratedPassphrase.Length > 0;

    public bool HasVault => _vault is not null;

    /// <summary>Smallest bits per channel that fits the payload, if that differs from the current choice.</summary>
    public int? RecommendedBits
    {
        get
        {
            if (Cover is null || RequiredBytes is not { } required || !HasContent || IsMeasuring)
            {
                return null;
            }

            var fit = StegoEngine.SupportedBitsPerChannel.FirstOrDefault(bits => Cover.CapacityFor(bits) >= required);
            return fit != 0 && fit != BitsPerChannel ? fit : null;
        }
    }

    public bool HasRecommendedBits => RecommendedBits is not null;

    public bool HasSavedFile => LastSavedPath is not null;

    public string FitButtonText => RecommendedBits is { } bits ? Strings.Format(Strings.FitBits, bits) : string.Empty;

    public bool HasCover => Cover is not null;

    public string CoverInfo => Cover is null ? string.Empty : Strings.Format(Strings.ImageInfoFormat, Cover.Width, Cover.Height, Cover.Name);

    public bool HasAttachments => Attachments.Count > 0;

    public bool HasNoAttachments => Attachments.Count == 0;

    public bool HasContent => MessageText.Length > 0 || Attachments.Count > 0;

    public int BitsIndex
    {
        get => BitsPerChannel - 1;
        set
        {
            if (value >= 0 && value < BitsOptions.Count)
            {
                BitsPerChannel = BitsOptions[value].Bits;
            }
        }
    }

    public PasswordStrength Strength => PasswordStrengthMeter.Evaluate(Password);

    public int StrengthLevel => (int)Strength;

    public bool ShowStrength => Password.Length > 0;

    public string StrengthText => Password.Length == 0 ? string.Empty : Strings.Format(Strings.StrengthFormat, Strings.StrengthName(Strength));

    public bool ShowPasswordMismatch => ConfirmPassword.Length > 0 && !string.Equals(Password, ConfirmPassword, StringComparison.Ordinal);

    public long CapacityBytes => Cover?.CapacityFor(BitsPerChannel) ?? 0;

    public CapacityState CapacityState
    {
        get
        {
            if (Cover is null)
            {
                return CapacityState.NoImage;
            }

            if (!HasContent)
            {
                return CapacityState.Empty;
            }

            if (IsMeasuring || RequiredBytes is null)
            {
                return CapacityState.Measuring;
            }

            if (RequiredBytes > CapacityBytes)
            {
                return CapacityState.Over;
            }

            return RequiredBytes * 2 > CapacityBytes ? CapacityState.Warning : CapacityState.Ok;
        }
    }

    public double UsagePercent => CapacityBytes == 0 || RequiredBytes is null || !HasContent
        ? 0
        : Math.Min(100, 100.0 * RequiredBytes.Value / CapacityBytes);

    public bool IsCapacityWarning => CapacityState == CapacityState.Warning;

    public bool IsCapacityOver => CapacityState == CapacityState.Over;

    public string CapacityText => CapacityState switch
    {
        CapacityState.NoImage => Strings.CapacityNoImage,
        CapacityState.Measuring => Strings.CapacityMeasuring,
        _ => Strings.Format(
            Strings.CapacityFormat,
            ByteSize.Format(HasContent ? RequiredBytes ?? 0 : 0),
            ByteSize.Format(CapacityBytes),
            (UsagePercent / 100).ToString("0.##%", Strings.Culture ?? System.Globalization.CultureInfo.CurrentUICulture)),
    };

    public string CapacityHint => CapacityState switch
    {
        CapacityState.Warning => Strings.CapacityWarning,
        CapacityState.Over => Strings.CapacityOver,
        _ => string.Empty,
    };

    public bool HasCapacityHint => CapacityHint.Length > 0;

    public Task LoadCoverFromPathAsync(string path) => LoadCoverAsync(() => _loader.LoadFileAsync(path, ImagePurpose.Cover));

    public Task LoadCoverFromBytesAsync(string name, byte[] bytes) =>
        LoadCoverAsync(() => _loader.LoadBytesAsync(name, bytes, ImagePurpose.Cover));

    public async Task AddFilesFromPathsAsync(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths)
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                continue;
            }

            if (info.Length > MaxAttachmentBytes)
            {
                ShowStatus(StatusSeverity.Error, Strings.ErrorTitle, Strings.Format(Strings.FileTooLarge, info.Name, ByteSize.Format(MaxAttachmentBytes)));
                continue;
            }

            try
            {
                Attachments.Add(new AttachmentViewModel(info.Name, await File.ReadAllBytesAsync(info.FullName)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowStatus(StatusSeverity.Error, Strings.ErrorTitle, ex.Message);
            }
        }
    }

    public void Dispose()
    {
        _measureCancellation?.Cancel();
        _measureCancellation?.Dispose();
    }

    protected override void OnBusyChanged() => HideCommand.NotifyCanExecuteChanged();

    /// <summary>WinUI's TextBox uses "\r" for new lines; store the portable "\n".</summary>
    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static Payload BuildPayload(string text, IReadOnlyList<AttachmentViewModel> files)
    {
        var entries = new List<PayloadEntry>(files.Count + 1);
        if (text.Length > 0)
        {
            entries.Add(PayloadEntry.FromText(NormalizeNewlines(text)));
        }

        entries.AddRange(files.Select(file => PayloadEntry.FromFile(file.Name, file.Content.Span)));
        return new Payload(entries);
    }

    private static string FormatPsnr(double psnr) =>
        double.IsPositiveInfinity(psnr) ? "∞" : psnr.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    partial void OnCoverChanged(LoadedImage? value) => OnCapacityInputsChanged();

    partial void OnBitsPerChannelChanged(int value) => OnCapacityInputsChanged();

    partial void OnRequiredBytesChanged(long? value) => OnCapacityInputsChanged();

    partial void OnIsMeasuringChanged(bool value) => OnCapacityInputsChanged();

    partial void OnMessageTextChanged(string value) => OnContentChanged();

    private void OnContentChanged()
    {
        OnPropertyChanged(nameof(HasContent));
        ScheduleMeasure();
        OnCapacityInputsChanged();
    }

    private void OnCapacityInputsChanged()
    {
        OnPropertyChanged(nameof(CapacityBytes));
        OnPropertyChanged(nameof(CapacityState));
        OnPropertyChanged(nameof(UsagePercent));
        OnPropertyChanged(nameof(IsCapacityWarning));
        OnPropertyChanged(nameof(IsCapacityOver));
        OnPropertyChanged(nameof(CapacityText));
        OnPropertyChanged(nameof(CapacityHint));
        OnPropertyChanged(nameof(HasCapacityHint));
        OnPropertyChanged(nameof(RecommendedBits));
        OnPropertyChanged(nameof(HasRecommendedBits));
        OnPropertyChanged(nameof(FitButtonText));
        HideCommand.NotifyCanExecuteChanged();
        FitBitsCommand.NotifyCanExecuteChanged();
    }

    private void ScheduleMeasure()
    {
        _measureCancellation?.Cancel();
        _measureCancellation?.Dispose();
        _measureCancellation = null;

        if (!HasContent)
        {
            RequiredBytes = 0;
            IsMeasuring = false;
            MeasureTask = Task.CompletedTask;
            return;
        }

        var cancellation = _measureCancellation = new CancellationTokenSource();
        IsMeasuring = true;
        MeasureTask = MeasureAsync(MessageText, [.. Attachments], cancellation.Token);
    }

    private async Task MeasureAsync(string text, IReadOnlyList<AttachmentViewModel> files, CancellationToken cancellationToken)
    {
        try
        {
            if (_measureDelay > TimeSpan.Zero)
            {
                await Task.Delay(_measureDelay, cancellationToken);
            }

            // Compression decides the real size, so measure the exact bytes that would be embedded.
            var size = await Task.Run(() => StegoEngine.GetContainerSize(BuildPayload(text, files)), cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                RequiredBytes = size;
                IsMeasuring = false;
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer measurement.
        }
        catch (EncoderFallbackException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                RequiredBytes = null;
                IsMeasuring = false;
                ShowStatus(StatusSeverity.Error, Strings.ErrorTitle, Strings.InvalidText);
            }
        }
    }

    private bool CanHide() =>
        Cover is not null
        && HasContent
        && Password.Length > 0
        && string.Equals(Password, ConfirmPassword, StringComparison.Ordinal)
        && !IsBusy
        && CapacityState is CapacityState.Ok or CapacityState.Warning;

    private async Task LoadCoverAsync(Func<Task<LoadedImage>> load)
    {
        IsBusy = true;
        ClearStatus();
        try
        {
            Cover = await load();
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
    private async Task BrowseCoverAsync()
    {
        if (await _dialogs.PickImageAsync() is { } path)
        {
            await LoadCoverFromPathAsync(path);
        }
    }

    [RelayCommand]
    private async Task PasteCoverAsync()
    {
        var image = await _clipboard.GetImageAsync();
        if (image is null)
        {
            ShowStatus(StatusSeverity.Warning, Strings.PasteImage, Strings.ClipboardNoImage);
            return;
        }

        await LoadCoverFromBytesAsync(image.Name, image.Bytes);
    }

    [RelayCommand]
    private Task LoadDroppedCoverAsync(ImageInput? input) =>
        input is null ? Task.CompletedTask : LoadCoverFromBytesAsync(input.Name, input.Bytes);

    [RelayCommand]
    private Task AddDroppedFilesAsync(IReadOnlyList<string>? paths) =>
        paths is null ? Task.CompletedTask : AddFilesFromPathsAsync(paths);

    [RelayCommand]
    private async Task AddFilesAsync() => await AddFilesFromPathsAsync(await _dialogs.PickFilesAsync());

    [RelayCommand]
    private void RemoveAttachment(AttachmentViewModel? attachment)
    {
        if (attachment is not null)
        {
            Attachments.Remove(attachment);
        }
    }

    [RelayCommand(CanExecute = nameof(CanFitBits))]
    private void FitBits()
    {
        if (RecommendedBits is { } bits)
        {
            BitsPerChannel = bits;
        }
    }

    private bool CanFitBits() => RecommendedBits is not null;

    [RelayCommand]
    private void GeneratePassphrase()
    {
        var thai = (Strings.Culture ?? System.Globalization.CultureInfo.CurrentUICulture).TwoLetterISOLanguageName == "th";
        GeneratedPassphrase = PassphraseGenerator.Generate(thai);
        Password = GeneratedPassphrase;
        ConfirmPassword = GeneratedPassphrase;
    }

    [RelayCommand(CanExecute = nameof(HasGeneratedPassphrase))]
    private void CopyGeneratedPassphrase()
    {
        var text = GeneratedPassphrase;
        _clipboard.SetText(text);
        ShowStatus(StatusSeverity.Informational, Strings.CopyText, Strings.Format(Strings.CopiedWillClear, (int)ClipboardClearDelay.TotalSeconds));
        ClipboardClearTask = ClearClipboardLaterAsync(text);
    }

    private async Task ClearClipboardLaterAsync(string text)
    {
        await Task.Delay(ClipboardClearDelay);
        _clipboard.ClearIfText(text);
    }

    private bool CanCopySavedFile() => LastSavedPath is not null && File.Exists(LastSavedPath);

    [RelayCommand(CanExecute = nameof(CanCopySavedFile))]
    private async Task CopySavedFileAsync()
    {
        await _clipboard.CopyFilesAsync([LastSavedPath!]);
        ShowStatus(StatusSeverity.Success, Strings.CopyAsFile, Strings.CopiedAsFile, LastSavedPath);
    }

    [RelayCommand(CanExecute = nameof(CanHide))]
    private async Task HideAsync()
    {
        var cover = Cover!;
        var path = await _dialogs.PickSavePngAsync(Path.GetFileNameWithoutExtension(cover.Name) + ".png");
        if (path is null)
        {
            return;
        }

        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            path += ".png";
        }

        IsBusy = true;
        ClearStatus();
        try
        {
            var payload = BuildPayload(MessageText, [.. Attachments]);
            var password = Password;
            var bits = BitsPerChannel;
            var difference = await Task.Run(() =>
            {
                var stego = _engine.Embed(cover.Pixels, payload, password, bits);
                _codec.SavePng(stego, path, overwrite: true); // the save dialog already asked about overwriting
                return ImageMetrics.Compare(cover.Pixels, stego);
            });

            if (SaveToVault && _vault is not null && VaultName.Trim().Length > 0)
            {
                _vault.Save(VaultName.Trim(), password);
            }

            Password = string.Empty;
            ConfirmPassword = string.Empty;
            LastSavedPath = path;
            ShowStatus(
                StatusSeverity.Success,
                Strings.HideSuccessTitle,
                Strings.Format(
                    Strings.HideSuccessMessage,
                    Path.GetFileName(path),
                    difference.ChangedRatio.ToString("0.##%", System.Globalization.CultureInfo.InvariantCulture),
                    FormatPsnr(difference.Psnr)),
                path);
        }
        catch (StegoException ex) when (ex.Error == StegoError.InsufficientCapacity)
        {
            ShowStatus(StatusSeverity.Error, Strings.ErrorTitle, Strings.CapacityOver);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or EncoderFallbackException or InvalidImageException)
        {
            ShowStatus(StatusSeverity.Error, Strings.ErrorTitle, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
