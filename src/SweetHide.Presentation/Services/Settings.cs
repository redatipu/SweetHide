using System.Text.Json;
using System.Text.Json.Serialization;

namespace SweetHide.Presentation.Services;

public enum AppLanguage
{
    Default = 0,
    Thai = 1,
    English = 2,
}

public enum AppTheme
{
    Default = 0,
    Light = 1,
    Dark = 2,
}

/// <param name="Language">UI language; <see cref="AppLanguage.Default"/> follows Windows.</param>
/// <param name="Theme">Colour theme; SweetHide starts in <see cref="AppTheme.Dark"/>.</param>
/// <param name="CheckForUpdates">Ask GitHub for a newer release at start-up. Off by default: no network unless asked.</param>
public sealed record AppSettings(AppLanguage Language = AppLanguage.Default, AppTheme Theme = AppTheme.Dark, bool CheckForUpdates = false);

public interface ISettingsStore
{
    AppSettings Load();

    void Save(AppSettings settings);
}

/// <summary>
/// Settings as JSON under %LOCALAPPDATA%\SweetHide. (Unpackaged apps have no ApplicationData.LocalSettings.)
/// A missing or damaged file just means defaults.
/// </summary>
public sealed class JsonSettingsStore(string path) : ISettingsStore
{
    public string FilePath { get; } = path;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            var settings = JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJsonContext.Default.AppSettings);
            return settings is null ? new AppSettings() : Sanitize(settings);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
    }

    private static AppSettings Sanitize(AppSettings settings) => settings with
    {
        Language = Enum.IsDefined(settings.Language) ? settings.Language : AppLanguage.Default,
        Theme = Enum.IsDefined(settings.Theme) ? settings.Theme : new AppSettings().Theme,
    };
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
