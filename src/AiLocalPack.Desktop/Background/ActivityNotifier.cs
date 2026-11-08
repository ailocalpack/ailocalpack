using AiLocalPack.Core.Activity;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Activity;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Services;

namespace AiLocalPack.Desktop.Background;

/// <summary>
/// Sends new Activity events to the OS as notifications (task 39, design §5 footer) when
/// <see cref="ActivityNotifications.ShouldNotify"/> says so: tray on, window not in front, the category's toggle on.
/// Title = the event's panel title, body = the game (+ the detail line). A click shows the window and opens the event
/// like a click on its row in the panel (game page, Packs or Generation tab; feed event marked read).
/// </summary>
public sealed class ActivityNotifier
{
    private readonly ActivityStore _store;
    private readonly ISystemNotifications _notifications;
    private readonly SettingsStore _settings;
    private readonly IAppWindow _window;
    private readonly ActivityPanelViewModel _panel;
    private readonly IUiDispatcher _dispatcher;
    private readonly Localizer _localizer;

    public ActivityNotifier(
        ActivityStore store,
        ISystemNotifications notifications,
        SettingsStore settings,
        IAppWindow window,
        ActivityPanelViewModel panel,
        IUiDispatcher dispatcher,
        Localizer localizer)
    {
        _store = store;
        _notifications = notifications;
        _settings = settings;
        _window = window;
        _panel = panel;
        _dispatcher = dispatcher;
        _localizer = localizer;
        _store.Added += (_, activity) => OnAdded(activity);
        _notifications.Activated += (_, id) => OnActivated(id);
    }

    /// <summary>The notification of the last event shown (tests and the manual check wait on it).</summary>
    public Task? LastShown { get; private set; }

    private void OnAdded(ActivityEvent activity)
    {
        if (!ActivityNotifications.ShouldNotify(activity, _settings.Current, _window.IsActive))
        {
            return;
        }

        var detail = ActivityTexts.Detail(activity, _panel.LaterRevisionOf(activity), _localizer);
        var body = string.Join(" · ", new[] { activity.GameName, detail }.Where(s => !string.IsNullOrWhiteSpace(s)));
        LastShown = _notifications.ShowAsync(new SystemNotification(activity.Id, ActivityTexts.Title(activity, _localizer), body));
    }

    private void OnActivated(string id) => _dispatcher.Post(() =>
    {
        _window.Show();
        _panel.OpenEvent(id);
    });
}
