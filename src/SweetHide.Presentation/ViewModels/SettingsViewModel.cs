using System.Reflection;

using CommunityToolkit.Mvvm.ComponentModel;

using SweetHide.Presentation.Services;

namespace SweetHide.Presentation.ViewModels;

/// <summary>Language and theme, saved to <see cref="ISettingsStore"/>.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly IThemeService _theme;
    private readonly AppLanguage _startupLanguage;
    private AppSettings _settings;

    public SettingsViewModel(ISettingsStore store, IThemeService theme, AppSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        _store = store;
        _theme = theme;
        _settings = current;
        _startupLanguage = current.Language;
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

    private void Update(AppSettings settings)
    {
        _settings = settings;
        _store.Save(settings);
    }
}
