using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

using SweetHide.Presentation.Services;

using Windows.UI.ViewManagement;

namespace SweetHide.App.Services;

internal sealed class ThemeService(Window window) : IThemeService
{
    public void Apply(AppTheme theme)
    {
        // Resolve "follow Windows" explicitly: once the app was started with a fixed theme, ElementTheme.Default
        // would keep following the app's start-up theme rather than the system one.
        var dark = theme switch
        {
            AppTheme.Light => false,
            AppTheme.Dark => true,
            _ => WindowsUsesDarkMode(),
        };

        if (window.Content is FrameworkElement root)
        {
            root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        }

        // Caption buttons (minimise/maximise/close) follow the same choice.
        window.AppWindow.TitleBar.PreferredTheme = dark ? TitleBarTheme.Dark : TitleBarTheme.Light;
    }

    private static bool WindowsUsesDarkMode()
    {
        var background = new UISettings().GetColorValue(UIColorType.Background);
        return background.R + background.G + background.B < 3 * 128;
    }
}
