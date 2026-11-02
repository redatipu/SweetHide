using Microsoft.UI.Xaml;

using SweetHide.Presentation;
using SweetHide.Presentation.Services;

namespace SweetHide.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    /// <summary>Composition root, created once the main window exists.</summary>
    internal static AppServices Services { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var store = new JsonSettingsStore(JsonSettingsStore.DefaultPath);
        var settings = store.Load();
        Strings.ApplyLanguage(settings.Language); // before any page builds its text

        _window = new MainWindow();
        Services = new AppServices(_window, store, settings);
        Services.Theme.Apply(settings.Theme);
        _window.ShowStartPage();
        _window.Activate();
    }
}
