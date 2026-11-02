using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace SweetHide.App.Services;

/// <summary>
/// Windows toast notifications for work that finishes while SweetHide is in the background (a hide finishing, the
/// watched folder finding a secret). Everything is best effort: if notifications can't be registered, nothing breaks.
/// </summary>
internal sealed class Notifications : IDisposable
{
    private bool _registered;

    /// <summary>Raised on a background thread when a notification is clicked, with its "action" argument.</summary>
    public event EventHandler<string>? Invoked;

    public void Register()
    {
        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                return;
            }

            AppNotificationManager.Default.NotificationInvoked += OnInvoked;
            AppNotificationManager.Default.Register(
                "SweetHide",
                new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png")));
            _registered = true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            _registered = false;
        }
    }

    public void Show(string title, string message, string action)
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            var notification = new AppNotificationBuilder()
                .AddArgument("action", action)
                .AddText(title)
                .AddText(message)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Notifications turned off for this app, or Focus Assist: fine.
        }
    }

    public void Dispose()
    {
        if (_registered)
        {
            AppNotificationManager.Default.NotificationInvoked -= OnInvoked;
            try
            {
                AppNotificationManager.Default.Unregister();
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Already gone.
            }
        }
    }

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) =>
        Invoked?.Invoke(this, args.Arguments.TryGetValue("action", out var action) ? action : string.Empty);
}
