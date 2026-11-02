using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using SweetHide.Presentation;
using SweetHide.Presentation.Services;

namespace SweetHide.App;

public partial class App : Application
{
    private MainWindow? _window;

    private static readonly AppPaths Paths = AppPaths.Resolve(AppContext.BaseDirectory);
    private readonly JsonSettingsStore _store;
    private readonly AppSettings _settings;

    public App()
    {
        SmokeTest.ParseArguments(Environment.GetCommandLineArgs());
        _store = new JsonSettingsStore(SmokeTest.IsEnabled
            ? Path.Combine(SmokeTest.OutputDirectory!, "settings.json")
            : Paths.SettingsFile);
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
        Services = new AppServices(_window, _store, _settings, Paths);
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

        _window.AttachUpdateBanner(Services.Settings);
        _window.Closed += (_, _) => Services.Dispose();
        _window.Activate();
        Services.StartBackgroundFeatures();
        _ = Services.Settings.CheckOnStartupAsync();

        var startup = StartupRequest.Parse(Environment.GetCommandLineArgs());
        if (startup is not null)
        {
            _ = OpenStartupFileAsync(startup);
        }
        else if (Services.Settings.ShowWelcome && _window.Content is FrameworkElement content)
        {
            content.Loaded += async (_, _) =>
            {
                Services.Settings.MarkWelcomeSeen();
                await _window.ShowWelcomeAsync(() => Services.Reveal.LoadSampleAsync(Services.Codec));
            };
        }
    }

    /// <summary>"Hide/Reveal with SweetHide" from Explorer, a file dropped on the exe, or "Open with".</summary>
    private async Task OpenStartupFileAsync(StartupRequest request)
    {
        _window!.NavigateTo(request.Page);
        if (request.Page == "hide")
        {
            await Services.Hide.LoadCoverFromPathAsync(request.Path);
        }
        else
        {
            await Services.Reveal.LoadImageFromPathAsync(request.Path);
        }
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
            var log = Paths.CrashLog;
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log, $"{DateTimeOffset.Now:O}{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Nothing more we can do while crashing.
        }
    }
}
