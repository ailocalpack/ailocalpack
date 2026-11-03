using AiLocalPack.Toolkit.Engines;

namespace AiLocalPack.Toolkit.Folders;

/// <summary>Why <see cref="FolderRecognizer"/> left a folder out.</summary>
public enum SkipReason
{
    /// <summary>No executable or engine marker anywhere inside (down to the depth limit).</summary>
    NoGame,

    /// <summary>Runtime installers: <c>_CommonRedist</c>, DirectX, vcredist, .NET, PhysX…</summary>
    Redistributable,

    /// <summary>Game installers (<c>setup_*.exe</c>, <c>__Installer</c>), not an installed game.</summary>
    Installer,

    /// <summary>Known non-game programs: Steam runtimes, overlays, mod managers…</summary>
    Utility,

    /// <summary>Save game folders.</summary>
    Saves,
}

/// <summary>One game found by <see cref="FolderRecognizer.Scan"/>.</summary>
/// <param name="Path">Full path of the game folder.</param>
/// <param name="RelativePath">Path from the scanned folder, '/'-separated; empty when the scanned folder is the game.</param>
/// <param name="Name">Display name suggestion: the folder name (a macOS bundle without <c>.app</c>).</param>
/// <param name="Engine">Engine detection on the game folder.</param>
/// <param name="MainExecutable">
/// Main executable relative to the game folder ('/'-separated; a macOS <c>.app</c> bundle is a folder), or <c>null</c>.
/// </param>
/// <param name="InSteamLibrary">The game lies in a Steam library (<c>steamapps/common</c>): Steam already lists it.</param>
public sealed record FoundGame(
    string Path, string RelativePath, string Name, EngineDetection Engine, string? MainExecutable, bool InSteamLibrary);

/// <summary>A folder the scan left out, with the reason. Only the topmost folder of a game-less branch is listed.</summary>
/// <param name="RelativePath">Path from the scanned folder, '/'-separated.</param>
public sealed record SkippedFolder(string RelativePath, SkipReason Reason);

/// <summary>Scan progress, reported after each folder and on each game found.</summary>
/// <param name="FoldersScanned">Folders looked at so far.</param>
/// <param name="FoldersFound">Folders known so far (scanned + waiting); grows while the scan goes deeper.</param>
/// <param name="GamesFound">Games found so far.</param>
/// <param name="Game">The game just found, or <c>null</c> for a plain progress tick.</param>
public sealed record FolderScanProgress(int FoldersScanned, int FoldersFound, int GamesFound, FoundGame? Game);

/// <summary>Limits of a <see cref="FolderRecognizer"/> scan.</summary>
public sealed record FolderScanOptions
{
    public static FolderScanOptions Default { get; } = new();

    /// <summary>Deepest folder level where a game is looked for (the scanned folder is 0).</summary>
    public int MaxDepth { get; init; } = 3;

    /// <summary>Files and folders listed at most before the scan stops and asks for a narrower folder.</summary>
    public int MaxEntries { get; init; } = 100_000;
}

/// <summary>Result of <see cref="FolderRecognizer.Scan"/>.</summary>
/// <param name="Folder">The scanned folder (full path).</param>
/// <param name="Games">Games found, ordered by relative path.</param>
/// <param name="Skipped">Folders left out, ordered by relative path.</param>
/// <param name="LimitReached">The scan stopped at <see cref="FolderScanOptions.MaxEntries"/>: the lists are partial.</param>
/// <param name="Stopped">The scan was cancelled: the lists hold what was found until then.</param>
public sealed record FolderScan(
    string Folder, IReadOnlyList<FoundGame> Games, IReadOnlyList<SkippedFolder> Skipped, bool LimitReached, bool Stopped)
{
    /// <summary>Every game found lies in a Steam library ("these games are already in your library via Steam").</summary>
    public bool IsSteamLibrary => Games.Count > 0 && Games.All(g => g.InSteamLibrary);
}
