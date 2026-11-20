namespace AiLocalPack.Core.Games;

/// <summary>Where a game in the catalog came from.</summary>
public enum GameSource
{
    /// <summary>Found by the Steam library scan; added and removed by Steam, not by the player.</summary>
    Steam,

    /// <summary>A folder the player added; stays until the player removes it.</summary>
    Manual,

    /// <summary>Found in the Epic Games Launcher's or Heroic's list of installed games (task 84).</summary>
    Epic,

    /// <summary>Found by GOG Galaxy's records, a GOG installer's info file or Heroic (task 85).</summary>
    Gog,

    /// <summary>Found in the EA app's installs (formerly Origin; task 86).</summary>
    Ea,
}

/// <summary>How sources and ID kinds go together: one kind per source (the catalog refuses other pairs).</summary>
public static class GameSources
{
    /// <summary>Every source in the order the UI lists stores (Steam first, then the other stores; manual last).</summary>
    public static IReadOnlyList<GameSource> Stores { get; } = [GameSource.Steam, GameSource.Epic, GameSource.Gog, GameSource.Ea];

    /// <summary>The ID kind games of <paramref name="source"/> have.</summary>
    public static GameIdKind KindOf(GameSource source) => source switch
    {
        GameSource.Steam => GameIdKind.Steam,
        GameSource.Manual => GameIdKind.Fingerprint,
        GameSource.Epic => GameIdKind.Epic,
        GameSource.Gog => GameIdKind.Gog,
        GameSource.Ea => GameIdKind.Ea,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    /// <summary>The source of games with <paramref name="kind"/> IDs.</summary>
    public static GameSource SourceOf(GameIdKind kind) => kind switch
    {
        GameIdKind.Steam => GameSource.Steam,
        GameIdKind.Fingerprint => GameSource.Manual,
        GameIdKind.Epic => GameSource.Epic,
        GameIdKind.Gog => GameSource.Gog,
        GameIdKind.Ea => GameSource.Ea,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>The store's name in English (registry texts, CLI): Steam, Epic Games, GOG, EA app; "added manually".</summary>
    public static string EnglishName(GameSource source) => source switch
    {
        GameSource.Steam => "Steam",
        GameSource.Epic => "Epic Games",
        GameSource.Gog => "GOG",
        GameSource.Ea => "EA app",
        _ => "added manually",
    };

    /// <summary>Canonical text of a source (<c>steam</c>, <c>manual</c>, <c>epic</c>, <c>gog</c>, <c>ea</c>), as stored and printed.</summary>
    public static string Name(GameSource source) => source.ToString().ToLowerInvariant();

    public static bool TryParse(string? text, out GameSource source)
    {
        source = default;
        var match = Enum.GetValues<GameSource>().Where(s => Name(s) == text).ToList();
        if (match.Count == 0)
        {
            return false;
        }

        source = match[0];
        return true;
    }
}

/// <summary>One game of the catalog (<see cref="GameCatalog"/>).</summary>
/// <param name="Id">Stable identity across machines.</param>
/// <param name="Name">Display name (the store's name, or the name the player gave a manual game).</param>
/// <param name="Source">The store whose scan found it, or added by hand.</param>
/// <param name="InstallPath">Game folder.</param>
/// <param name="LibraryPath">
/// Steam library holding the game, or the folder another store installed it into (its parent); <c>null</c> for manual games.
/// </param>
/// <param name="Version">Installed build; <c>null</c> when unknown (no build ID, file hashes not computed yet).</param>
/// <param name="Engine">
/// Detected engine ID from the toolkit's <c>EngineKind</c> (<c>unity:il2cpp</c>, <c>unreal:5</c>, <c>rpgmaker:mz</c>,
/// <c>renpy</c>, <c>godot:4</c>, <c>unknown</c>); <c>null</c> = not detected yet.
/// </param>
/// <param name="AntiCheat">
/// Anti-cheat ID found in the game files (<c>AntiCheatPolicy</c> rules: <c>eac</c>, <c>battleye</c>…), <c>none</c> when the
/// check found nothing, <c>null</c> = not checked yet. Whether it blocks the game is decided by <c>AntiCheatPolicy.Resolve</c>
/// (known-games list, whitelist).
/// </param>
/// <param name="LastPlayed">Last launch, or <c>null</c> when never played or unknown.</param>
/// <param name="IsAvailable">
/// <c>false</c> while the game folder can't be reached (Steam library on an unplugged drive or SD card, Steam not found,
/// manual folder missing). The game stays in the catalog with everything known about it.
/// </param>
/// <param name="MainExecutable">
/// The main executable (relative to <paramref name="InstallPath"/>, '/'-separated): a manual game's as detected or
/// chosen when it was added, another store's as its records name it; the one launched first when it runs on this OS.
/// <c>null</c> for Steam games (Steam starts them) and when unknown.
/// </param>
/// <param name="Platform">
/// The OS the installed game files are for, from its executables (a Windows build under Proton on Linux is
/// <see cref="Storage.OsKind.Windows"/>); found with the engine, <c>null</c> while unknown. A Steam build ID is the same
/// on every OS, so a pack's build matches only on the same platform (<c>PackBuild.Matches</c>).
/// </param>
/// <param name="App">
/// The app that installed a store game when the store has more than one (<see cref="StoreApps"/>: the Epic Games
/// Launcher or Heroic…); it decides how the game starts. <c>null</c> for Steam and manual games.
/// </param>
public sealed record Game(
    GameId Id,
    string Name,
    GameSource Source,
    string InstallPath,
    string? LibraryPath,
    GameVersion? Version,
    string? Engine,
    string? AntiCheat,
    DateTimeOffset? LastPlayed,
    bool IsAvailable,
    string? MainExecutable = null,
    Storage.OsKind? Platform = null,
    string? App = null);

/// <summary>Canonical IDs of the apps that install store games (<see cref="Game.App"/>).</summary>
public static class StoreApps
{
    public const string EpicLauncher = "epic-launcher";
    public const string Heroic = "heroic";
    public const string Legendary = "legendary";
    public const string GogGalaxy = "galaxy";
    public const string GogInstaller = "gog-installer";
    public const string EaApp = "ea-app";
}

/// <summary>Something that happened to a game when the catalog changed.</summary>
public abstract record GameChange(Game Game);

/// <summary>A game appeared (new Steam install, folder added by the player).</summary>
public sealed record GameAdded(Game Game) : GameChange(Game);

/// <summary>A game left the catalog (uninstalled in Steam, removed by the player). <see cref="GameChange.Game"/> is its last state.</summary>
public sealed record GameRemoved(Game Game) : GameChange(Game);

/// <summary>The installed build changed (store update): installed packs may need a re-check (task 33).</summary>
public sealed record GameVersionChanged(Game Game, GameVersion Previous) : GameChange(Game);

/// <summary>Arguments of <see cref="GameCatalog.Changed"/>: the catalog after the change and what happened to which game.</summary>
public sealed class GameCatalogChangedEventArgs(IReadOnlyList<Game> games, IReadOnlyList<GameChange> changes) : EventArgs
{
    public IReadOnlyList<Game> Games { get; } = games;

    /// <summary>Added / removed / version-changed games; empty when only other details changed (name, last played, availability).</summary>
    public IReadOnlyList<GameChange> Changes { get; } = changes;
}
