using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SweetHide.Presentation.Services;
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

    private void OnRemoveVaultEntryClick(object sender, RoutedEventArgs e) =>
        ViewModel.RemoveVaultEntryCommand.Execute((sender as FrameworkElement)?.DataContext as VaultEntry);
}
