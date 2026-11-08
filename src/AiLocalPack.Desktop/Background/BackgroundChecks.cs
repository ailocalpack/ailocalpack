using AiLocalPack.Core.Background;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Steam;
using AiLocalPack.Desktop.Library;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Desktop.Background;

/// <summary>The autostart command of this run; <c>null</c> = don't touch the OS entry (unknown executable, throwaway home).</summary>
public sealed record AutostartTarget(AutostartCommand? Command);

/// <summary>
/// General → Background (task 39), applied: the OS autostart entry follows
/// <see cref="AppSettings.RunInBackground"/> (at start and on every change), and while it is on the installed packs are
/// re-checked in the background: a library refresh (which re-checks them, <see cref="Install.InstalledPackMonitor"/>)
/// every <see cref="RecheckScheduler.Period"/> and soon after a Steam app manifest changed
/// (<see cref="SteamManifestWatcher"/> on the scanned libraries). With the setting off, packs are checked at start only.
/// Never throws.
/// </summary>
public sealed class BackgroundChecks : IDisposable
{
    /// <summary>How often the scheduler is asked whether a run is due.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private readonly SettingsStore _settings;
    private readonly GameLibrary _library;
    private readonly IAutostart _autostart;
    private readonly AutostartTarget _target;
    private readonly SteamManifestWatcher _watcher;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private bool _started;
    private bool? _autostartApplied;

    public BackgroundChecks(
        SettingsStore settings,
        GameLibrary library,
        IAutostart autostart,
        AutostartTarget target,
        SteamManifestWatcher watcher,
        TimeProvider time,
        ILogger<BackgroundChecks>? logger = null)
    {
        _settings = settings;
        _library = library;
        _autostart = autostart;
        _target = target;
        _watcher = watcher;
        _time = time;
        _logger = logger ?? NullLogger<BackgroundChecks>.Instance;
        Scheduler = new RecheckScheduler(time, _ => CheckNowAsync());
    }

    public RecheckScheduler Scheduler { get; }

    /// <summary>Background re-checks run (the setting is on).</summary>
    public bool IsOn => _settings.Current.RunInBackground;

    /// <summary>Starts following the setting, the libraries and the clock. Once; later calls do nothing.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        _settings.Changed += (_, settings) => SyncAutostart(settings.RunInBackground);
        _library.Progressed += (_, progress) =>
        {
            if (progress is GameLibraryScanned scanned)
            {
                Watch(scanned.Scan);
            }
        };
        _watcher.Changed += (_, path) =>
        {
            if (IsOn)
            {
                _logger.LogDebug("Steam app manifest changed: {Path}", path);
                Scheduler.Notify();
            }
        };
        if (_library.LastScan is { } scan)
        {
            Watch(scan);
        }

        SyncAutostart(_settings.Current.RunInBackground);
        _ = TickLoopAsync(_stop.Token);
    }

    /// <summary>Re-checks now (tray menu "Check for game updates"): a library refresh, then the packs.</summary>
    public Task CheckNowAsync()
    {
        _logger.LogInformation("Background re-check of installed packs");
        return _library.RefreshAsync();
    }

    /// <summary>Makes the OS entry match the setting; logs what changed or failed.</summary>
    public void SyncAutostart(bool enabled)
    {
        lock (_lock)
        {
            if (_autostartApplied == enabled)
            {
                return;
            }

            _autostartApplied = enabled;
        }

        if (_target.Command is null)
        {
            _logger.LogInformation("Start with the system: left alone ({Location}) in this run (throwaway home or unknown executable)", _autostart.Location);
            return;
        }

        try
        {
            var change = Autostart.Sync(_autostart, enabled, _target.Command);
            if (change != AutostartChange.None)
            {
                _logger.LogInformation("Start with the system: {Change} {Location}", change, _autostart.Location);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogWarning(e, "Couldn't update the start-with-the-system entry {Location}", _autostart.Location);
            lock (_lock)
            {
                _autostartApplied = null;
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private void Watch(SteamScan scan) => _watcher.Watch(scan.Libraries.Where(l => l.IsAvailable).Select(l => Path.Combine(l.Path, "steamapps")));

    private async Task TickLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TickInterval, _time);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (IsOn)
                {
                    await Scheduler.TickAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
