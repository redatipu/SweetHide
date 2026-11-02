using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

using SweetHide.Presentation.Services;

namespace SweetHide.App.Services;

internal sealed class ThemeService(Window window) : IThemeService
{
    public void Apply(AppTheme theme)
    {
        if (window.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                AppTheme.Light => ElementTheme.Light,
                AppTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }

        // Caption buttons (minimise/maximise/close) follow the same choice.
        window.AppWindow.TitleBar.PreferredTheme = theme switch
        {
            AppTheme.Light => TitleBarTheme.Light,
            AppTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }
}
