using System.ComponentModel;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

using SweetHide.App.Controls;
using SweetHide.Presentation.ViewModels;

namespace SweetHide.App.Views;

public sealed partial class AnalyzePage : Page
{
    private int _renderVersion;

    public AnalyzePage()
    {
        ViewModel = App.Services.Analyze;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public AnalyzeViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ShowBitPlane();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AnalyzeViewModel.BitPlanePng))
        {
            ShowBitPlane();
        }
    }

    private async void ShowBitPlane()
    {
        var version = ++_renderVersion;
        var bitmap = ViewModel.BitPlanePng is { } png ? await BitmapLoader.FromBytesAsync(png) : null;
        if (version == _renderVersion)
        {
            BitPlaneImage.Source = bitmap;
        }
    }

    private void OnPasteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
        args.Handled = PageHelpers.PasteImageUnlessTyping(XamlRoot, ViewModel.PasteImageCommand);
}
