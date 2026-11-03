namespace AiLocalPack.Core.Steam;

/// <summary>A fully installed Steam game.</summary>
/// <param name="AppId">Steam app ID (<c>steam:&lt;AppId&gt;</c> is the game identity).</param>
/// <param name="Name">Name from the app manifest.</param>
/// <param name="BuildId">Installed build (<c>buildid</c>), or <c>null</c> when the manifest has none.</param>
/// <param name="InstallPath">Game folder: <c>&lt;library&gt;/steamapps/common/&lt;installdir&gt;</c>.</param>
/// <param name="LibraryPath">The library folder the game is installed in.</param>
/// <param name="LastPlayed">Last launch, or <c>null</c> when never played or unknown.</param>
public sealed record SteamGame(
    uint AppId,
    string Name,
    string? BuildId,
    string InstallPath,
    string LibraryPath,
    DateTimeOffset? LastPlayed);

/// <summary>
/// A Steam library folder (holds <c>steamapps/</c>). Not available = listed but missing right now (unplugged drive,
/// removed SD card): not an error, its games are simply not listed.
/// </summary>
/// <param name="Path">Library folder (the one holding <c>steamapps/</c>).</param>
/// <param name="IsAvailable">The folder holds <c>steamapps/</c> right now.</param>
/// <param name="IsAddedByHand">
/// Added by the player (Settings → Libraries) rather than found through Steam (a root or <c>libraryfolders.vdf</c>).
/// </param>
public sealed record SteamLibrary(string Path, bool IsAvailable, bool IsAddedByHand = false);

/// <summary>
/// The player's changes to the Steam library list (<c>AppSettings.SteamLibraryFolders</c> / <c>IgnoredSteamLibraries</c>):
/// folders added by hand (a library on a drive Steam doesn't list) and folders Steam lists that the player removed.
/// </summary>
public sealed record SteamLibraryChoices(IReadOnlyList<string> Added, IReadOnlyList<string> Ignored)
{
    public static SteamLibraryChoices None { get; } = new([], []);
}

/// <summary>
/// Result of <see cref="SteamLibraryScanner.Scan()"/>: libraries in Steam's order (folders added by hand last), games
/// sorted by name.
/// </summary>
public sealed record SteamScan(IReadOnlyList<SteamLibrary> Libraries, IReadOnlyList<SteamGame> Games)
{
    public static SteamScan Empty { get; } = new([], []);

    /// <summary>Steam itself was found (a root or a library it lists); folders added by hand alone don't count.</summary>
    public bool SteamFound => Libraries.Any(l => !l.IsAddedByHand);
}
