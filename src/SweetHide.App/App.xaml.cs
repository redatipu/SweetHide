using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using SweetHide.Presentation;
using SweetHide.Presentation.Services;

namespace SweetHide.App;

public partial class App : Application
{
    private MainWindow? _window;

    private readonly JsonSettingsStore _store;
    private readonly AppSettings _settings;

    public App()
    {
        SmokeTest.ParseArguments(Environment.GetCommandLineArgs());
        _store = new JsonSettingsStore(SmokeTest.IsEnabled
            ? Path.Combine(SmokeTest.OutputDirectory!, "settings.json")
            : JsonSettingsStore.DefaultPath);
        _settings = _store.Load();
        if (SmokeTest.Language is { } language)
        {
            _settings = _settings with { Language = language == "th" ? AppLanguage.Thai : AppLanguage.English };
        }

        if (SmokeTest.Theme is { } theme)
        {
            _settings = _settings with { Theme = theme == "light" ? AppTheme.Light : AppTheme.Dark };
        }

        // RequestedTheme can only be set here. "Follow Windows" leaves it unset.
        if (_settings.Theme != AppTheme.Default)
        {
            RequestedTheme = _settings.Theme == AppTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark;
        }

        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    /// <summary>Composition root, created once the main window exists.</summary>
    internal static AppServices Services { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Strings.ApplyLanguage(_settings.Language); // before any page builds its text

        _window = new MainWindow();
        Services = new AppServices(_window, _store, _settings);
        Services.Theme.Apply(_settings.Theme);
        _window.ShowStartPage();

        if (SmokeTest.IsEnabled)
        {
            // Mica isn't captured by RenderTargetBitmap; give screenshots a solid background.
            if (_window.Content is Panel root && Resources.TryGetValue("SolidBackgroundFillColorBaseBrush", out var brush))
            {
                root.Background = brush as Brush;
            }

            _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1320, 900));
            _window.Activate();
            _ = SmokeTest.RunAsync(_window);
            return;
        }

        _window.Activate();
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        if (SmokeTest.IsEnabled)
        {
            SmokeTest.Fail(e.Exception);
            return;
        }

        try
        {
            var log = Path.Combine(Path.GetDirectoryName(JsonSettingsStore.DefaultPath)!, "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log, $"{DateTimeOffset.Now:O}{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Nothing more we can do while crashing.
        }
    }
}
