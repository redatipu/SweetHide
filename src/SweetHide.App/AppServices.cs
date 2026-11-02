using System.Runtime.InteropServices;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

using SweetHide.App.Services;
using SweetHide.Core;
using SweetHide.Core.Imaging;
using SweetHide.Presentation;
using SweetHide.Presentation.Imaging;
using SweetHide.Presentation.Services;
using SweetHide.Presentation.ViewModels;

namespace SweetHide.App;

/// <summary>Creates the services and one view model per page (kept for the app's lifetime so state survives navigation).</summary>
internal sealed class AppServices : IDisposable
{
    private readonly MainWindow _window;
    private readonly AutoRevealer _autoRevealer;
    private FolderWatcher? _watcher;

    public AppServices(MainWindow window, ISettingsStore store, AppSettings settings, AppPaths paths)
    {
        _window = window;
        Codec = new SkiaImageCodec();
        var engine = new StegoEngine();
        var loader = new ImageLoader(Codec);
        var dialogs = new FileDialogService(window);
        var clipboard = new ClipboardService();
        var shell = new ShellService();
        var vault = new ProtectedFileVault(paths.VaultFile, new DpapiProtector());
        _autoRevealer = new AutoRevealer(engine, loader, vault);
        Explorer = new ExplorerIntegration();

        Theme = new ThemeService(window);
        Hide = new HideViewModel(engine, Codec, loader, dialogs, clipboard, shell, vault: vault);
        Reveal = new RevealViewModel(engine, loader, dialogs, clipboard, shell, _autoRevealer);
        Analyze = new AnalyzeViewModel(Codec, loader, dialogs, clipboard, shell);
        var updates = new GitHubUpdateService(
            new HttpClient { Timeout = TimeSpan.FromSeconds(60) },
            assetSuffix: RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "-win-arm64.zip" : "-win-x64.zip");
        Settings = new SettingsViewModel(store, Theme, settings, updates, shell, downloadFolder: null, paths.IsPortable, vault, Explorer, dialogs);

        vault.Changed += (_, _) => window.DispatcherQueue.TryEnqueue(Settings.RefreshVault);
        Settings.WatchFolderChanged += (_, _) => RestartWatcher();
        Hide.PropertyChanged += (_, e) =>
        {
            // Toast when a hide finishes while the user is in another window.
            if (e.PropertyName == nameof(HideViewModel.LastSavedPath) && Hide.LastSavedPath is { } path && !window.IsForeground)
            {
                Notifications.Show(Strings.DoneTitle, Strings.Format(Strings.HideDoneToast, Path.GetFileName(path)), "hide");
            }
        };
    }

    public IImageCodec Codec { get; }

    public ThemeService Theme { get; }

    public ExplorerIntegration Explorer { get; }

    public Notifications Notifications { get; } = new();

    public HideViewModel Hide { get; }

    public RevealViewModel Reveal { get; }

    public AnalyzeViewModel Analyze { get; }

    public SettingsViewModel Settings { get; }

    /// <summary>Starts notifications and the folder watcher; not used by the smoke test.</summary>
    public void StartBackgroundFeatures()
    {
        Notifications.Invoked += (_, action) => _window.DispatcherQueue.TryEnqueue(() => _window.BringToFront(action));
        Notifications.Register();
        Explorer.RefreshIfEnabled();
        RestartWatcher();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        Notifications.Dispose();
    }

    private void RestartWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (Settings.WatchFolder is { } folder && Directory.Exists(folder))
        {
            _watcher = new FolderWatcher(folder, OnWatchedImageAsync);
        }
    }

    private async Task OnWatchedImageAsync(string path)
    {
        var result = await _autoRevealer.TryFileAsync(path);
        if (result is null)
        {
            return;
        }

        _window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            Reveal.ShowAutoRevealed(result);
            if (_window.IsForeground)
            {
                _window.NavigateTo("reveal");
            }
            else
            {
                Notifications.Show(Strings.Format(Strings.WatchFound, Path.GetFileName(path)), Strings.Format(Strings.RevealedWithVault, result.VaultName), "reveal");
            }
        });
    }
}
