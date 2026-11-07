using AiLocalPack.Core.Games;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Library;
using AiLocalPack.Desktop.Install;

namespace AiLocalPack.Desktop.Library;

/// <summary>The pack installed for a game and language, as statuses need it.</summary>
public interface IInstalledPacks
{
    /// <summary>Raised (on any thread) after a game's pack was installed, replaced or uninstalled.</summary>
    event EventHandler<GameId>? Changed;

    InstalledPackState? Find(GameId game, string language);
}

/// <summary>
/// The install records of <see cref="InstalledPackStore"/> (task 29). A game has at most one pack: it counts for its
/// own language only. What a game update did to it (<see cref="InstalledPackState.Problem"/>) is the re-check's
/// finding (<see cref="InstalledPackMonitor"/>, task 33).
/// </summary>
public sealed class StoreInstalledPacks : IInstalledPacks
{
    private readonly InstalledPackStore _store;
    private readonly InstalledPackMonitor? _monitor;

    public StoreInstalledPacks(InstalledPackStore store, InstalledPackMonitor? monitor = null)
    {
        _store = store;
        _monitor = monitor;
        _store.Changed += (_, game) => Changed?.Invoke(this, game);
        if (_monitor is not null)
        {
            _monitor.Changed += (_, game) => Changed?.Invoke(this, game);
        }
    }

    public event EventHandler<GameId>? Changed;

    public InstalledPackState? Find(GameId game, string language) =>
        _store.Find(game) is { } pack && pack.Language == language
            ? new InstalledPackState(pack.PackId, _monitor?.ProblemOf(pack) ?? InstalledPackProblem.None, pack.Revision)
            : null;
}
