using AiLocalPack.Core.Games;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Packs;
using AiLocalPack.Desktop.Library;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Desktop.Generation;

/// <summary>Whether a game session counts as the player trying the installed local pack (task 55). Pure.</summary>
public static class LocalPackTrial
{
    /// <summary>
    /// The session ended with a local pack installed before it started: the player played the game with the pack. A session
    /// that started before the install (the game kept running) doesn't count.
    /// </summary>
    public static bool Counts(InstalledPack? record, GameSessionEnded ended)
    {
        ArgumentNullException.ThrowIfNull(ended);
        return record is { Local: true } && record.Game == ended.Game && ended.StartedAt >= record.InstalledAt;
    }
}

/// <summary>
/// The packs generated on this machine (task 55, <see cref="LocalPackStore"/>) as the Generation tab's result and
/// Packs → Mine show them: listed from disk, the installed one told apart, the player's marks (tried in game, "Not now"
/// on the share strip) and deleting. Follows <see cref="GameSessions"/>: when the game closes after a session that
/// started with a local pack installed, that pack is marked tried in game, and publishing is offered from then on.
/// <see cref="Changed"/> tells the pages. Never throws for disk state.
/// </summary>
public sealed class LocalPacks
{
    private readonly LocalPackStore _store;
    private readonly InstalledPackStore _installed;
    private readonly ILogger _logger;

    public LocalPacks(LocalPackStore store, InstalledPackStore installed, GameSessions sessions, ILogger<LocalPacks>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _store = store;
        _installed = installed;
        _logger = logger ?? NullLogger<LocalPacks>.Instance;
        sessions.Changed += OnSessionChanged;
    }

    /// <summary>Raised (on any thread) when a game's local packs changed: deleted, marked tried, "Not now".</summary>
    public event EventHandler<GameId>? Changed;

    /// <summary>The game's local packs, by language and ID. Reads the disk.</summary>
    public IReadOnlyList<LocalPack> List(GameId game) => _store.List(game);

    /// <summary>The local pack, or <c>null</c> when it is gone. Reads the disk.</summary>
    public LocalPack? Find(GameId game, string language, string id) => _store.Find(game, language, id);

    /// <summary>The local pack the job made, or <c>null</c>. Reads the disk.</summary>
    public LocalPack? FindByJob(GameId game, string jobId) => _store.FindByJob(game, jobId);

    /// <summary>The pack is the one installed into its game.</summary>
    public bool IsInstalled(LocalPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return IsInstalled(_installed.Find(pack.Manifest.Game), pack);
    }

    /// <summary><paramref name="record"/> is <paramref name="pack"/>.</summary>
    public static bool IsInstalled(InstalledPack? record, LocalPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return record is { Local: true } && record.Game == pack.Manifest.Game && record.Language == pack.Manifest.Language && record.PackId == pack.Manifest.Id;
    }

    /// <summary>
    /// Deletes the pack and its report. Refused (<c>false</c>) while it is installed: uninstalling it needs nothing of it,
    /// but re-applying after a game update would. Throws for a disk error.
    /// </summary>
    /// <exception cref="IOException">A file can't be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">A file can't be deleted.</exception>
    public bool Delete(LocalPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (IsInstalled(pack))
        {
            return false;
        }

        _store.Delete(pack.Manifest.Game, pack.Manifest.Language, pack.Manifest.Id);
        _logger.LogInformation("Local pack {Pack} ({Language}) of {Game} deleted", pack.Manifest.Id, pack.Manifest.Language, pack.Manifest.Game);
        Changed?.Invoke(this, pack.Manifest.Game);
        return true;
    }

    /// <summary>"Not now" on the share strip: the strip stays hidden for the pack (publishing stays in Packs → Mine).</summary>
    public void DismissShare(LocalPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        Change(pack.Manifest.Game, pack.Manifest.Language, pack.Manifest.Id, r => r with { ShareDismissed = true });
    }

    /// <summary>Marks the pack tried in game at <paramref name="at"/> (the first time only).</summary>
    public void MarkTried(GameId game, string language, string id, DateTimeOffset at) =>
        Change(game, language, id, r => r.Tried is null ? r with { Tried = at } : r);

    private void Change(GameId game, string language, string id, Func<LocalPackReport, LocalPackReport> change)
    {
        try
        {
            if (_store.UpdateReport(game, language, id, change) is not null)
            {
                Changed?.Invoke(this, game);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Couldn't save the report of the local pack {Pack} ({Language}) of {Game}", id, language, game);
        }
    }

    private void OnSessionChanged(object? sender, GameSessionEvent change)
    {
        if (change is not GameSessionEnded ended || _installed.Find(ended.Game) is not { } record || !LocalPackTrial.Counts(record, ended))
        {
            return;
        }

        if (_store.Find(record.Game, record.Language, record.PackId) is { Report.IsTried: false })
        {
            _logger.LogInformation("Local pack {Pack} of {Game} tried in game ({Duration} played)", record.PackId, record.Game, ended.Duration);
            MarkTried(record.Game, record.Language, record.PackId, ended.EndedAt);
        }
    }
}
