using AiLocalPack.Core.Games;
using AiLocalPack.Core.Steam;
using AiLocalPack.Toolkit.AntiCheat;
using AiLocalPack.Toolkit.Engines;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Library;

/// <summary>
/// Keeps the game catalog in step with the disk: a refresh scans Steam, merges the result into <see cref="GameCatalog"/>,
/// then detects engines and anti-cheats of new or updated games. One refresh runs at a time; games appear (catalog
/// <see cref="GameCatalog.Changed"/>) as soon as the Steam scan is merged, detection details follow.
/// </summary>
public sealed class GameLibrary(
    GameCatalog catalog,
    SteamLibraryScanner scanner,
    EngineDetector engines,
    AntiCheatDetector antiCheats,
    ILogger<GameLibrary> logger)
{
    private readonly Lock _lock = new();
    private Task? _running;

    public GameCatalog Catalog { get; } = catalog;

    /// <summary>A refresh is running.</summary>
    public bool IsScanning { get; private set; }

    /// <summary>At least one refresh finished this session (before that the catalog holds only what was saved last time).</summary>
    public bool HasScanned { get; private set; }

    /// <summary>Raised (on the refreshing thread) when <see cref="IsScanning"/> or <see cref="HasScanned"/> changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Refreshes in the background; while one runs, returns that one instead of starting another.</summary>
    public Task RefreshAsync()
    {
        lock (_lock)
        {
            return _running is { IsCompleted: false } ? _running : _running = Task.Run(Refresh);
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
            var scan = scanner.Scan();
            var changes = Catalog.Refresh(scan);
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
