using AiLocalPack.Core.Games;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Library;

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
/// own language only. Whether an update broke it (<see cref="InstalledPackState.BrokenByUpdate"/>) comes with task 33.
/// </summary>
public sealed class StoreInstalledPacks : IInstalledPacks
{
    private readonly InstalledPackStore _store;

    public StoreInstalledPacks(InstalledPackStore store)
    {
        _store = store;
        _store.Changed += (_, game) => Changed?.Invoke(this, game);
    }

    public event EventHandler<GameId>? Changed;

    public InstalledPackState? Find(GameId game, string language) =>
        _store.Find(game) is { } pack && pack.Language == language ? new InstalledPackState(pack.PackId, false, pack.Revision) : null;
}
