using System.Reflection;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using SweetHide.Presentation.Services;

namespace SweetHide.Presentation.ViewModels;

/// <summary>Language, theme and updates, saved to <see cref="ISettingsStore"/>.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly IThemeService _theme;
    private readonly IUpdateService? _updates;
    private readonly IShellService? _shell;
    private readonly string _downloadFolder;
    private readonly AppLanguage _startupLanguage;
    private AppSettings _settings;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand), nameof(DownloadUpdateCommand))]
    private bool _isCheckingUpdates;

    [ObservableProperty]
    private string _updateStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateBanner))]
    [NotifyCanExecuteChangedFor(nameof(DownloadUpdateCommand), nameof(OpenReleasePageCommand))]
    private UpdateInfo? _availableUpdate;

    public SettingsViewModel(
        ISettingsStore store,
        IThemeService theme,
        AppSettings current,
        IUpdateService? updates = null,
        IShellService? shell = null,
        string? downloadFolder = null,
        bool isPortable = false)
    {
        ArgumentNullException.ThrowIfNull(current);
        _store = store;
        _theme = theme;
        _updates = updates;
        _shell = shell;
        _downloadFolder = downloadFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        _settings = current;
        _startupLanguage = current.Language;
        IsPortable = isPortable;
    }

    public bool IsPortable { get; }

    public bool CanCheckUpdates => _updates is not null;

    public bool HasUpdate => AvailableUpdate is not null;

    public string UpdateBanner => AvailableUpdate is null ? string.Empty : Strings.Format(Strings.UpdateAvailable, AvailableUpdate.Version);

    /// <summary>The last download, for tests and "show in folder".</summary>
    public string? DownloadedUpdatePath { get; private set; }

    public bool CheckForUpdatesOnStart
    {
        get => _settings.CheckForUpdates;
        set
        {
            if (value != _settings.CheckForUpdates)
            {
                Update(_settings with { CheckForUpdates = value });
                OnPropertyChanged();
            }
        }
    }

    public int LanguageIndex
    {
        get => (int)_settings.Language;
        set
        {
            if (value != LanguageIndex && Enum.IsDefined((AppLanguage)value))
            {
                Update(_settings with { Language = (AppLanguage)value });
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowRestartHint));
            }
        }
    }

    public int ThemeIndex
    {
        get => (int)_settings.Theme;
        set
        {
            if (value != ThemeIndex && Enum.IsDefined((AppTheme)value))
            {
                Update(_settings with { Theme = (AppTheme)value });
                _theme.Apply(_settings.Theme);
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Labels in <see cref="AppLanguage"/> order.</summary>
    public static IReadOnlyList<string> LanguageOptions => [Strings.LanguageDefault, Strings.LanguageThai, Strings.LanguageEnglish];

    /// <summary>Labels in <see cref="AppTheme"/> order.</summary>
    public static IReadOnlyList<string> ThemeOptions => [Strings.ThemeDefault, Strings.ThemeLight, Strings.ThemeDark];

    public bool ShowRestartHint => _settings.Language != _startupLanguage;

    public static string VersionText => Strings.Format(Strings.AboutVersion, Version);

    public static string Version { get; } =
        typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    /// <summary>Start-up check, only when the user turned it on. Failures stay quiet (no nagging offline).</summary>
    public async Task CheckOnStartupAsync()
    {
        if (_settings.CheckForUpdates && _updates is not null)
        {
            await CheckAsync(quiet: true);
        }
    }

    private bool CanCheck() => _updates is not null && !IsCheckingUpdates;

    private bool CanDownload() => AvailableUpdate?.AssetUrl is not null && !IsCheckingUpdates;

    private bool CanOpenReleasePage() => AvailableUpdate is not null && _shell is not null;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckForUpdatesAsync() => CheckAsync(quiet: false);

    private async Task CheckAsync(bool quiet)
    {
        IsCheckingUpdates = true;
        UpdateStatus = quiet ? UpdateStatus : Strings.UpdateChecking;
        try
        {
            AvailableUpdate = await _updates!.CheckAsync(Version);
            UpdateStatus = AvailableUpdate is null
                ? (quiet ? string.Empty : Strings.UpToDate)
                : Strings.Format(Strings.UpdateAvailable, AvailableUpdate.Version);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            UpdateStatus = quiet ? string.Empty : Strings.Format(Strings.UpdateFailed, ex.Message);
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadUpdateAsync()
    {
        IsCheckingUpdates = true;
        UpdateStatus = Strings.UpdateChecking;
        try
        {
            DownloadedUpdatePath = await _updates!.DownloadAsync(AvailableUpdate!, _downloadFolder);
            UpdateStatus = Strings.Format(Strings.UpdateDownloaded, DownloadedUpdatePath);
            _shell?.ShowInFolder(DownloadedUpdatePath);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            UpdateStatus = Strings.Format(Strings.UpdateFailed, ex.Message);
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenReleasePage))]
    private void OpenReleasePage() => _shell!.OpenUri(AvailableUpdate!.PageUrl);

    private void Update(AppSettings settings)
    {
        _settings = settings;
        _store.Save(settings);
    }
}
