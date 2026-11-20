using AiLocalPack.Core.Steam;
using AiLocalPack.Core.Stores;

namespace AiLocalPack.Core.Games;

/// <summary>Pure merge of the stored catalog with fresh store scans; see <see cref="Merge(IEnumerable{Game}, StoreScans, Func{string, bool})"/>.</summary>
public static class GameCatalogMerge
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Steam games in the scan are added or refreshed (name, folder, library, build, last played; engine and anti-cheat
    /// kept); a different build ID than stored gives <see cref="GameVersionChanged"/>. A stored Steam game missing from
    /// the scan is kept as unavailable when its library is listed but not available, or when Steam was not found at all
    /// (<see cref="SteamScan.SteamFound"/>: Steam on a missing drive must not wipe the catalog); otherwise it was
    /// uninstalled and is removed. Manual games are kept; their availability follows <paramref name="directoryExists"/>.
    /// Games of other stores are kept as they are. Result is sorted by name, then ID.
    /// </summary>
    public static (IReadOnlyList<Game> Games, IReadOnlyList<GameChange> Changes) Merge(
        IEnumerable<Game> stored,
        SteamScan scan,
        Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(scan);
        return Merge(stored, new StoreScans(scan, [], new HashSet<GameSource>()), directoryExists);
    }

    /// <summary>
    /// Merges a refresh of every store (task 83). Steam as in <see cref="Merge(IEnumerable{Game}, SteamScan, Func{string, bool})"/>
    /// when <see cref="StoreScans.Steam"/> is set. Each other store with a scan: its games in the scan are added or
    /// refreshed (name, folder, library, version, main executable, app, availability; engine, anti-cheat, platform and last
    /// played kept; a different version gives <see cref="GameVersionChanged"/>); a stored game missing from the scan is
    /// kept as unavailable when the store wasn't found or couldn't be read (<see cref="StoreScan.Problem"/>), otherwise
    /// it was uninstalled and is removed. A new store game whose folder a manual game holds is left out (the manual game
    /// keeps its ID and installed packs). A store without a scan keeps its games; a store in
    /// <see cref="StoreScans.Disabled"/> loses them (<see cref="GameRemoved"/>: folders and packs stay on disk).
    /// </summary>
    public static (IReadOnlyList<Game> Games, IReadOnlyList<GameChange> Changes) Merge(
        IEnumerable<Game> stored,
        StoreScans scans,
        Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(scans);
        ArgumentNullException.ThrowIfNull(directoryExists);

        var steamScan = scans.Steam;
        var scanned = steamScan?.Games.ToDictionary(g => GameId.Steam(g.AppId)) ?? [];
        var unavailableLibraries = (steamScan?.Libraries ?? []).Where(l => !l.IsAvailable).Select(l => l.Path).ToHashSet(PathComparer);
        var steamFound = steamScan?.SteamFound ?? true;
        var stores = scans.Stores.GroupBy(s => s.Source).ToDictionary(g => g.Key, g => g.First());
        var storeGames = new Dictionary<GameId, StoreGame>();
        foreach (var scan in stores.Values)
        {
            foreach (var game in scan.Games)
            {
                if (GameSources.SourceOf(game.Id.Kind) == scan.Source)
                {
                    storeGames.TryAdd(game.Id, game);
                }
            }
        }

        var games = new List<Game>();
        var changes = new List<GameChange>();
        var seen = new HashSet<GameId>();

        foreach (var game in stored)
        {
            if (!seen.Add(game.Id))
            {
                continue;
            }

            if (game.Source == GameSource.Manual)
            {
                games.Add(game with { IsAvailable = directoryExists(game.InstallPath) });
            }
            else if (scans.Disabled.Contains(game.Source))
            {
                changes.Add(new GameRemoved(game));
            }
            else if (game.Source == GameSource.Steam)
            {
                if (steamScan is null)
                {
                    games.Add(game);
                }
                else if (scanned.TryGetValue(game.Id, out var steam))
                {
                    Refreshed(FromScan(steam, game), game);
                }
                else if (!steamFound || (game.LibraryPath is not null && unavailableLibraries.Contains(game.LibraryPath)))
                {
                    games.Add(game with { IsAvailable = false });
                }
                else
                {
                    changes.Add(new GameRemoved(game));
                }
            }
            else if (!stores.TryGetValue(game.Source, out var store))
            {
                games.Add(game);
            }
            else if (storeGames.TryGetValue(game.Id, out var found))
            {
                Refreshed(FromStore(found, game), game);
            }
            else if (!store.Found
                || store.Problem is not null
                || (store.MissingFolders ?? []).Contains(game.InstallPath, PathComparer))
            {
                games.Add(game with { IsAvailable = false });
            }
            else
            {
                changes.Add(new GameRemoved(game));
            }
        }

        foreach (var (id, steam) in scanned)
        {
            if (!scans.Disabled.Contains(GameSource.Steam) && seen.Add(id))
            {
                Added(FromScan(steam, previous: null));
            }
        }

        var manualFolders = games.Where(g => g.Source == GameSource.Manual).Select(g => g.InstallPath).ToHashSet(PathComparer);
        foreach (var (id, found) in storeGames)
        {
            if (!scans.Disabled.Contains(GameSources.SourceOf(id.Kind))
                && !manualFolders.Contains(found.InstallPath)
                && seen.Add(id))
            {
                Added(FromStore(found, previous: null));
            }
        }

        return (Sort(games), changes);

        void Refreshed(Game refreshed, Game previous)
        {
            games.Add(refreshed);
            if (previous.Version is not null && refreshed.Version is not null && previous.Version != refreshed.Version)
            {
                changes.Add(new GameVersionChanged(refreshed, previous.Version));
            }
        }

        void Added(Game added)
        {
            games.Add(added);
            changes.Add(new GameAdded(added));
        }
    }

    /// <summary>Catalog order: name (current culture, case-insensitive), then ID.</summary>
    public static List<Game> Sort(IEnumerable<Game> games) =>
        games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(g => g.Id.ToString(), StringComparer.Ordinal)
            .ToList();

    private static Game FromScan(SteamGame steam, Game? previous) => new(
        GameId.Steam(steam.AppId),
        steam.Name,
        GameSource.Steam,
        steam.InstallPath,
        steam.LibraryPath,
        // A manifest that lost its build ID keeps the known version rather than forgetting it.
        steam.BuildId is { } build ? GameVersion.FromBuildId(build) : previous?.Version,
        previous?.Engine,
        previous?.AntiCheat,
        steam.LastPlayed ?? previous?.LastPlayed,
        IsAvailable: true,
        Platform: previous?.Platform);

    private static Game FromStore(StoreGame found, Game? previous) => new(
        found.Id,
        found.Name,
        GameSources.SourceOf(found.Id.Kind),
        found.InstallPath,
        found.LibraryPath,
        // A record that lost its version keeps the known one rather than forgetting it.
        found.Version is { } version ? GameVersion.FromBuildId(version) : previous?.Version,
        previous?.Engine,
        previous?.AntiCheat,
        previous?.LastPlayed,
        found.IsAvailable,
        found.MainExecutable ?? previous?.MainExecutable,
        previous?.Platform,
        found.App);
}

/// <summary>What one catalog refresh read (<see cref="GameCatalogMerge.Merge(IEnumerable{Game}, StoreScans, Func{string, bool})"/>).</summary>
/// <param name="Steam">The Steam scan; <c>null</c> = Steam wasn't scanned (switched off, or not part of this refresh).</param>
/// <param name="Stores">Scans of the other stores; a store without one keeps its games.</param>
/// <param name="Disabled">Stores the player switched off: their games leave the catalog.</param>
public sealed record StoreScans(SteamScan? Steam, IReadOnlyList<StoreScan> Stores, IReadOnlySet<GameSource> Disabled);
