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
/// or updated games. One refresh runs at a time; games appear (catalog <see cref="GameCatalog.Changed"/>) as soon as
/// the Steam scan is merged, detection details follow.
/// </summary>
public sealed class GameLibrary(
    GameCatalog catalog,
    SteamLibraryScanner scanner,
    EngineDetector engines,
    AntiCheatDetector antiCheats,
    SettingsStore settings,
    TimeProvider time,
    ILogger<GameLibrary> logger)
{
    private readonly Lock _lock = new();
    private Task? _running;
    private Task? _queued;

    public GameCatalog Catalog { get; } = catalog;

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
            var scan = scanner.Scan(settings.Current.SteamLibraries);
            var changes = Catalog.Refresh(scan);
            LastScan = scan;
            LastScanAt = time.GetUtcNow();
            logger.LogInformation(
                "Steam scan: {Libraries} libraries, {Games} games, {Changes} catalog changes",
                scan.Libraries.Count, scan.Games.Count, changes.Count);
            engines.UpdateCatalog(Catalog, changes);
            antiCheats.UpdateCatalog(Catalog, changes);
        }
        catch (Exception e)
        {
            // A failed refresh must not take the app down; the catalog keeps the last known games.
            logger.LogError(e, "Game library refresh failed");
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
