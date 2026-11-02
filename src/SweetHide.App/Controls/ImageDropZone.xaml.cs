using System.Runtime.InteropServices;
using System.Windows.Input;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

using SweetHide.App.Services;
using SweetHide.Presentation;
using SweetHide.Presentation.Imaging;

using Windows.ApplicationModel.DataTransfer;

namespace SweetHide.App.Controls;

/// <summary>Drop target + preview for one image, with Browse and Paste buttons.</summary>
public sealed partial class ImageDropZone : UserControl
{
    public static readonly DependencyProperty ImageProperty = DependencyProperty.Register(
        nameof(Image), typeof(object), typeof(ImageDropZone), new PropertyMetadata(null, OnImageChanged));

    public static readonly DependencyProperty ImageInfoProperty = DependencyProperty.Register(
        nameof(ImageInfo), typeof(string), typeof(ImageDropZone), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty BrowseCommandProperty = DependencyProperty.Register(
        nameof(BrowseCommand), typeof(ICommand), typeof(ImageDropZone), new PropertyMetadata(null));

    public static readonly DependencyProperty PasteCommandProperty = DependencyProperty.Register(
        nameof(PasteCommand), typeof(ICommand), typeof(ImageDropZone), new PropertyMetadata(null));

    public static readonly DependencyProperty DropCommandProperty = DependencyProperty.Register(
        nameof(DropCommand), typeof(ICommand), typeof(ImageDropZone), new PropertyMetadata(null));

    private int _previewVersion;

    public ImageDropZone()
    {
        InitializeComponent();
    }

    /// <summary>The <see cref="LoadedImage"/> to preview, or null for the empty state.</summary>
    public object? Image
    {
        get => GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    public string ImageInfo
    {
        get => (string)GetValue(ImageInfoProperty);
        set => SetValue(ImageInfoProperty, value);
    }

    public ICommand? BrowseCommand
    {
        get => (ICommand?)GetValue(BrowseCommandProperty);
        set => SetValue(BrowseCommandProperty, value);
    }

    public ICommand? PasteCommand
    {
        get => (ICommand?)GetValue(PasteCommandProperty);
        set => SetValue(PasteCommandProperty, value);
    }

    /// <summary>Executed with an <see cref="Presentation.Services.ImageInput"/> when an image is dropped.</summary>
    public ICommand? DropCommand
    {
        get => (ICommand?)GetValue(DropCommandProperty);
        set => SetValue(DropCommandProperty, value);
    }

    private static void OnImageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ImageDropZone)d).ShowImage(e.NewValue as LoadedImage);

    private async void ShowImage(LoadedImage? image)
    {
        var version = ++_previewVersion;
        EmptyState.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
        LoadedState.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        if (image is null)
        {
            Preview.Source = null;
            return;
        }

        var bitmap = await BitmapLoader.LoadAsync(image);
        if (version == _previewVersion)
        {
            Preview.Source = bitmap;
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!DataViewReader.HasImageOrFiles(e.DataView))
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = Strings.DropToUse;
        Outline.Stroke = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
    }

    private void OnDragLeave(object sender, DragEventArgs e) => ResetOutline();

    private async void OnDrop(object sender, DragEventArgs e)
    {
        ResetOutline();
        var deferral = e.GetDeferral();
        try
        {
            var input = await DataViewReader.ReadImageAsync(e.DataView);
            if (input is not null && DropCommand?.CanExecute(input) == true)
            {
                DropCommand.Execute(input);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            // The dragged item could not be read (e.g. a virtual file); nothing to do.
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void ResetOutline() =>
        Outline.Stroke = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"];
}

/// <summary>Turns image bytes into something an Image element can show.</summary>
internal static class BitmapLoader
{
    public static async Task<BitmapImage?> LoadAsync(LoadedImage image)
    {
        var bitmap = await FromBytesAsync(image.EncodedBytes.ToArray());
        if (bitmap is not null)
        {
            return bitmap;
        }

        // Windows couldn't decode the original (e.g. WebP without the codec): preview our decoded pixels instead.
        var png = await Task.Run(() =>
        {
            using var stream = new MemoryStream();
            new SweetHide.Core.Imaging.SkiaImageCodec().EncodePng(image.Pixels, stream);
            return stream.ToArray();
        });
        return await FromBytesAsync(png);
    }

    public static async Task<BitmapImage?> FromBytesAsync(byte[] bytes)
    {
        try
        {
            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(bytes, writable: false).AsRandomAccessStream();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            return null;
        }
    }
}
