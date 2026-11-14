using AiLocalPack.Core.Games;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Publishing;
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

    /// <summary>
    /// Raised (on any thread) when the game closed within <see cref="GameCrashRule.Threshold"/> of starting with a local
    /// pack installed (task 75): the pack is marked crashed (not tried), and the player is offered a fix with AI.
    /// </summary>
    public event EventHandler<LocalPack>? Crashed;

    /// <summary>The game's local packs, by language and ID. Reads the disk.</summary>
    public IReadOnlyList<LocalPack> List(GameId game) => _store.List(game);

    /// <summary>Every game's local packs (Settings → GitHub, PR tracking). Reads the disk.</summary>
    public IReadOnlyList<LocalPack> ListAll() => _store.List();

    /// <summary>Records what became of the pack's PR (merged or closed, task 62); nothing when the pack was sent again since.</summary>
    public void RecordPullRequest(LocalPack pack, int pullRequest, PullRequestState state, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(pack);
        Change(pack.Manifest.Game, pack.Manifest.Language, pack.Manifest.Id, r => r.Published is { } p && p.PullRequest == pullRequest && p.State != state
            ? r with { Published = p with { State = state, StateAt = at } }
            : r);
    }

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

    /// <summary>
    /// <paramref name="record"/> is <paramref name="pack"/>: the same revision (an update job's pack replaces its local
    /// base, task 56) and installed after it was built (a fix with AI rebuilds the pack as its next draft under the same
    /// ID and revision, task 75, so the installed one is an earlier draft).
    /// </summary>
    public static bool IsInstalled(InstalledPack? record, LocalPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return IsSamePack(record, pack) && record!.InstalledAt >= pack.Report.Created;
    }

    /// <summary><paramref name="record"/> installed this pack's ID and revision, maybe an earlier draft of it (task 75).</summary>
    public static bool IsSamePack(InstalledPack? record, LocalPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return record is { Local: true } && record.Game == pack.Manifest.Game && record.Language == pack.Manifest.Language && record.PackId == pack.Manifest.Id
            && record.Revision == pack.Manifest.Revision;
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

    /// <summary>The game's local packs changed on disk by another writer (publishing recorded a PR, task 61): the pages read them again.</summary>
    public void Refresh(GameId game) => Changed?.Invoke(this, game);

    /// <summary>Marks the pack tried in game at <paramref name="at"/> (the first time only).</summary>
    public void MarkTried(GameId game, string language, string id, DateTimeOffset at) =>
        Change(game, language, id, r => r.Tried is null ? r with { Tried = at } : r);

    /// <summary>
    /// "Not a crash, I closed it": the short session was the player's own (a look at the menu), so it counts as tried at
    /// the time the game closed (the first time only) and the crash goes. Nothing when the pack has no crash.
    /// </summary>
    public void DismissCrash(LocalPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        Change(pack.Manifest.Game, pack.Manifest.Language, pack.Manifest.Id, r => r.Crash is { } crash
            ? r with { Crash = null, Tried = r.Tried ?? crash.At }
            : r);
        _logger.LogInformation("Local pack {Pack} of {Game}: the player says the short session wasn't a crash", pack.Manifest.Id, pack.Manifest.Game);
    }

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

        if (_store.Find(record.Game, record.Language, record.PackId) is not { } pack || !IsInstalled(record, pack))
        {
            return;
        }

        if (GameCrashRule.IsCrash(ended))
        {
            _logger.LogInformation("Local pack {Pack} of {Game}: the game closed {Duration} after it started", record.PackId, record.Game, ended.Duration);
            var crash = new LocalPackCrash(ended.EndedAt, ended.Duration);
            Change(record.Game, record.Language, record.PackId, r => r with { Crash = crash });
            if (_store.Find(record.Game, record.Language, record.PackId) is { } crashed)
            {
                Crashed?.Invoke(this, crashed);
            }

            return;
        }

        if (!pack.Report.IsTried)
        {
            _logger.LogInformation("Local pack {Pack} of {Game} tried in game ({Duration} played)", record.PackId, record.Game, ended.Duration);
            MarkTried(record.Game, record.Language, record.PackId, ended.EndedAt);
        }
    }
}
