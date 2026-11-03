using AiLocalPack.Core.Steam;

namespace AiLocalPack.Core.Games;

/// <summary>Pure merge of the stored catalog with a fresh Steam scan; see <see cref="Merge"/>.</summary>
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
    /// Result is sorted by name, then ID.
    /// </summary>
    public static (IReadOnlyList<Game> Games, IReadOnlyList<GameChange> Changes) Merge(
        IEnumerable<Game> stored,
        SteamScan scan,
        Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(directoryExists);

        var scanned = scan.Games.ToDictionary(g => GameId.Steam(g.AppId));
        var unavailableLibraries = scan.Libraries.Where(l => !l.IsAvailable).Select(l => l.Path).ToHashSet(PathComparer);
        var steamFound = scan.SteamFound;
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
            else if (scanned.TryGetValue(game.Id, out var steam))
            {
                var refreshed = FromScan(steam, game);
                games.Add(refreshed);
                if (game.Version is not null && refreshed.Version is not null && game.Version != refreshed.Version)
                {
                    changes.Add(new GameVersionChanged(refreshed, game.Version));
                }
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

        foreach (var (id, steam) in scanned)
        {
            if (seen.Add(id))
            {
                var added = FromScan(steam, previous: null);
                games.Add(added);
                changes.Add(new GameAdded(added));
            }
        }

        return (Sort(games), changes);
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
        IsAvailable: true);
}
