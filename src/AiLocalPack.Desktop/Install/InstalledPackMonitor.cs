using System.Collections.Concurrent;
using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Registry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Desktop.Install;

/// <summary>What the last re-check found for a game's installed pack; valid only for the record it was made for.</summary>
/// <param name="InstalledAt">The record's install time: a later install or re-apply makes the entry stale.</param>
/// <param name="Failure">The automatic re-apply that failed (<see cref="InstalledPackProblem.ReapplyFailed"/>).</param>
public sealed record InstalledPackHealth(
    GameId Game,
    string PackId,
    int Revision,
    DateTimeOffset InstalledAt,
    InstalledPackProblem Problem,
    InstallOutcome? Failure = null)
{
    public bool IsFor(InstalledPack record) =>
        record.Game == Game && record.PackId == PackId && record.Revision == Revision && record.InstalledAt == InstalledAt;
}

/// <summary>One installed pack's re-check.</summary>
/// <param name="Title">The pack's title from the pack list, else its ID.</param>
/// <param name="Reapply">The automatic re-apply, when one ran.</param>
public sealed record PackRecheckResult(Game Game, string Title, PackRecheckDecision Decision, InstalledPackProblem Problem, InstallOutcome? Reapply = null)
{
    /// <summary>Re-applied after the game update.</summary>
    public bool Reapplied => Reapply is { Succeeded: true };
}

/// <summary>
/// Re-checks installed packs after game updates (task 33, design §3 update banner): each record's files are hashed
/// (<see cref="PackRecheck.Inspect"/>); when an update or a Steam file check put files back or rewrote them, the
/// installed revision's manifest (pack cache, else the registry while it lists the revision) is checked against the game
/// as it is without the pack, and <see cref="PackRecheck.Decide"/> says what to do: re-apply it (with a fresh backup,
/// through <see cref="PackInstallService"/>; setting <see cref="AppSettings.ReapplyAfterGameUpdates"/>), or mark it
/// <see cref="InstalledPackProblem.Incompatible"/> / <see cref="InstalledPackProblem.Wiped"/> /
/// <see cref="InstalledPackProblem.ReapplyFailed"/> so the statuses say <c>Needs update</c>. Unavailable and blocked
/// games are left alone. Runs at start-up (<see cref="CheckAllAsync"/> from <c>AppStartup</c>, after the Steam scan and
/// the pack list) and after every later library refresh. What it found is kept for the session only: the next start
/// checks again. Never throws.
/// </summary>
public sealed class InstalledPackMonitor
{
    private readonly InstalledPackStore _store;
    private readonly GameLibrary _library;
    private readonly PackIndex _packIndex;
    private readonly PackCache _cache;
    private readonly PackManifests _manifests;
    private readonly PackCompatibilityChecker _checker;
    private readonly FileHashCache _hashes;
    private readonly PackInstallService _service;
    private readonly SettingsStore _settings;
    private readonly AntiCheatPolicy _policy;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<GameId, InstalledPackHealth> _health = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _started;

    public InstalledPackMonitor(
        InstalledPackStore store,
        GameLibrary library,
        PackIndex packIndex,
        PackCache cache,
        PackManifests manifests,
        PackCompatibilityChecker checker,
        FileHashCache hashes,
        PackInstallService service,
        SettingsStore settings,
        AntiCheatPolicy policy,
        ILogger<InstalledPackMonitor>? logger = null)
    {
        _store = store;
        _library = library;
        _packIndex = packIndex;
        _cache = cache;
        _manifests = manifests;
        _checker = checker;
        _hashes = hashes;
        _service = service;
        _settings = settings;
        _policy = policy;
        _logger = logger ?? NullLogger<InstalledPackMonitor>.Instance;
        _library.Progressed += OnLibraryProgressed;
    }

    /// <summary>Raised (on a pool thread) when what a game's pack needs changed.</summary>
    public event EventHandler<GameId>? Changed;

    /// <summary>Raised (on a pool thread) after an automatic re-apply, done or failed (the done / failure toast).</summary>
    public event EventHandler<PackRecheckResult>? Reapplied;

    /// <summary>The last re-check's finding for the game's current record; <c>null</c> when none applies (intact, not checked).</summary>
    public InstalledPackHealth? Find(GameId game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return _health.TryGetValue(game, out var health) && _store.Find(game) is { } record && health.IsFor(record) ? health : null;
    }

    /// <summary>The problem of the installed pack (<see cref="InstalledPackProblem.None"/> when in place or not checked).</summary>
    public InstalledPackProblem ProblemOf(InstalledPack record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return _health.TryGetValue(record.Game, out var health) && health.IsFor(record) ? health.Problem : InstalledPackProblem.None;
    }

    /// <summary>
    /// Re-checks every installed pack, one game after another (one pass at a time: a request during one waits for it).
    /// From the first call on, later library refreshes check again by themselves.
    /// </summary>
    public async Task<IReadOnlyList<PackRecheckResult>> CheckAllAsync(CancellationToken cancellationToken = default)
    {
        _started = true;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<PackRecheckResult>();
            foreach (var record in _store.All)
            {
                if (_library.Catalog.Find(record.Game) is not { } game)
                {
                    continue;
                }

                try
                {
                    if (await CheckAsync(record, game, cancellationToken).ConfigureAwait(false) is { } result)
                    {
                        results.Add(result);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _logger.LogError(e, "Re-checking the pack {Pack} of {Game} failed", record.PackId, record.Game);
                }
            }

            _hashes.Save();
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>One game's pack; <c>null</c> when the game can't be checked (unavailable, blocked).</summary>
    private async Task<PackRecheckResult?> CheckAsync(InstalledPack record, Game game, CancellationToken cancellationToken)
    {
        if (!game.IsAvailable || !Directory.Exists(game.InstallPath) || GameBlocks.Resolve(game, _policy, _packIndex.Current?.Index) is not null)
        {
            return null;
        }

        var title = _service.TitleOf(game.Id, record);
        var files = await Task.Run(() => PackRecheck.Inspect(record, game.InstallPath, _hashes, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (files.IsIntact)
        {
            Set(record, InstalledPackProblem.None);
            return new PackRecheckResult(game, title, PackRecheckDecision.Intact, InstalledPackProblem.None);
        }

        PackCompatibility? compatibility = null;
        if (files.Unreadable.Count == 0 && await ManifestOfAsync(record, cancellationToken).ConfigureAwait(false) is { } manifest)
        {
            compatibility = await Task.Run(() => _checker.Check(manifest, game, files.Originals, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        var decision = PackRecheck.Decide(files, compatibility?.Verdict, _settings.Current.ReapplyAfterGameUpdates);
        _logger.LogInformation(
            "Pack {Pack} rev {Revision} of {Game}: {Reverted} files put back, {Changed} changed, {Missing} missing, {Unreadable} unreadable; {Verdict} ({Reason}) → {Decision}",
            record.PackId, record.Revision, game.Id, files.Reverted.Count, files.Changed.Count, files.Missing.Count, files.Unreadable.Count,
            compatibility?.Verdict.ToString() ?? "not checked", compatibility?.Reason.ToString() ?? "-", decision);

        switch (decision)
        {
            case PackRecheckDecision.Unchecked:
                return new PackRecheckResult(game, title, decision, ProblemOf(record));
            case PackRecheckDecision.Incompatible:
                Set(record, InstalledPackProblem.Incompatible);
                return new PackRecheckResult(game, title, decision, InstalledPackProblem.Incompatible);
            case PackRecheckDecision.Wiped:
                Set(record, InstalledPackProblem.Wiped);
                return new PackRecheckResult(game, title, decision, InstalledPackProblem.Wiped);
        }

        var outcome = await _service.ReapplyAsync(game, cancellationToken).ConfigureAwait(false);
        PackRecheckResult result;
        if (outcome.Succeeded)
        {
            _health.TryRemove(game.Id, out _);
            result = new PackRecheckResult(game, title, decision, InstalledPackProblem.None, outcome);
        }
        else if (outcome.Error!.Kind == InstallErrorKind.Busy)
        {
            // The player is installing something: left for them (or the next check).
            Set(record, InstalledPackProblem.Wiped);
            return new PackRecheckResult(game, title, PackRecheckDecision.Wiped, InstalledPackProblem.Wiped);
        }
        else
        {
            Set(_store.Find(game.Id) ?? record, InstalledPackProblem.ReapplyFailed, outcome);
            result = new PackRecheckResult(game, title, decision, InstalledPackProblem.ReapplyFailed, outcome);
        }

        Changed?.Invoke(this, game.Id);
        Reapplied?.Invoke(this, result);
        return result;
    }

    /// <summary>The installed revision's manifest: the pack cache keeps it since the install; else the registry while it lists the revision.</summary>
    private async Task<PackManifest?> ManifestOfAsync(InstalledPack record, CancellationToken cancellationToken)
    {
        if (_cache.Find(record.Game, record.Language, record.PackId, record.Revision) is { } cached)
        {
            return cached.Manifest;
        }

        var pack = _packIndex.Current?.Index?.PacksFor(record.Game, record.Language).FirstOrDefault(p => p.Id == record.PackId && p.Revision == record.Revision);
        if (pack is null || _packIndex.Current?.Location is not { } location)
        {
            return null;
        }

        var fetch = await _manifests.LoadAsync(location, pack, cancellationToken).ConfigureAwait(false);
        return fetch.Manifest;
    }

    private void Set(InstalledPack record, InstalledPackProblem problem, InstallOutcome? failure = null)
    {
        var before = _health.TryGetValue(record.Game, out var known) && known.IsFor(record) ? known.Problem : InstalledPackProblem.None;
        if (problem == InstalledPackProblem.None)
        {
            _health.TryRemove(record.Game, out _);
        }
        else
        {
            _health[record.Game] = new InstalledPackHealth(record.Game, record.PackId, record.Revision, record.InstalledAt, problem, failure);
        }

        if (before != problem || failure is not null)
        {
            Changed?.Invoke(this, record.Game);
        }
    }

    /// <summary>A refresh after the start-up one (a Rescan, a library added) may have found updated games.</summary>
    private void OnLibraryProgressed(object? sender, GameLibraryProgress progress)
    {
        if (_started && progress is GameLibraryDetected)
        {
            _ = CheckAllAsync();
        }
    }
}
