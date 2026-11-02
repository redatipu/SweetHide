using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

using SweetHide.App.Views;
using SweetHide.Presentation;
using SweetHide.Presentation.ViewModels;

using Windows.Graphics;

namespace SweetHide.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        Title = Strings.AppTitle;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "SweetHide.ico"));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 640;
            presenter.PreferredMinimumHeight = 520;
        }

        // Size relative to the screen so it looks right at any DPI.
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = (int)(area.Width * 0.72);
        var height = (int)(area.Height * 0.86);
        AppWindow.MoveAndResize(new RectInt32(area.X + ((area.Width - width) / 2), area.Y + ((area.Height - height) / 2), width, height));
    }

    public void ShowStartPage() => Navigation.SelectedItem = Navigation.MenuItems[0];

    internal Page? CurrentPage => ContentFrame.Content as Page;

    /// <summary>Shows a banner when the settings page found a newer release.</summary>
    internal void AttachUpdateBanner(SettingsViewModel settings)
    {
        void Refresh()
        {
            UpdateBar.Message = settings.UpdateBanner;
            UpdateBar.IsOpen = settings.HasUpdate;
        }

        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.HasUpdate))
            {
                Refresh();
            }
        };
        Refresh();
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        UpdateBar.IsOpen = false;
        NavigateTo("settings");
    }

    /// <summary>Selects the navigation item with the given tag ("hide", "reveal", "analyze" or "settings").</summary>
    internal void NavigateTo(string tag) =>
        Navigation.SelectedItem = Navigation.MenuItems.Concat(Navigation.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .First(item => (item.Tag as string) == tag);

    private void OnPaneToggleRequested(TitleBar sender, object args) => Navigation.IsPaneOpen = !Navigation.IsPaneOpen;

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = (args.SelectedItemContainer?.Tag as string) switch
        {
            "reveal" => typeof(RevealPage),
            "analyze" => typeof(AnalyzePage),
            "settings" => typeof(SettingsPage),
            _ => typeof(HidePage),
        };

        if (ContentFrame.CurrentSourcePageType != page)
        {
            ContentFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
        }
    }
}
