using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

using SweetHide.App.Services;
using SweetHide.Presentation;
using SweetHide.Presentation.ViewModels;

using Windows.ApplicationModel.DataTransfer;

namespace SweetHide.App.Views;

public sealed partial class HidePage : Page
{
    public HidePage()
    {
        ViewModel = App.Services.Hide;
        InitializeComponent();
    }

    public HideViewModel ViewModel { get; }

    private void OnPasteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
        args.Handled = PageHelpers.PasteImageUnlessTyping(XamlRoot, ViewModel.PasteCoverCommand);

    private void OnOpenAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.BrowseCoverCommand.Execute(null);
    }

    private void OnPrimaryAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel.HideCommand.CanExecute(null))
        {
            ViewModel.HideCommand.Execute(null);
        }
    }

    private void OnAttachmentsDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = Strings.DropToAttach;
        }
    }

    private async void OnAttachmentsDrop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            await ViewModel.AddDroppedFilesCommand.ExecuteAsync(await DataViewReader.ReadFilePathsAsync(e.DataView));
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnRemoveAttachmentClick(object sender, RoutedEventArgs e) =>
        ViewModel.RemoveAttachmentCommand.Execute((sender as FrameworkElement)?.DataContext as AttachmentViewModel);
}
