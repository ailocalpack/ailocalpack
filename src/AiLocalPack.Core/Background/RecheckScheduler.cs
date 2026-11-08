namespace AiLocalPack.Core.Background;

/// <summary>
/// When the background re-check of installed packs runs while the app sits in the tray (task 39): every
/// <see cref="Period"/> since the last run, and <see cref="Quiet"/> after the last Steam app manifest change
/// (<see cref="Steam.SteamManifestWatcher"/>; Steam rewrites a manifest many times during an update, so the check waits
/// until it stays quiet). Runs never overlap: a change during a run makes one more run after it. Time comes from the
/// <see cref="TimeProvider"/>; the host calls <see cref="TickAsync"/> on a timer (tests call it after moving a fake clock).
/// </summary>
public sealed class RecheckScheduler
{
    /// <summary>Default period: a game updated while nothing was watched (Steam not running, another store) is found within it.</summary>
    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromHours(6);

    /// <summary>Default quiet time after the last manifest change.</summary>
    public static readonly TimeSpan DefaultQuiet = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _time;
    private readonly Func<CancellationToken, Task> _run;
    private readonly Lock _lock = new();
    private DateTimeOffset _lastRun;
    private DateTimeOffset? _lastChange;
    private bool _running;

    /// <param name="run">The re-check (a library refresh, which re-checks the packs); its exceptions are swallowed by <see cref="TickAsync"/>.</param>
    public RecheckScheduler(TimeProvider time, Func<CancellationToken, Task> run, TimeSpan? period = null, TimeSpan? quiet = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(run);
        _time = time;
        _run = run;
        Period = period ?? DefaultPeriod;
        Quiet = quiet ?? DefaultQuiet;
        _lastRun = time.GetUtcNow();
    }

    public TimeSpan Period { get; }

    public TimeSpan Quiet { get; }

    /// <summary>The last run's start (at first, the scheduler's creation: start-up checks just ran).</summary>
    public DateTimeOffset LastRun
    {
        get
        {
            lock (_lock)
            {
                return _lastRun;
            }
        }
    }

    /// <summary>A Steam app manifest changed (a game updated, installed, verified).</summary>
    public void Notify()
    {
        lock (_lock)
        {
            _lastChange = _time.GetUtcNow();
        }
    }

    /// <summary>Whether a run is due now: no run going on and a quiet change pending or the period over.</summary>
    public bool IsDue
    {
        get
        {
            lock (_lock)
            {
                return !_running && IsDueLocked(_time.GetUtcNow());
            }
        }
    }

    /// <summary>Runs the re-check when due; <c>true</c> when it ran. Never throws (but for cancellation).</summary>
    public async Task<bool> TickAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (_running || !IsDueLocked(now))
            {
                return false;
            }

            _running = true;
            _lastRun = now;
            // Changes from now on make another run; the ones before are covered by this one.
            _lastChange = null;
        }

        try
        {
            await _run(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The run logs its own failures; the next period tries again.
        }
        finally
        {
            lock (_lock)
            {
                _running = false;
            }
        }

        return true;
    }

    private bool IsDueLocked(DateTimeOffset now) =>
        (_lastChange is { } change && now - change >= Quiet) || now - _lastRun >= Period;
}
