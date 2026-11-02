using Microsoft.UI.Xaml.Controls;

using SweetHide.Presentation.ViewModels;

namespace SweetHide.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        ViewModel = App.Services.Settings;
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }
}
