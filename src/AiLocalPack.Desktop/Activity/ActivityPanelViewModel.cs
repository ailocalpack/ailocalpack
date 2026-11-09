using System.Collections.ObjectModel;
using System.ComponentModel;
using AiLocalPack.Core.Activity;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Install;
using AiLocalPack.Desktop.Covers;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Install;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.Startup;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.GamePage;
using AiLocalPack.Desktop.ViewModels.Settings;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.Activity;

/// <summary>
/// The Activity panel under 🔔 (design §5, task 38): <i>Needs your decision</i> (cards with a tone stripe, the fixing
/// action, 🕑 <i>Decide later</i>), <i>Running</i> (the install in progress; the startup checks while they run), the feed
/// by day (<i>Today</i>, <i>Yesterday</i>, older days behind <i>Show N more</i>; unread dot, ✕), <c>Read all</c> (never
/// clears decisions), ⚙ → Settings → General. A row opens its game's page. The 🔔 badge: amber with the number of
/// decisions, otherwise running + unread. Follows the <see cref="ActivityStore"/> (on the UI thread) and the UI language.
/// </summary>
public sealed partial class ActivityPanelViewModel : ObservableObject
{
    private readonly ActivityStore _store;
    private readonly InstallFlow _install;
    private readonly GameLibrary _library;
    private readonly PackIndex _packIndex;
    private readonly InstalledPackStore _installed;
    private readonly INavigationService _navigation;
    private readonly IPageFactory _pages;
    private readonly IDialogService _dialogs;
    private readonly ICoverLoader _covers;
    private readonly GenerationService _jobs;
    private readonly Localizer _localizer;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly Dictionary<GameId, Task<Bitmap?>> _coverLoads = [];
    private string? _eventsShown;
    private bool _refreshQueued;

    public ActivityPanelViewModel(
        ActivityRecorder recorder,
        InstallFlow install,
        GameLibrary library,
        PackIndex packIndex,
        InstalledPackStore installed,
        INavigationService navigation,
        IPageFactory pages,
        IDialogService dialogs,
        ICoverLoader covers,
        GenerationService jobs,
        StartupProgress startup,
        Localizer localizer,
        IUiDispatcher ui,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        _store = recorder.Store;
        _install = install;
        _library = library;
        _packIndex = packIndex;
        _installed = installed;
        _navigation = navigation;
        _pages = pages;
        _dialogs = dialogs;
        _covers = covers;
        _jobs = jobs;
        _localizer = localizer;
        _ui = ui;
        _time = time;
        Startup = startup;
        _store.Changed += (_, _) => QueueRefresh();
        _packIndex.Changed += (_, _) => QueueRefresh(events: true);
        _localizer.CultureChanged += (_, _) => Refresh(events: true);
        Startup.PropertyChanged += OnStartupChanged;
        Refresh(events: true);
    }

    /// <summary>Asks the view to close the panel (a row or ⚙ opened a page).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The startup checks: shown under <i>Running</i> from the first one started until every one has finished.</summary>
    public StartupProgress Startup { get; }

    public bool ShowStartupChecks => Startup.IsBackgroundRunning
        || (Startup.BackgroundFinished > 0 && Startup.BackgroundFinished < Startup.Background.Count);

    public ObservableCollection<ActivityRowViewModel> Decisions { get; } = [];

    public ObservableCollection<ActivityRowViewModel> Running { get; } = [];

    /// <summary>Today, Yesterday, then the older days once <see cref="ShowOlder"/> was pressed.</summary>
    public ObservableCollection<ActivityDayViewModel> Days { get; } = [];

    public bool HasDecisions => Decisions.Count > 0;

    public bool HasRunning => Running.Count > 0 || ShowStartupChecks;

    public string DecisionCount => Decisions.Count.ToString(_localizer.Culture);

    public string RunningCount => (Running.Count + (ShowStartupChecks ? 1 : 0)).ToString(_localizer.Culture);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOlder))]
    public partial bool IsOlderShown { get; set; }

    /// <summary>Feed events older than yesterday.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOlder), nameof(ShowMoreText))]
    public partial int OlderCount { get; set; }

    public bool HasOlder => !IsOlderShown && OlderCount > 0;

    public string ShowMoreText => _localizer.Plural(nameof(Strings.Activity_ShowMore_One), OlderCount);

    [ObservableProperty]
    public partial bool HasUnread { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    /// <summary>The 🔔 badge.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge), nameof(IsBadgeAttention), nameof(BadgeText), nameof(BellName))]
    public partial ActivityBadge Badge { get; set; } = ActivityBadge.Of(0, 0, 0);

    public bool HasBadge => Badge.Tone != ActivityBadgeTone.None;

    public bool IsBadgeAttention => Badge.Tone == ActivityBadgeTone.Attention;

    public string BadgeText => Badge.Count > 99 ? "99+" : Badge.Count.ToString(_localizer.Culture);

    /// <summary>🔔's screen reader name: checks running, decisions waiting, or the count.</summary>
    public string BellName => ActivityTexts.BellName(Badge, Startup.IsBackgroundRunning, _localizer);

    /// <summary>The panel opened: today may have become yesterday since the last look.</summary>
    public void OnOpened() => Refresh(events: true);

    [RelayCommand]
    private void ReadAll() => _store.MarkAllRead();

    [RelayCommand]
    private void ShowOlder()
    {
        IsOlderShown = true;
        Refresh(events: true);
    }

    /// <summary>⚙: Settings → General, where the notification toggles are.</summary>
    [RelayCommand]
    private void OpenSettings()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        _navigation.NavigateTo(_pages.Create<SettingsViewModel>(SettingsSectionId.General));
    }

    private void QueueRefresh(bool events = false)
    {
        // Progress comes often and from pool threads: one refresh per UI turn is enough.
        if (events)
        {
            _eventsShown = null;
        }

        lock (_coverLoads)
        {
            if (_refreshQueued)
            {
                return;
            }

            _refreshQueued = true;
        }

        _ui.Post(() =>
        {
            lock (_coverLoads)
            {
                _refreshQueued = false;
            }

            Refresh(events: false);
        });
    }

    private void Refresh(bool events)
    {
        var all = _store.Events;
        var signature = string.Join("|", all.Select(e => $"{e.Id}:{e.IsDecision}:{e.IsRead}"));
        if (events || signature != _eventsShown)
        {
            _eventsShown = signature;
            RefreshEvents(all);
        }

        RefreshRunning();
        Badge = _store.Badge;
        IsEmpty = all.Count == 0 && !HasRunning;
        OnPropertyChanged(nameof(BellName));
        OnPropertyChanged(nameof(ShowMoreText));
    }

    private void RefreshEvents(IReadOnlyList<ActivityEvent> all)
    {
        Decisions.Clear();
        foreach (var activity in all.Where(e => e.IsDecision))
        {
            Decisions.Add(EventRow(activity, ActivityRowKind.Decision));
        }

        var zone = _time.LocalTimeZone;
        var feed = all.Where(e => e.IsInFeed).ToList();
        var days = ActivityFeed.ByDay(feed, _time.GetUtcNow(), zone);
        Days.Clear();
        foreach (var day in days.Where(d => d.Kind != ActivityDayKind.Earlier || IsOlderShown))
        {
            var unread = day.Kind == ActivityDayKind.Today ? day.Events.Count(e => !e.IsRead) : 0;
            Days.Add(new ActivityDayViewModel(
                ActivityTexts.Day(day, _localizer),
                unread > 0 ? _localizer.Plural(nameof(Strings.Activity_Day_New_One), unread) : null,
                [.. day.Events.Select(e => EventRow(e, ActivityRowKind.Event))]));
        }

        OlderCount = days.Where(d => d.Kind == ActivityDayKind.Earlier).Sum(d => d.Events.Count);
        HasUnread = feed.Any(e => !e.IsRead);
        OnPropertyChanged(nameof(HasDecisions));
        OnPropertyChanged(nameof(DecisionCount));
    }

    private void RefreshRunning()
    {
        var tasks = _store.Tasks;
        for (var i = Running.Count - 1; i >= 0; i--)
        {
            if (tasks.All(t => t.Id != Running[i].Id))
            {
                Running.RemoveAt(i);
            }
        }

        foreach (var task in tasks)
        {
            var row = Running.FirstOrDefault(r => r.Id == task.Id);
            if (row is null)
            {
                row = new ActivityRowViewModel(task.Id, ActivityRowKind.Running, task.Game, task.GameName ?? _localizer[nameof(Strings.Activity_App)],
                    ActivityTone.Info, _localizer[nameof(Strings.Activity_Section_Running)], () => OpenGame(task.Game, null))
                {
                    PauseCommand = task.Pause is { } pause ? new RelayCommand(pause) : null,
                    CancelCommand = task.Cancel is { } cancel ? new RelayCommand(cancel) : null,
                };
                LoadCover(row);
                Running.Add(row);
            }

            var percent = (int)Math.Round((task.Fraction ?? 0) * 100);
            row.Title = task.Title;
            row.Percent = percent;
            row.Time = task.Fraction is null ? null : $"{percent} %";
            row.Step = task.Step;
        }

        OnPropertyChanged(nameof(HasRunning));
        OnPropertyChanged(nameof(RunningCount));
    }

    private ActivityRowViewModel EventRow(ActivityEvent activity, ActivityRowKind kind)
    {
        var game = activity.Game is { } id ? _library.Catalog.Find(id) : null;
        var later = activity.Kind == ActivityEventKind.PackIncompatible ? LaterRevision(activity) : null;
        var row = new ActivityRowViewModel(
            activity.Id,
            kind,
            activity.Game,
            game?.Name ?? activity.GameName ?? _localizer[nameof(Strings.Activity_App)],
            activity.Tone,
            ActivityTexts.Tone(activity.Tone, _localizer),
            () => Open(activity))
        {
            HideCommand = kind == ActivityRowKind.Event ? new RelayCommand(() => _store.Hide(activity.Id)) : null,
            DecideLaterCommand = kind == ActivityRowKind.Decision ? new RelayCommand(() => _store.DecideLater(activity.Id)) : null,
        };
        row.Title = ActivityTexts.Title(activity, _localizer);
        row.Detail = ActivityTexts.Detail(activity, later?.Revision, _localizer, _time);
        row.Time = ActivityTexts.Time(activity.Time, _time.LocalTimeZone, _localizer);
        row.IsUnread = kind == ActivityRowKind.Event && !activity.IsRead;
        row.Actions = game is null ? [] : Actions(activity, game, later, kind == ActivityRowKind.Decision);
        LoadCover(row);
        return row;
    }

    /// <summary>
    /// The row's buttons; none when the game left the library. A decision set aside or settled keeps only what still
    /// informs (<c>Log</c>): its fix belongs to the card.
    /// </summary>
    private List<ActivityActionViewModel> Actions(ActivityEvent activity, Game game, Core.Registry.RegistryPack? later, bool isDecision)
    {
        var actions = new List<ActivityActionViewModel>();
        if (!isDecision && ActivityEvents.IsDecisionKind(activity.Kind))
        {
            if (activity.Kind == ActivityEventKind.AutoReapplyFailed)
            {
                actions.Add(Action(nameof(Strings.Activity_Action_Log), null, false, () => ShowDetails(activity, null)));
            }

            return actions;
        }

        switch (activity.Kind)
        {
            case ActivityEventKind.AutoReapplyFailed:
                if (activity.CanRetry)
                {
                    actions.Add(Action(nameof(Strings.Install_Toast_Retry), "IconRefresh", true, () => _ = _install.ReapplyAsync(Fresh(game))));
                }

                actions.Add(Action(nameof(Strings.Activity_Action_Log), null, false, () => ShowDetails(activity, () => _ = _install.ReapplyAsync(Fresh(game)))));
                break;
            case ActivityEventKind.PackIncompatible when later is not null:
                actions.Add(new ActivityActionViewModel(
                    _localizer.Format(nameof(Strings.Activity_Action_Update), later.Revision), "IconRefresh", true,
                    new RelayCommand(() => _ = _install.InstallAsync(Fresh(game), later))));
                break;
            case ActivityEventKind.PackIncompatible:
                actions.Add(Action(nameof(Strings.Activity_Action_Translate), "IconSparkles", true, () =>
                {
                    _store.Resolve(activity.Id);
                    OpenGame(activity.Game, GamePageTab.Generation);
                }));
                break;
            case ActivityEventKind.GenerationPaused:
                actions.Add(Action(nameof(Strings.Activity_Action_ResumeNow), "IconPlay", true, () =>
                {
                    _ = _jobs.ResumeAsync(Fresh(game));
                    OpenGame(activity.Game, GamePageTab.Generation);
                }));
                break;
            case ActivityEventKind.PackNotApplied:
                actions.Add(Action(nameof(Strings.Activity_Action_Reapply), "IconRefresh", true, () => _ = _install.ReapplyAsync(Fresh(game))));
                break;
            case ActivityEventKind.PackAutoReapplied when _installed.Find(game.Id) is not null:
                actions.Add(Action(nameof(Strings.Install_Toast_Undo), "IconUndo", false, () => _install.Uninstall(Fresh(game))));
                break;
            case ActivityEventKind.ReapplyFailed:
                actions.Add(Action(nameof(Strings.Install_Toast_Details), null, false, () => ShowDetails(activity, () => _ = _install.ReapplyAsync(Fresh(game)))));
                break;
            case ActivityEventKind.InstallFailed or ActivityEventKind.UninstallFailed:
                actions.Add(Action(nameof(Strings.Install_Toast_Details), null, false, () => ShowDetails(activity, null)));
                break;
        }

        return actions;

        ActivityActionViewModel Action(string key, string? icon, bool primary, Action run) =>
            new(_localizer[key], icon, primary, new RelayCommand(() =>
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
                run();
            }));
    }

    /// <summary>The registry's newer revision of the installed pack (same ID and language), else <c>null</c>.</summary>
    private Core.Registry.RegistryPack? LaterRevision(ActivityEvent activity)
    {
        if (activity.Game is not { } game || _installed.Find(game) is not { } record || record.PackId != activity.PackId)
        {
            return null;
        }

        return _packIndex.Current?.Index?.PacksFor(game, record.Language)
            .Where(p => p.Id == record.PackId && p.Revision > record.Revision)
            .MaxBy(p => p.Revision);
    }

    private void ShowDetails(ActivityEvent activity, Action? retry)
    {
        var kind = activity.Kind switch
        {
            ActivityEventKind.UninstallFailed => InstallOperationKind.Uninstall,
            ActivityEventKind.InstallFailed => InstallOperationKind.Install,
            _ => InstallOperationKind.Reapply,
        };
        _dialogs.Show<InstallDetailsDialogViewModel>(new InstallDetailsRequest(
            new InstallDetails(kind, activity.PackTitle ?? activity.PackId ?? string.Empty, activity.Detail ?? string.Empty, activity.FilesRestored, activity.Log, activity.CanRetry),
            retry));
    }

    /// <summary>Opens an event like a click on its row (an OS notification clicked, task 39); nothing for an unknown ID.</summary>
    public void OpenEvent(string id)
    {
        if (_store.Find(id) is { } activity)
        {
            Open(activity);
        }
    }

    /// <summary>The registry's newer revision of an incompatible pack's event, for its detail line.</summary>
    public int? LaterRevisionOf(ActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        return activity.Kind == ActivityEventKind.PackIncompatible ? LaterRevision(activity)?.Revision : null;
    }

    private void Open(ActivityEvent activity)
    {
        _store.MarkRead(activity.Id);
        var generation = activity.Kind == ActivityEventKind.GenerationPaused
            || (activity.Kind == ActivityEventKind.PackIncompatible && LaterRevision(activity) is null);
        OpenGame(activity.Game, generation ? GamePageTab.Generation : GamePageTab.Packs);
    }

    private void OpenGame(GameId? id, GamePageTab? tab)
    {
        if (id is null || _library.Catalog.Find(id) is not { } game)
        {
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
        _navigation.NavigateTo(_pages.Create<GamePageViewModel>(game, tab ?? GamePageTab.Default));
    }

    private Game Fresh(Game game) => _library.Catalog.Find(game.Id) ?? game;

    /// <summary>The game's cover thumbnail, loaded once per game.</summary>
    private void LoadCover(ActivityRowViewModel row)
    {
        if (row.Game is not { } id || _library.Catalog.Find(id) is not { } game)
        {
            return;
        }

        if (!_coverLoads.TryGetValue(id, out var load))
        {
            load = LoadCoverAsync(game);
            _coverLoads[id] = load;
        }

        if (load.IsCompletedSuccessfully)
        {
            row.Cover = load.Result;
            return;
        }

        _ = load.ContinueWith(t => _ui.Post(() => row.Cover = t.Result), TaskScheduler.Default);
    }

    private async Task<Bitmap?> LoadCoverAsync(Game game)
    {
        try
        {
            return (await _covers.LoadAsync(game).ConfigureAwait(false)) is { IsIcon: false } image ? image.Bitmap : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    private void OnStartupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StartupProgress.IsBackgroundRunning) or nameof(StartupProgress.BackgroundFinished))
        {
            OnPropertyChanged(nameof(ShowStartupChecks));
            OnPropertyChanged(nameof(HasRunning));
            OnPropertyChanged(nameof(RunningCount));
            OnPropertyChanged(nameof(BellName));
            IsEmpty = _store.Events.Count == 0 && !HasRunning;
        }
    }
}
