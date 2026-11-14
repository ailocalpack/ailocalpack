using AiLocalPack.Core.Games;
using AiLocalPack.Core.Steam;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Library;

/// <summary>
/// The languages each Steam game ships with (task 80), kept in step with the library: after every Steam scan the client's
/// app info cache (<see cref="SteamAppInfo"/>) is read for the scanned games; a game it doesn't hold is looked up in the
/// store (<see cref="ISteamStoreLanguages"/>), one request at a time, <see cref="StoreSpacing"/> apart, stopping at the
/// first "too many requests"; a store answer is asked again after <see cref="StoreMaxAge"/>, a failed lookup at the next
/// scan. Everything is remembered in <see cref="OfficialLanguageStore"/>, so statuses are known at start and offline.
/// Manual games have none (their engines' language files are a later task). Never throws.
/// </summary>
public sealed class GameOfficialLanguages
{
    /// <summary>Between two store requests: the store allows about 200 in 5 minutes.</summary>
    public static readonly TimeSpan StoreSpacing = TimeSpan.FromSeconds(1.5);

    /// <summary>How long a store answer (or "the store doesn't know it") is used before asking again.</summary>
    public static readonly TimeSpan StoreMaxAge = TimeSpan.FromDays(7);

    private readonly OfficialLanguageStore _store;
    private readonly ISteamStoreLanguages _storeApi;
    private readonly SteamLocator _locator;
    private readonly GameCatalog _catalog;
    private readonly TimeProvider _time;
    private readonly ILogger<GameOfficialLanguages> _logger;
    private readonly Lock _lock = new();
    private Task _running = Task.CompletedTask;
    private SteamScan? _pending;

    public GameOfficialLanguages(
        OfficialLanguageStore store,
        ISteamStoreLanguages storeApi,
        SteamLocator locator,
        GameLibrary library,
        TimeProvider time,
        ILogger<GameOfficialLanguages> logger)
    {
        ArgumentNullException.ThrowIfNull(library);
        _store = store;
        _storeApi = storeApi;
        _locator = locator;
        _catalog = library.Catalog;
        _time = time;
        _logger = logger;
        library.Progressed += (_, progress) =>
        {
            if (progress is GameLibraryScanned scanned)
            {
                _ = UpdateAsync(scanned.Scan);
            }
        };
        if (library.LastScan is { } last)
        {
            _ = UpdateAsync(last);
        }
    }

    /// <summary>Raised (on a background thread) with the games whose languages changed.</summary>
    public event EventHandler<IReadOnlyList<GameId>>? Changed;

    /// <summary>The update running now, or a finished task (tests).</summary>
    internal Task WhenIdle
    {
        get
        {
            lock (_lock)
            {
                return _running;
            }
        }
    }

    /// <summary>The game's languages as last read; <c>null</c> when unknown (a manual game, not read yet).</summary>
    public OfficialLanguages? Find(GameId game) => _store.Find(game);

    /// <summary>Reads the languages of <paramref name="scan"/>'s games; a call while one runs is done after it (the latest scan only).</summary>
    public Task UpdateAsync(SteamScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        lock (_lock)
        {
            if (_running.IsCompleted)
            {
                return _running = Task.Run(() => RunAsync(scan));
            }

            var queued = _pending is null;
            _pending = scan;
            if (queued)
            {
                _running = _running.ContinueWith(
                    _ =>
                    {
                        SteamScan next;
                        lock (_lock)
                        {
                            next = _pending!;
                            _pending = null;
                        }

                        return RunAsync(next);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default).Unwrap();
            }

            return _running;
        }
    }

    private async Task RunAsync(SteamScan scan)
    {
        try
        {
            var now = _time.GetUtcNow();
            var appInfo = ReadAppInfo(scan);
            var known = new Dictionary<GameId, OfficialLanguages>();
            var ask = new List<SteamGame>();
            foreach (var game in scan.Games)
            {
                var id = GameId.Steam(game.AppId);
                if (appInfo.Apps.TryGetValue(game.AppId, out var languages))
                {
                    known[id] = new OfficialLanguages(languages, OfficialLanguagesSource.SteamClient, now, game.Language);
                }
                else if (_store.Find(id) is { Source: OfficialLanguagesSource.SteamStore } saved && now - saved.Checked < StoreMaxAge)
                {
                    known[id] = saved with { SteamLanguage = game.Language };
                }
                else
                {
                    ask.Add(game);
                }
            }

            Publish(_store.Set(known));
            _store.Keep([.. _catalog.Games.Select(g => g.Id)]);
            if (ask.Count > 0)
            {
                _logger.LogInformation("Official languages: {Count} games not in Steam's app info; asking the store", ask.Count);
                await AskStoreAsync(ask).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            // Languages are a nicety: the library works without them.
            _logger.LogWarning(e, "Reading the games' official languages failed");
        }
    }

    private SteamAppInfoLanguages ReadAppInfo(SteamScan scan)
    {
        var apps = scan.Games.Select(g => g.AppId).ToList();
        if (apps.Count == 0)
        {
            return SteamAppInfoLanguages.Missing;
        }

        foreach (var root in _locator.FindRoots())
        {
            var file = SteamAppInfo.FileIn(root);
            if (!File.Exists(file))
            {
                continue;
            }

            var read = SteamAppInfo.ReadLanguages(file, apps);
            if (read.Problem is { } problem)
            {
                _logger.LogWarning("Steam app info {File}: {Problem} ({Read} games read)", file, problem, read.Apps.Count);
            }

            return read;
        }

        return SteamAppInfoLanguages.Missing;
    }

    private async Task AskStoreAsync(IReadOnlyList<SteamGame> games)
    {
        for (var i = 0; i < games.Count; i++)
        {
            if (i > 0)
            {
                await Task.Delay(StoreSpacing, _time).ConfigureAwait(false);
            }

            var game = games[i];
            var answer = await _storeApi.GetAsync(game.AppId).ConfigureAwait(false);
            switch (answer.Lookup)
            {
                case SteamStoreLookup.RateLimited:
                    _logger.LogInformation("Steam store: too many requests; {Count} games wait for the next scan", games.Count - i);
                    return;
                case SteamStoreLookup.Failed:
                    _logger.LogInformation("Steam store: no languages for {AppId} ({Problem})", game.AppId, answer.Problem);
                    continue;
            }

            var languages = new OfficialLanguages(answer.Languages ?? [], OfficialLanguagesSource.SteamStore, _time.GetUtcNow(), game.Language);
            Publish(_store.Set([new(GameId.Steam(game.AppId), languages)]));
        }
    }

    private void Publish(IReadOnlyList<GameId> changed)
    {
        if (changed.Count > 0)
        {
            Changed?.Invoke(this, changed);
        }
    }
}
