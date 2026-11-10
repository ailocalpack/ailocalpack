using System.Collections.Concurrent;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Packs;
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
/// finding (<see cref="InstalledPackMonitor"/>, task 33). Whether it was made for the game's files as they are
/// (<see cref="InstalledPackState.ForOtherVersion"/>, task 56) comes from the installed revision's manifest (the local
/// pack, else the pack cache, which keeps it since the install), worked out once per install record: an update job's
/// next revision replaces a local base, so its manifest may be gone later in the session. Unknown manifest = fits.
/// </summary>
public sealed class StoreInstalledPacks : IInstalledPacks
{
    private readonly InstalledPackStore _store;
    private readonly InstalledPackMonitor? _monitor;
    private readonly LocalPackStore? _localPacks;
    private readonly PackCache? _cache;
    private readonly ConcurrentDictionary<(GameId Game, string PackId, int Revision, DateTimeOffset InstalledAt), bool> _otherVersion = new();

    public StoreInstalledPacks(InstalledPackStore store, InstalledPackMonitor? monitor = null, LocalPackStore? localPacks = null, PackCache? cache = null)
    {
        _store = store;
        _monitor = monitor;
        _localPacks = localPacks;
        _cache = cache;
        _store.Changed += (_, game) => Changed?.Invoke(this, game);
        if (_monitor is not null)
        {
            _monitor.Changed += (_, game) => Changed?.Invoke(this, game);
        }
    }

    public event EventHandler<GameId>? Changed;

    public InstalledPackState? Find(GameId game, string language) =>
        _store.Find(game) is { } pack && pack.Language == language
            ? new InstalledPackState(pack.PackId, _monitor?.ProblemOf(pack) ?? InstalledPackProblem.None, pack.Revision, pack.Local, pack.Title, IsForOtherVersion(pack))
            : null;

    private bool IsForOtherVersion(InstalledPack record) =>
        _otherVersion.GetOrAdd((record.Game, record.PackId, record.Revision, record.InstalledAt), _ => ManifestOf(record) is { } manifest && PackFit.IsForOtherVersion(record, manifest));

    private PackManifest? ManifestOf(InstalledPack record) => record.Local
        ? _localPacks?.Find(record.Game, record.Language, record.PackId)?.Manifest is { } local && local.Revision == record.Revision ? local : null
        : _cache?.Find(record.Game, record.Language, record.PackId, record.Revision)?.Manifest;
}
