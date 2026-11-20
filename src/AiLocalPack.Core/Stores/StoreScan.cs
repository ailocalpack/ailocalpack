using AiLocalPack.Core.Games;

namespace AiLocalPack.Core.Stores;

/// <summary>
/// A game store beside Steam (Epic Games, GOG, EA app; task 83): reads the store's own records of installed games.
/// Steam keeps its own scanner (<c>SteamLibraryScanner</c>) because of its library folders and settings.
/// </summary>
public interface IGameStore
{
    /// <summary>The source of the games this store finds; their IDs are of kind <see cref="GameSources.KindOf"/>.</summary>
    GameSource Source { get; }

    /// <summary>Whether the store can be on this OS at all (the EA app is Windows only).</summary>
    bool IsSupportedOnThisOs { get; }

    /// <summary>Reads the installed games. Best effort: never throws for disk state; a problem the player should see is <see cref="StoreScan.Problem"/>.</summary>
    StoreScan Scan();
}

/// <summary>A game a store lists as installed.</summary>
/// <param name="Id">The store's ID (<see cref="GameId.Store"/>).</param>
/// <param name="Name">The store's name of the game.</param>
/// <param name="InstallPath">Game folder.</param>
/// <param name="LibraryPath">The folder the game was installed into (its parent), shown and grouped in Settings → Libraries.</param>
/// <param name="Version">Installed version as the store records it (<see cref="GameVersion.FromBuildId"/>), <c>null</c> when unknown.</param>
/// <param name="MainExecutable">Main executable relative to <paramref name="InstallPath"/>, '/'-separated; <c>null</c> when unknown.</param>
/// <param name="App">The app that installed it (<see cref="StoreApps"/>).</param>
/// <param name="IsAvailable">The game folder exists right now (a store lists games on unplugged drives too).</param>
public sealed record StoreGame(
    GameId Id,
    string Name,
    string InstallPath,
    string LibraryPath,
    string? Version,
    string? MainExecutable,
    string App,
    bool IsAvailable = true);

/// <summary>Result of <see cref="IGameStore.Scan"/>.</summary>
/// <param name="Source">The store.</param>
/// <param name="Found">
/// Some of the store's records were found on this computer (its app, a list of installs, an install's info file); a store
/// that isn't installed is not an error.
/// </param>
/// <param name="ReadFrom">What was read, for Settings → Libraries ("Reads …"): a folder, file or registry key; <c>null</c> when nothing was found.</param>
/// <param name="Games">Installed games, first entry per ID, sorted by name.</param>
/// <param name="Problem">
/// The store was found but its list couldn't be read (a locked or broken file): its games already in the catalog stay as
/// unavailable instead of being removed. <c>null</c> = read fine.
/// </param>
/// <param name="MissingFolders">
/// Game folders the store lists that aren't there right now and whose game it can't name without them (the EA app
/// keeps the ID in the folder): a game the catalog already has there stays as unavailable.
/// </param>
public sealed record StoreScan(
    GameSource Source,
    bool Found,
    string? ReadFrom,
    IReadOnlyList<StoreGame> Games,
    string? Problem = null,
    IReadOnlyList<string>? MissingFolders = null)
{
    public static StoreScan NotFound(GameSource source) => new(source, false, null, []);

    /// <summary>The folders games were installed into, with how many games each holds and whether it is there right now.</summary>
    public IReadOnlyList<StoreFolder> Folders =>
    [
        .. Games
            .GroupBy(g => g.LibraryPath, Storage.PathNames.Comparer)
            .Select(g => new StoreFolder(g.Key, g.Count(), g.Any(x => x.IsAvailable), g.First().App))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase),
    ];
}

/// <summary>A folder a store installed games into.</summary>
/// <param name="Path">The folder.</param>
/// <param name="Games">How many of the store's games are in it.</param>
/// <param name="IsAvailable">At least one of its games is there right now (not on an unplugged drive).</param>
/// <param name="App">The app that installed its first game (<see cref="StoreApps"/>).</param>
public sealed record StoreFolder(string Path, int Games, bool IsAvailable, string App);
