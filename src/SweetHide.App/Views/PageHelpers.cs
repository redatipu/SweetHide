using System.Windows.Input;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace SweetHide.App.Views;

internal static class PageHelpers
{
    /// <summary>Ctrl+V pastes an image, except while typing, where it must paste text as usual.</summary>
    /// <returns>Whether the shortcut was handled.</returns>
    public static bool PasteImageUnlessTyping(XamlRoot root, ICommand paste)
    {
        if (FocusManager.GetFocusedElement(root) is TextBox or PasswordBox or AutoSuggestBox)
        {
            return false;
        }

        if (paste.CanExecute(null))
        {
            paste.Execute(null);
        }

        return true;
    }
}
