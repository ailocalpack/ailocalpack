using System.Globalization;
using System.Text.Json;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Core.Activity;

/// <summary>
/// What the Activity panel shows (design §5): decisions waiting for the player, running tasks and the feed. Events are
/// persisted in <c>&lt;data&gt;/activity.json</c> (loaded on first use, saved after every change, atomic); tasks live in
/// memory only. The feed keeps the newest <see cref="MaxFeed"/> events (decisions are never dropped). A corrupted file is
/// moved aside as <c>activity.json.&lt;utc&gt;.bak</c>; an unreadable one gives an empty store that isn't written this
/// session. A failed save is logged and kept in memory. Thread-safe; <see cref="Changed"/> is raised outside the lock, on
/// the caller's thread.
/// </summary>
public sealed class ActivityStore
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Feed events kept (hidden ones count too: they only keep a finding from coming back).</summary>
    public const int MaxFeed = 200;

    private readonly Lock _lock = new();
    private readonly string _file;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private List<ActivityEvent>? _events;
    private List<ActivityTask> _tasks = [];
    private bool _canSave = true;

    public ActivityStore(AppPaths paths, TimeProvider? time = null, ILogger<ActivityStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _file = paths.ActivityFile;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Raised after any change of events or tasks.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised after <see cref="Add"/> added a new event (before <see cref="Changed"/>), on the adding thread.</summary>
    public event EventHandler<ActivityEvent>? Added;

    /// <summary>Every visible event (decisions and feed, not hidden), newest first.</summary>
    public IReadOnlyList<ActivityEvent> Events
    {
        get
        {
            lock (_lock)
            {
                return [.. Loaded().Where(e => !e.IsHidden)];
            }
        }
    }

    /// <summary><i>Needs your decision</i>, newest first.</summary>
    public IReadOnlyList<ActivityEvent> Decisions => [.. Events.Where(e => e.IsDecision)];

    /// <summary>The feed, newest first.</summary>
    public IReadOnlyList<ActivityEvent> Feed => [.. Events.Where(e => e.IsInFeed)];

    /// <summary>Running tasks, oldest first.</summary>
    public IReadOnlyList<ActivityTask> Tasks
    {
        get
        {
            lock (_lock)
            {
                return _tasks;
            }
        }
    }

    /// <summary>The 🔔 badge (<see cref="ActivityBadge.Of"/>).</summary>
    public ActivityBadge Badge
    {
        get
        {
            lock (_lock)
            {
                var events = Loaded().Where(e => !e.IsHidden).ToList();
                return ActivityBadge.Of(events.Count(e => e.IsDecision), _tasks.Count, events.Count(e => e.IsInFeed && !e.IsRead));
            }
        }
    }

    public ActivityEvent? Find(string id)
    {
        lock (_lock)
        {
            return Loaded().FirstOrDefault(e => e.Id == id);
        }
    }

    /// <summary>
    /// Adds an event; <c>false</c> (nothing added) when one with the same <see cref="ActivityEvent.Key"/> is known already,
    /// whatever happened to it since (set aside, hidden).
    /// </summary>
    public bool Add(ActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        lock (_lock)
        {
            var events = Loaded();
            if (events.Any(e => e.Id == activity.Id || (activity.Key is not null && e.Key == activity.Key)))
            {
                return false;
            }

            events.Insert(0, activity);
            events.Sort((a, b) => b.Time.CompareTo(a.Time));
            Trim(events);
            Save();
        }

        Added?.Invoke(this, activity);
        RaiseChanged();
        return true;
    }

    /// <summary>Marks a feed event read (opened). A decision stays as it is.</summary>
    public void MarkRead(string id) => Change(id, e => e.IsDecision || e.IsRead ? e : e with { IsRead = true });

    /// <summary><c>Read all</c>: every feed event read; decisions are never cleared by it.</summary>
    public void MarkAllRead()
    {
        lock (_lock)
        {
            var events = Loaded();
            if (!events.Any(e => e.IsInFeed && !e.IsRead))
            {
                return;
            }

            for (var i = 0; i < events.Count; i++)
            {
                if (events[i].IsInFeed && !events[i].IsRead)
                {
                    events[i] = events[i] with { IsRead = true };
                }
            }

            Save();
        }

        RaiseChanged();
    }

    /// <summary>✕ on a feed event. A decision can't be hidden: it is set aside first (<see cref="DecideLater"/>).</summary>
    public void Hide(string id) => Change(id, e => e.IsDecision ? e : e with { IsHidden = true, IsRead = true });

    /// <summary>🕑 <i>Decide later</i>: the decision moves into the feed, read.</summary>
    public void DecideLater(string id) => Change(id, e => e.IsDecision ? e with { IsDecision = false, IsRead = true } : e);

    /// <summary>The player acted on a decision (pressed its action): it moves into the feed, read.</summary>
    public void Resolve(string id) => DecideLater(id);

    /// <summary>
    /// Settles every decision of the game matching <paramref name="which"/> (the pack was installed, re-applied or
    /// uninstalled again, so its old problems no longer apply).
    /// </summary>
    public void ResolveGame(GameId game, Func<ActivityEvent, bool> which)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(which);
        lock (_lock)
        {
            var events = Loaded();
            var changed = false;
            for (var i = 0; i < events.Count; i++)
            {
                if (events[i].IsDecision && events[i].Game == game && which(events[i]))
                {
                    events[i] = events[i] with { IsDecision = false, IsRead = true };
                    changed = true;
                }
            }

            if (!changed)
            {
                return;
            }

            Save();
        }

        RaiseChanged();
    }

    /// <summary>Adds a running task, or replaces the one with the same ID (progress).</summary>
    public void SetTask(ActivityTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_lock)
        {
            var index = _tasks.FindIndex(t => t.Id == task.Id);
            _tasks = index < 0 ? [.. _tasks, task] : [.. _tasks[..index], task, .. _tasks[(index + 1)..]];
        }

        RaiseChanged();
    }

    /// <summary>The task ended (its outcome, if any, is an event of its own).</summary>
    public void EndTask(string id)
    {
        lock (_lock)
        {
            if (_tasks.All(t => t.Id != id))
            {
                return;
            }

            _tasks = [.. _tasks.Where(t => t.Id != id)];
        }

        RaiseChanged();
    }

    private void Change(string id, Func<ActivityEvent, ActivityEvent> change)
    {
        lock (_lock)
        {
            var events = Loaded();
            var index = events.FindIndex(e => e.Id == id);
            if (index < 0)
            {
                return;
            }

            var changed = change(events[index]);
            if (changed == events[index])
            {
                return;
            }

            events[index] = changed;
            Save();
        }

        RaiseChanged();
    }

    /// <summary>Drops the oldest feed events beyond <see cref="MaxFeed"/>; decisions stay.</summary>
    private static void Trim(List<ActivityEvent> events)
    {
        var feed = 0;
        events.RemoveAll(e => !e.IsDecision && ++feed > MaxFeed);
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private List<ActivityEvent> Loaded() => _events ??= Load();

    private List<ActivityEvent> Load()
    {
        string text;
        try
        {
            if (!File.Exists(_file))
            {
                return [];
            }

            text = File.ReadAllText(_file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(e, "Can't read the activity file {File}; nothing is saved this session", _file);
            _canSave = false;
            return [];
        }

        ActivityFile? file;
        try
        {
            file = JsonSerializer.Deserialize(text, ActivityJsonContext.Default.ActivityFile);
        }
        catch (JsonException e)
        {
            MoveAside(e);
            return [];
        }

        if (file is null)
        {
            MoveAside(null);
            return [];
        }

        if (file.SchemaVersion > CurrentSchemaVersion)
        {
            _logger.LogWarning("{File} has schema {Version}, newer than this app; it is read but not written", _file, file.SchemaVersion);
            _canSave = false;
        }

        var events = new List<ActivityEvent>();
        foreach (var entry in file.Events ?? [])
        {
            if (FromJson(entry) is { } activity && events.All(e => e.Id != activity.Id))
            {
                events.Add(activity);
            }
            else
            {
                _logger.LogWarning("Skipping a broken or unknown activity entry in {File}", _file);
            }
        }

        events.Sort((a, b) => b.Time.CompareTo(a.Time));
        return events;
    }

    private void MoveAside(Exception? e)
    {
        var backup = $"{_file}.{_time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.bak";
        _logger.LogError(e, "The activity file {File} is damaged; moved to {Backup}", _file, backup);
        try
        {
            File.Move(_file, backup, overwrite: true);
        }
        catch (Exception move) when (move is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(move, "Can't move {File} aside; nothing is saved this session", _file);
            _canSave = false;
        }
    }

    private void Save()
    {
        if (!_canSave)
        {
            return;
        }

        try
        {
            var file = new ActivityFile(CurrentSchemaVersion, [.. Loaded().Select(ToJson)]);
            AtomicFile.WriteAllBytes(_file, JsonSerializer.SerializeToUtf8Bytes(file, ActivityJsonContext.Default.ActivityFile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(e, "Saving the activity file {File} failed; kept in memory", _file);
        }
    }

    private static ActivityEventJson ToJson(ActivityEvent e) => new(
        e.Id,
        JsonNamingPolicy.CamelCase.ConvertName(e.Kind.ToString()),
        e.Time.ToUniversalTime(),
        e.Game?.ToString(),
        e.GameName,
        e.PackId,
        e.PackTitle,
        e.Revision,
        e.Detail,
        e.Log.Count == 0 ? null : e.Log,
        e.FilesRestored,
        e.CanRetry,
        e.Key,
        e.IsDecision,
        e.IsRead,
        e.IsHidden);

    private static ActivityEvent? FromJson(ActivityEventJson? json)
    {
        if (json is null || string.IsNullOrEmpty(json.Id) || json.Time == default
            || !Enum.TryParse<ActivityEventKind>(json.Kind, ignoreCase: true, out var kind) || !Enum.IsDefined(kind)
            || json.Kind!.Any(char.IsDigit))
        {
            return null;
        }

        GameId? game = null;
        if (json.Game is not null && !GameId.TryParse(json.Game, out game))
        {
            return null;
        }

        return new ActivityEvent
        {
            Id = json.Id,
            Kind = kind,
            Time = json.Time,
            Game = game,
            GameName = json.GameName,
            PackId = json.Pack,
            PackTitle = json.PackTitle,
            Revision = json.Revision,
            Detail = json.Detail,
            Log = json.Log?.Where(l => l is not null).ToList() ?? [],
            FilesRestored = json.FilesRestored,
            CanRetry = json.CanRetry,
            Key = json.Key,
            IsDecision = json.Decision,
            IsRead = json.Read,
            IsHidden = json.Hidden,
        };
    }
}
