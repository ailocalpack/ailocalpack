using AiLocalPack.Core.Games;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Steam;
using AiLocalPack.Toolkit.AntiCheat;
using AiLocalPack.Toolkit.Engines;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Library;

/// <summary>
/// Keeps the game catalog in step with the disk: a refresh scans Steam (with the library folders the player added or
/// removed in settings), merges the result into <see cref="GameCatalog"/>, then detects engines and anti-cheats of new
/// or updated games (every game when the anti-cheat rules changed, <see cref="AntiCheatRules"/>; a new pack list with
/// other rules starts a refresh). One refresh runs at a time; games appear (catalog <see cref="GameCatalog.Changed"/>) as soon as
/// the Steam scan is merged, detection details follow.
/// </summary>
public sealed class GameLibrary
{
    private readonly SteamLibraryScanner _scanner;
    private readonly EngineDetector _engines;
    private readonly AntiCheatDetector _antiCheats;
    private readonly AntiCheatRules? _rules;
    private readonly SettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<GameLibrary> _logger;
    private readonly Lock _lock = new();
    private Task? _running;
    private Task? _queued;

    /// <param name="rules">
    /// Which anti-cheat rules the catalog was checked with; <c>null</c> = only new and updated games are checked.
    /// </param>
    public GameLibrary(
        GameCatalog catalog,
        SteamLibraryScanner scanner,
        EngineDetector engines,
        AntiCheatDetector antiCheats,
        AntiCheatRules? rules,
        SettingsStore settings,
        TimeProvider time,
        ILogger<GameLibrary> logger)
    {
        Catalog = catalog;
        _scanner = scanner;
        _engines = engines;
        _antiCheats = antiCheats;
        _rules = rules;
        _settings = settings;
        _time = time;
        _logger = logger;
        if (rules is not null)
        {
            rules.RulesChanged += (_, _) => _ = RefreshAsync();
        }
    }

    public GameCatalog Catalog { get; }

    /// <summary>The last finished Steam scan this session (libraries and their availability); <c>null</c> before the first.</summary>
    public SteamScan? LastScan { get; private set; }

    /// <summary>When <see cref="LastScan"/> finished.</summary>
    public DateTimeOffset? LastScanAt { get; private set; }

    /// <summary>A refresh is running.</summary>
    public bool IsScanning { get; private set; }

    /// <summary>At least one refresh finished this session (before that the catalog holds only what was saved last time).</summary>
    public bool HasScanned { get; private set; }

    /// <summary>Raised (on the refreshing thread) when <see cref="IsScanning"/> or <see cref="HasScanned"/> changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Raised (on the refreshing thread) as a refresh moves on: <see cref="GameLibraryScanned"/> once the Steam scan is
    /// merged, then <see cref="GameLibraryDetected"/>, or <see cref="GameLibraryRefreshFailed"/> instead of either.
    /// </summary>
    public event EventHandler<GameLibraryProgress>? Progressed;

    /// <summary>
    /// Refreshes in the background. While one runs, one more is queued after it (the request may come from a settings
    /// change the running scan hasn't seen); further requests join the queued one.
    /// </summary>
    public Task RefreshAsync()
    {
        lock (_lock)
        {
            if (_running is not { IsCompleted: false })
            {
                return _running = Task.Run(Refresh);
            }

            if (_queued is null)
            {
                _queued = _running.ContinueWith(
                    _ =>
                    {
                        lock (_lock)
                        {
                            _queued = null;
                        }

                        Refresh();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
                _running = _queued;
            }

            return _queued;
        }
    }

    /// <summary>Refreshes on the calling thread. Never throws: a failure is logged and the catalog keeps what it had.</summary>
    public void Refresh()
    {
        lock (_lock)
        {
            if (IsScanning)
            {
                return;
            }

            IsScanning = true;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var scan = _scanner.Scan(_settings.Current.SteamLibraries);
            var changes = Catalog.Refresh(scan);
            LastScan = scan;
            LastScanAt = _time.GetUtcNow();
            _logger.LogInformation(
                "Steam scan: {Libraries} libraries, {Games} games, {Changes} catalog changes",
                scan.Libraries.Count, scan.Games.Count, changes.Count);
            Progressed?.Invoke(this, new GameLibraryScanned(scan, changes));
            var stamp = _antiCheats.Policy.RulesStamp;
            var recheck = _rules is not null && !_rules.IsChecked(stamp);
            if (recheck)
            {
                _logger.LogInformation("Anti-cheat rules {Stamp} are new: checking every game again", stamp);
            }

            var detected = Math.Max(_engines.UpdateCatalog(Catalog, changes), _antiCheats.UpdateCatalog(Catalog, changes, recheck));
            _rules?.MarkChecked(stamp);
            Progressed?.Invoke(this, new GameLibraryDetected(detected));
        }
        catch (Exception e)
        {
            // A failed refresh must not take the app down; the catalog keeps the last known games.
            _logger.LogError(e, "Game library refresh failed");
            Progressed?.Invoke(this, new GameLibraryRefreshFailed(e));
        }
        finally
        {
            lock (_lock)
            {
                IsScanning = false;
                HasScanned = true;
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>A step of a <see cref="GameLibrary"/> refresh.</summary>
public abstract record GameLibraryProgress;

/// <summary>The Steam scan was merged into the catalog.</summary>
public sealed record GameLibraryScanned(SteamScan Scan, IReadOnlyList<GameChange> Changes) : GameLibraryProgress;

/// <summary>Engines and anti-cheats were detected; <paramref name="Games"/> = how many games were checked (0 = none needed it).</summary>
public sealed record GameLibraryDetected(int Games) : GameLibraryProgress;

/// <summary>The refresh stopped with an error (logged); the catalog keeps what it had.</summary>
public sealed record GameLibraryRefreshFailed(Exception Error) : GameLibraryProgress;
