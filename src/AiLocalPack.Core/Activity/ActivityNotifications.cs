using AiLocalPack.Core.Settings;

namespace AiLocalPack.Core.Activity;

/// <summary>The General → Notifications toggle an event falls under.</summary>
public enum NotificationCategory
{
    /// <summary><see cref="AppSettings.NotifyNeedsDecision"/>: a decision waits for the player.</summary>
    NeedsDecision,

    /// <summary><see cref="AppSettings.NotifyTaskFinished"/>: an install, re-apply or uninstall ended (done or failed).</summary>
    TaskFinished,

    /// <summary><see cref="AppSettings.NotifyCommunity"/>: reviews, confirmations on the player's packs (tasks 62–64).</summary>
    Community,
}

/// <summary>
/// Which Activity events become OS notifications (task 39, design §5): the same events the panel shows, while the app's
/// window isn't in front (closed to the tray, minimized, another app active) and the tray is on (notifications need the
/// app running in the background, as the Notifications group says), per the category's toggle.
/// </summary>
public static class ActivityNotifications
{
    public static NotificationCategory CategoryOf(ActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        return activity.IsDecision ? NotificationCategory.NeedsDecision : NotificationCategory.TaskFinished;
    }

    /// <param name="windowActive">The app's window is shown and active: the player sees the panel and the toasts.</param>
    public static bool ShouldNotify(ActivityEvent activity, AppSettings settings, bool windowActive)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(settings);
        if (windowActive || !settings.RunInBackground || activity.IsHidden)
        {
            return false;
        }

        return CategoryOf(activity) switch
        {
            NotificationCategory.NeedsDecision => settings.NotifyNeedsDecision,
            NotificationCategory.TaskFinished => settings.NotifyTaskFinished,
            _ => settings.NotifyCommunity,
        };
    }
}
