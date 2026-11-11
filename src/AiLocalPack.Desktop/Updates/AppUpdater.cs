using AiLocalPack.Core.Activity;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Updates;

/// <summary>Where the app's own update stands (Settings → About, design §8).</summary>
public enum AppUpdateStatus
{
    /// <summary>This copy can't update itself (not installed from a release).</summary>
    Off,

    /// <summary>Not checked yet.</summary>
    Idle,

    Checking,

    /// <summary>The last check found nothing newer (<see cref="AppUpdater.CheckedAt"/>).</summary>
    UpToDate,

    /// <summary>A newer version is being downloaded in the background (<see cref="AppUpdater.Percent"/>).</summary>
    Downloading,

    /// <summary>Downloaded: <c>Restart to update</c> (it also goes in on the next start).</summary>
    Ready,

    /// <summary>The updater runs and the app quits.</summary>
    Restarting,

    /// <summary>The last check, download or restart failed (<see cref="AppUpdater.Error"/>); checked again later.</summary>
    Failed,
}

/// <summary>What keeps the app from restarting for an update right now.</summary>
public enum AppRestartBlock
{
    None,

    /// <summary>A pack is being installed, re-applied or uninstalled: never cut short.</summary>
    Installing,

    /// <summary>A generation or a publish runs: the player confirms (a generation can be resumed after the restart).</summary>
    Working,
}

/// <summary>
/// The app's self-update (task 67): checks the release feed at start and every <see cref="CheckInterval"/> while the app
/// runs (window or tray), downloads a newer version in the background and then waits for <c>Restart to update</c>
/// (Activity feed event "AI Local Pack X is ready", once per version). Thread-safe; <see cref="Changed"/> may come from
/// any thread. Never throws: problems become <see cref="AppUpdateStatus.Failed"/> with the reason logged.
/// </summary>
public sealed class AppUpdater : IDisposable
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly IAppUpdateSource _source;
    private readonly IAppQuit _quit;
    private readonly ActivityStore _activity;
    private readonly TimeProvider _time;
    private readonly ILogger<AppUpdater> _logger;
    private readonly Lock _lock = new();
    private ITimer? _timer;
    private AppUpdateOffer? _offer;

    public AppUpdater(IAppUpdateSource source, IAppQuit quit, ActivityStore activity, TimeProvider time, ILogger<AppUpdater> logger)
    {
        _source = source;
        _quit = quit;
        _activity = activity;
        _time = time;
        _logger = logger;
        if (source.Install == AppInstallKind.Development)
        {
            Status = AppUpdateStatus.Off;
        }
        else if (source.PendingRestart is { } pending)
        {
            _offer = pending;
            Status = AppUpdateStatus.Ready;
        }
    }

    /// <summary>The state changed (any thread).</summary>
    public event EventHandler? Changed;

    public AppUpdateStatus Status { get; private set; } = AppUpdateStatus.Idle;

    public AppInstallKind Install => _source.Install;

    public string? Channel => _source.Channel;

    /// <summary>The installed version; <c>null</c> when not installed from a release.</summary>
    public string? CurrentVersion => _source.CurrentVersion;

    /// <summary>The version found (downloading, ready, restarting); <c>null</c> otherwise.</summary>
    public string? Version => Status is AppUpdateStatus.Downloading or AppUpdateStatus.Ready or AppUpdateStatus.Restarting ? _offer?.Version : null;

    /// <summary>The download's progress, 0–100.</summary>
    public int Percent { get; private set; }

    /// <summary>When the feed last answered.</summary>
    public DateTimeOffset? CheckedAt { get; private set; }

    /// <summary>Why the last attempt failed (<see cref="AppUpdateStatus.Failed"/>), in English for the log; <c>null</c> otherwise.</summary>
    public string? Error { get; private set; }

    /// <summary>The download running in the background, for tests; a finished task when none runs.</summary>
    internal Task Download { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Checks the feed; a newer version starts downloading in the background (<see cref="Download"/>). Returns once the
    /// feed answered, with the new status. Does nothing (returns the status) while off, checking, downloading, ready
    /// or restarting.
    /// </summary>
    public async Task<AppUpdateStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (Status is not (AppUpdateStatus.Idle or AppUpdateStatus.UpToDate or AppUpdateStatus.Failed))
            {
                return Status;
            }

            Set(AppUpdateStatus.Checking);
        }

        Raise();
        AppUpdateOffer? offer;
        try
        {
            offer = await _source.CheckAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Checking for app updates failed");
            Fail(e);
            return AppUpdateStatus.Failed;
        }

        lock (_lock)
        {
            CheckedAt = _time.GetUtcNow();
            if (offer is null)
            {
                Set(AppUpdateStatus.UpToDate);
            }
            else
            {
                _offer = offer;
                Percent = 0;
                Set(AppUpdateStatus.Downloading);
                _logger.LogInformation("App update {Version} found; downloading", offer.Version);
                Download = DownloadAsync(offer, cancellationToken);
            }
        }

        Raise();
        return offer is null ? AppUpdateStatus.UpToDate : AppUpdateStatus.Downloading;
    }

    /// <summary>Checks again every <see cref="CheckInterval"/> from now on (once; later calls do nothing).</summary>
    public void StartSchedule()
    {
        lock (_lock)
        {
            if (_timer is not null || Status == AppUpdateStatus.Off)
            {
                return;
            }

            _timer = _time.CreateTimer(_ => _ = CheckAsync(), null, CheckInterval, CheckInterval);
        }
    }

    /// <summary>What a restart would cut short now: an install (refused), a generation or publish (asks first).</summary>
    public AppRestartBlock RestartBlock
    {
        get
        {
            var tasks = _activity.Tasks;
            if (tasks.Any(t => t.Kind is ActivityTaskKind.Install or ActivityTaskKind.Reapply or ActivityTaskKind.Uninstall))
            {
                return AppRestartBlock.Installing;
            }

            return tasks.Any(t => t.Kind is ActivityTaskKind.Generation or ActivityTaskKind.Publish) ? AppRestartBlock.Working : AppRestartBlock.None;
        }
    }

    /// <summary>
    /// <c>Restart to update</c>: starts the updater (it waits for the app to exit) and quits. <c>false</c> when nothing is
    /// ready, an install runs, or the updater couldn't start (then <see cref="AppUpdateStatus.Failed"/>).
    /// </summary>
    public bool Restart()
    {
        AppUpdateOffer offer;
        lock (_lock)
        {
            if (Status != AppUpdateStatus.Ready || _offer is null || RestartBlock == AppRestartBlock.Installing)
            {
                return false;
            }

            offer = _offer;
        }

        try
        {
            _source.ApplyOnExit(offer);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Starting the app update {Version} failed", offer.Version);
            Fail(e);
            return false;
        }

        _logger.LogInformation("Restarting to update to {Version}", offer.Version);
        lock (_lock)
        {
            Set(AppUpdateStatus.Restarting);
        }

        Raise();
        _quit.Quit();
        return true;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private async Task DownloadAsync(AppUpdateOffer offer, CancellationToken cancellationToken)
    {
        // Off the caller's path: CheckAsync has returned before the first byte.
        await Task.Yield();
        try
        {
            await _source.DownloadAsync(offer, ReportProgress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Downloading the app update {Version} failed", offer.Version);
            Fail(e);
            return;
        }

        lock (_lock)
        {
            Percent = 100;
            Set(AppUpdateStatus.Ready);
        }

        _logger.LogInformation("App update {Version} downloaded; ready to restart", offer.Version);
        var ready = ActivityEvent.Create(ActivityEventKind.AppUpdateReady, _time.GetUtcNow()) with
        {
            Version = offer.Version,
            Key = "app-update:" + offer.Version,
        };
        _activity.Add(ready);
        Raise();
    }

    private void ReportProgress(int percent)
    {
        lock (_lock)
        {
            var clamped = Math.Clamp(percent, 0, 100);
            if (Status != AppUpdateStatus.Downloading || clamped == Percent)
            {
                return;
            }

            Percent = clamped;
        }

        Raise();
    }

    private void Fail(Exception e)
    {
        lock (_lock)
        {
            Error = e.Message;
            Set(AppUpdateStatus.Failed);
        }

        Raise();
    }

    /// <summary>Under the lock.</summary>
    private void Set(AppUpdateStatus status)
    {
        Status = status;
        if (status != AppUpdateStatus.Failed)
        {
            Error = null;
        }
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
