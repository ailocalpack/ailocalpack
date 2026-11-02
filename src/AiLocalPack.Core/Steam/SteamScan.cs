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
/// A Steam library folder (holds <c>steamapps/</c>). Not available = listed by Steam but missing right now
/// (unplugged drive, removed SD card): not an error, its games are simply not listed.
/// </summary>
public sealed record SteamLibrary(string Path, bool IsAvailable);

/// <summary>Result of <see cref="SteamLibraryScanner.Scan"/>: libraries in Steam's order, games sorted by name.</summary>
public sealed record SteamScan(IReadOnlyList<SteamLibrary> Libraries, IReadOnlyList<SteamGame> Games)
{
    public static SteamScan Empty { get; } = new([], []);
}
