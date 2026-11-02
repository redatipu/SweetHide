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

public sealed record AppSettings(AppLanguage Language = AppLanguage.Default, AppTheme Theme = AppTheme.Default);

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
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SweetHide", "settings.json");

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

    private static AppSettings Sanitize(AppSettings settings) => new(
        Enum.IsDefined(settings.Language) ? settings.Language : AppLanguage.Default,
        Enum.IsDefined(settings.Theme) ? settings.Theme : AppTheme.Default);
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
