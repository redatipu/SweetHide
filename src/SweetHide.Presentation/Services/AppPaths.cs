namespace SweetHide.Presentation.Services;

/// <summary>
/// Where SweetHide keeps its own files. Normally %LOCALAPPDATA%\SweetHide; in portable mode (a file named
/// "portable" next to the executable) a "data" folder beside the app, so it can live on a USB stick.
/// </summary>
public sealed class AppPaths
{
    public const string PortableMarker = "portable";

    private AppPaths(string dataDirectory, bool isPortable)
    {
        DataDirectory = dataDirectory;
        IsPortable = isPortable;
    }

    public string DataDirectory { get; }

    public bool IsPortable { get; }

    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public string VaultFile => Path.Combine(DataDirectory, "vault.dat");

    public string CrashLog => Path.Combine(DataDirectory, "crash.log");

    public static AppPaths Resolve(string appDirectory, string? localAppData = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(appDirectory);
        if (File.Exists(Path.Combine(appDirectory, PortableMarker)))
        {
            return new AppPaths(Path.Combine(appDirectory, "data"), isPortable: true);
        }

        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppPaths(Path.Combine(localAppData, "SweetHide"), isPortable: false);
    }
}
