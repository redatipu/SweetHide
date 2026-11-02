using Microsoft.UI.Xaml.Controls;

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
}
