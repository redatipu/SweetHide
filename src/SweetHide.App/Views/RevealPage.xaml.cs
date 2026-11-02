using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

using SweetHide.Presentation.ViewModels;

using Windows.System;

namespace SweetHide.App.Views;

public sealed partial class RevealPage : Page
{
    public RevealPage()
    {
        ViewModel = App.Services.Reveal;
        InitializeComponent();
        Loaded += (_, _) => ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    public RevealViewModel ViewModel { get; }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // On smaller windows the results land below the fold; bring them into view once revealing finishes.
        if (e.PropertyName == nameof(RevealViewModel.IsBusy) && !ViewModel.IsBusy && ViewModel.HasResults)
        {
            DispatcherQueue.TryEnqueue(() => ResultsColumn.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0 }));
        }
    }

    private void OnPasteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
        args.Handled = PageHelpers.PasteImageUnlessTyping(XamlRoot, ViewModel.PasteImageCommand);

    private void OnPasswordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && ViewModel.RevealCommand.CanExecute(null))
        {
            e.Handled = true;
            ViewModel.RevealCommand.Execute(null);
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e) =>
        ViewModel.CopyTextCommand.Execute(ItemOf(sender));

    private void OnSaveClick(object sender, RoutedEventArgs e) =>
        ViewModel.SaveFileCommand.Execute(ItemOf(sender));

    private static RevealedItemViewModel? ItemOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as RevealedItemViewModel;
}
