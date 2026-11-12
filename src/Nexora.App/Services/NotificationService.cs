using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Nexora.Core.Settings;

namespace Nexora.App.Services;

/// <summary>Local Windows notifications only. Nothing is sent over the network.</summary>
public sealed class NotificationService(ISettingsService settings, ILogger<NotificationService> logger)
{
    private bool _registered;

    public Action<string, string>? Fallback { get; set; }

    public void Initialize()
    {
        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "App notifications unavailable; tray balloons will be used instead");
        }
    }

    public void Show(string title, string message)
    {
        if (!settings.Current.NotificationsEnabled)
        {
            return;
        }

        if (_registered)
        {
            try
            {
                var notification = new AppNotificationBuilder().AddText(title).AddText(message).BuildNotification();
                AppNotificationManager.Default.Show(notification);
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Notification failed");
            }
        }

        Fallback?.Invoke(title, message);
    }

    public void Shutdown()
    {
        if (_registered)
        {
            try
            {
                AppNotificationManager.Default.Unregister();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Notification unregister failed");
            }
        }
    }
}
