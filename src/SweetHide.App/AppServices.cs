using Microsoft.UI.Xaml;

using SweetHide.App.Services;
using SweetHide.Core;
using SweetHide.Core.Imaging;
using SweetHide.Presentation.Imaging;
using SweetHide.Presentation.Services;
using SweetHide.Presentation.ViewModels;

namespace SweetHide.App;

/// <summary>Creates the services and one view model per page (kept for the app's lifetime so state survives navigation).</summary>
internal sealed class AppServices
{
    public AppServices(Window window, ISettingsStore store, AppSettings settings)
    {
        var codec = new SkiaImageCodec();
        var engine = new StegoEngine();
        var loader = new ImageLoader(codec);
        var dialogs = new FileDialogService(window);
        var clipboard = new ClipboardService();
        var shell = new ShellService();

        Theme = new ThemeService(window);
        Hide = new HideViewModel(engine, codec, loader, dialogs, clipboard, shell);
        Reveal = new RevealViewModel(engine, loader, dialogs, clipboard, shell);
        Analyze = new AnalyzeViewModel(codec, loader, dialogs, clipboard, shell);
        Settings = new SettingsViewModel(store, Theme, settings);
    }

    public ThemeService Theme { get; }

    public HideViewModel Hide { get; }

    public RevealViewModel Reveal { get; }

    public AnalyzeViewModel Analyze { get; }

    public SettingsViewModel Settings { get; }
}
