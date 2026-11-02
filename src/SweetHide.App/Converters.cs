using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using SweetHide.Presentation;
using SweetHide.Presentation.ViewModels;

namespace SweetHide.App;

/// <summary>Small functions used from x:Bind (no IValueConverter boilerplate).</summary>
internal static class Converters
{
    public static InfoBarSeverity Severity(StatusSeverity severity) => severity switch
    {
        StatusSeverity.Success => InfoBarSeverity.Success,
        StatusSeverity.Warning => InfoBarSeverity.Warning,
        StatusSeverity.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };

    public static bool Not(bool value) => !value;

    /// <summary>In-app preview of a revealed image file; null when there is nothing to show or it can't be decoded.</summary>
    public static ImageSource? ImageFromBytes(byte[] bytes)
    {
        if (bytes is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage { DecodePixelWidth = 640 };
            bitmap.SetSource(new MemoryStream(bytes, writable: false).AsRandomAccessStream());
            return bitmap;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>Lines of the "how to send it" guide with a ✓/✗ in front.</summary>
internal static class GuideText
{
    public static string Line => "✅ " + Strings.SendGuideLine;

    public static string Telegram => "✅ " + Strings.SendGuideTelegram;

    public static string WhatsApp => "✅ " + Strings.SendGuideWhatsApp;

    public static string Discord => "✅ " + Strings.SendGuideDiscord;

    public static string Bad => "❌ " + Strings.SendGuideBad;
}
