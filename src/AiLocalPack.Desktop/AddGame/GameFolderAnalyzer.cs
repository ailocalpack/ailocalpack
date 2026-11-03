using AiLocalPack.Core.Games;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.AntiCheat;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Folders;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.AddGame;

/// <summary>Everything the add-folder dialog shows about one game found in a folder (design §2, step 2).</summary>
/// <param name="Game">The game as the folder scan found it (path, name, engine, main executable).</param>
/// <param name="Id">Fingerprint ID (<c>fp:…</c>) computed from the detected engine and the main executable.</param>
/// <param name="Version">File-hash version, or <c>null</c> when there are no version files.</param>
/// <param name="AntiCheat">Anti-cheats found in the game files.</param>
/// <param name="Size">Files and bytes in the game folder.</param>
/// <param name="Executables">Executables the player can pick as the main one (the detected one first).</param>
/// <param name="Icon">The game's icon, or <c>null</c>.</param>
public sealed record GameAnalysis(
    FoundGame Game,
    GameId Id,
    GameVersion? Version,
    AntiCheatDetection AntiCheat,
    FolderSize Size,
    IReadOnlyList<string> Executables,
    GameIcon? Icon);

/// <summary>Hashing progress of <see cref="GameFolderAnalyzer.Analyze"/>.</summary>
public enum AnalysisStage
{
    /// <summary>Anti-cheat check, executables, folder size, icon.</summary>
    Details,

    /// <summary>Hashing the version files and the fingerprint (progress = files).</summary>
    Fingerprint,
}

/// <param name="FilesDone">Files hashed (fingerprint stage).</param>
/// <param name="FilesTotal">Files to hash (fingerprint stage).</param>
public readonly record struct AnalysisProgress(AnalysisStage Stage, int FilesDone, int FilesTotal);

/// <summary>
/// The add-folder dialog's work, off the UI thread: finds the games in a folder (<see cref="FolderRecognizer"/>), then for
/// one game checks anti-cheats, lists executables, measures the folder, extracts the icon and computes the fingerprint
/// and version (hashes cached in <see cref="FileHashCache"/>, saved after each analysis).
/// </summary>
public sealed class GameFolderAnalyzer(
    FolderRecognizer recognizer,
    GameFingerprinter fingerprinter,
    AntiCheatDetector antiCheats,
    GameIconExtractor icons,
    FileHashCache hashes,
    ILogger<GameFolderAnalyzer> logger)
{
    /// <summary>Games in <paramref name="folder"/> (it may be one itself). Never throws for disk state.</summary>
    public FolderScan Scan(string folder, IProgress<FolderScanProgress>? progress = null, CancellationToken cancellationToken = default) =>
        recognizer.Scan(folder, FolderScanOptions.Default, progress, cancellationToken);

    /// <summary>
    /// Analyzes one found game. Throws <see cref="IOException"/> when a file needed for the fingerprint can't be read and
    /// <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public GameAnalysis Analyze(FoundGame game, IProgress<AnalysisProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        progress?.Report(new AnalysisProgress(AnalysisStage.Details, 0, 0));
        var antiCheat = antiCheats.Detect(game.Path);
        cancellationToken.ThrowIfCancellationRequested();
        var executables = FolderRecognizer.ListExecutables(game.Path, game.MainExecutable);
        var size = FolderSize.Measure(game.Path, cancellationToken);
        var icon = icons.Extract(game.Path, game.MainExecutable);
        var (id, version) = Identify(game.Path, game.Engine.Kind, game.MainExecutable, progress, cancellationToken);
        logger.LogInformation(
            "Analyzed {Folder}: {Id}, {Engine}, {Executable}, {Files} files, anti-cheat {AntiCheat}",
            game.Path, id, game.Engine.Kind, game.MainExecutable, size.Files, antiCheat.StoredId);
        return new GameAnalysis(game, id, version, antiCheat, size, executables, icon);
    }

    /// <summary>
    /// Fingerprint and version for a main executable (again when the player picks another one). The version goes first:
    /// it hashes the most files, and the fingerprint then reuses the cached executable hash.
    /// </summary>
    public (GameId Id, GameVersion? Version) Identify(
        string gameFolder,
        EngineKind engine,
        string? mainExecutable,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var hashing = progress is null
            ? null
            : new SyncProgress<HashProgress>(p => progress.Report(new AnalysisProgress(AnalysisStage.Fingerprint, p.FilesDone, p.FilesTotal)));
        try
        {
            var version = fingerprinter.Version(gameFolder, engine, mainExecutable, hashing, cancellationToken);
            var id = fingerprinter.Fingerprint(gameFolder, engine, mainExecutable, hashing, cancellationToken);
            return (id, version);
        }
        finally
        {
            hashes.Save();
        }
    }

    /// <summary>Reports on the calling thread (<see cref="Progress{T}"/> would post to a captured context).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
