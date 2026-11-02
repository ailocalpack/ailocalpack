namespace AiLocalPack.Core.Games;

/// <summary>Where a game in the catalog came from.</summary>
public enum GameSource
{
    /// <summary>Found by the Steam library scan; added and removed by Steam, not by the player.</summary>
    Steam,

    /// <summary>A folder the player added; stays until the player removes it.</summary>
    Manual,
}

/// <summary>One game of the catalog (<see cref="GameCatalog"/>).</summary>
/// <param name="Id">Stable identity across machines.</param>
/// <param name="Name">Display name (Steam manifest name, or the name the player gave a manual game).</param>
/// <param name="Source">Steam scan or added by hand.</param>
/// <param name="InstallPath">Game folder.</param>
/// <param name="LibraryPath">Steam library holding the game; <c>null</c> for manual games.</param>
/// <param name="Version">Installed build; <c>null</c> when unknown (no build ID, file hashes not computed yet).</param>
/// <param name="Engine">Detected engine (task 09); <c>null</c> = not detected yet.</param>
/// <param name="AntiCheat">Detected anti-cheat display name (task 10); <c>null</c> = none found or not checked yet.</param>
/// <param name="LastPlayed">Last launch, or <c>null</c> when never played or unknown.</param>
/// <param name="IsAvailable">
/// <c>false</c> while the game folder can't be reached (Steam library on an unplugged drive or SD card, Steam not found,
/// manual folder missing). The game stays in the catalog with everything known about it.
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
    bool IsAvailable);

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
