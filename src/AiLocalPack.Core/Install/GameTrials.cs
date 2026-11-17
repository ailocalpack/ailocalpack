using System.Security.Cryptography;
using System.Text.Json;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Core.Install;

/// <summary>How <see cref="GameTrials.Recover"/> ended.</summary>
/// <param name="GameFolder">The game folder a crashed trial was put back in, when there was one.</param>
public sealed record TrialRecovery(bool Busy, string? GameFolder = null, GameWriteUndo? Undo = null)
{
    public static TrialRecovery Nothing { get; } = new(false);
}

/// <summary>
/// Test runs on the live game folder (_Live game folder_ in <c>docs/generation.md</c>), as transactions like an
/// install's: a trial holds the install lock (<see cref="InstallLock"/>, so no install, uninstall or other trial runs
/// meanwhile, in the app or the CLI) and writes <see cref="AppPaths.TrialJournal"/> before anything changes; its own
/// folder (<c>&lt;data&gt;/trials/&lt;id&gt;/</c>) keeps the game's write snapshot and backups (<see cref="GameWriteGuard"/>)
/// and what it laid over the game (<see cref="TestRunOverlay"/>: an installed pack's files are moved aside like the
/// game's own and put back). Disposing the trial undoes both, then deletes its folder and the journal. A trial a crash
/// left is undone by <see cref="Recover"/> (start-up, through <see cref="PackInstaller.Recover"/>, and before every trial).
/// </summary>
public sealed class GameTrials(AppPaths paths, IFileCloner? cloner = null, ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>
    /// Opens a trial on <paramref name="gameFolder"/>: the lock, a crashed trial undone, the journal, the write guard over
    /// the game folder and <paramref name="dataRoots"/>. <c>null</c> = another transaction holds the lock, or an install
    /// left by a crash isn't finished yet.
    /// </summary>
    /// <param name="known">Keys of files the game wrote in earlier runs (<see cref="GameWriteGuard.Key"/>).</param>
    /// <exception cref="IOException">The journal or the trial folder can't be written (nothing is changed, the lock is released).</exception>
    public GameTrial? TryOpen(string gameFolder, IReadOnlyList<GameWriteRoot> dataRoots, IEnumerable<string> known, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);
        ArgumentNullException.ThrowIfNull(dataRoots);
        ArgumentNullException.ThrowIfNull(known);
        var gate = InstallLock.TryAcquire(paths);
        if (gate is null)
        {
            return null;
        }

        try
        {
            RecoverLocked();
            if (File.Exists(paths.InstallJournal))
            {
                gate.Dispose();
                return null;
            }

            var id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));
            var folder = Path.Combine(paths.Trials, id);
            var game = Path.GetFullPath(gameFolder);
            WriteJournal(game, folder);
            Directory.CreateDirectory(folder);
            var guard = GameWriteGuard.Take(folder, [GameWriteRoot.Game(game), .. dataRoots], known, cloner, log);
            return new GameTrial(this, gate, game, folder, guard, log);
        }
        catch
        {
            gate.Dispose();
            throw;
        }
    }

    /// <summary>Undoes a trial a crash left (nothing when there is none). Never throws for disk state.</summary>
    public TrialRecovery Recover()
    {
        using var gate = InstallLock.TryAcquire(paths);
        return gate is null ? new TrialRecovery(Busy: true) : RecoverLocked();
    }

    /// <summary>A trial left by a crash is there to undo. Never throws.</summary>
    public bool HasInterruptedTrial => File.Exists(paths.TrialJournal);

    /// <summary><see cref="Recover"/> with the lock held.</summary>
    internal TrialRecovery RecoverLocked()
    {
        if (ReadJournal() is not { } journal)
        {
            return TrialRecovery.Nothing;
        }

        var (game, folder) = journal;

        try
        {
            var undo = Finish(game, folder, null);
            _logger.LogWarning("Undid the test run a crash left on {Game}: {Undone} files put back, {Left} left as they are", game, undo.Undone, undo.Left.Count);
            return new TrialRecovery(false, game, undo);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(e, "Undoing the test run a crash left on {Game} failed; tried again on the next start", game);
            return new TrialRecovery(false, game);
        }
    }

    /// <summary>Puts the game back (the laid files, then its own writes) and drops the trial's folder and journal.</summary>
    /// <exception cref="IOException">A laid file can't be moved back (the journal stays, so it is tried again).</exception>
    internal GameWriteUndo Finish(string game, string folder, GameWriteGuard? guard)
    {
        TestRunOverlay.Recover(folder, game);
        var undo = (guard ?? GameWriteGuard.Load(folder))?.Undo() ?? GameWriteUndo.None;
        DeleteTree(folder);
        File.Delete(paths.TrialJournal);
        return undo;
    }

    private void WriteJournal(string game, string folder)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("gameFolder", game);
            writer.WriteString("folder", folder);
            writer.WriteEndObject();
        }

        Directory.CreateDirectory(paths.Data);
        AtomicFile.WriteAllBytes(paths.TrialJournal, buffer.ToArray());
    }

    private (string Game, string Folder)? ReadJournal()
    {
        try
        {
            if (!File.Exists(paths.TrialJournal))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(paths.TrialJournal));
            var root = document.RootElement;
            var trials = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.Trials)) + Path.DirectorySeparatorChar;
            return root.GetProperty("schemaVersion").GetInt32() == 1
                && root.GetProperty("gameFolder").GetString() is { Length: > 0 } game && Path.IsPathFullyQualified(game)
                && root.GetProperty("folder").GetString() is { Length: > 0 } folder && Path.GetFullPath(folder).StartsWith(trials, StringComparison.OrdinalIgnoreCase)
                ? (game, folder)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            _logger.LogError(e, "The test run journal {File} can't be read; it is left for a look by hand", paths.TrialJournal);
            return null;
        }
    }

    private static void DeleteTree(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(folder, recursive: true);
    }
}

/// <summary>
/// One test run on the live game folder, open from <see cref="GameTrials.TryOpen"/> until disposed (which undoes it).
/// Between runs of the game: <see cref="Lay"/> the build over it, then <see cref="Undo"/>.
/// </summary>
public sealed class GameTrial : IDisposable
{
    private readonly GameTrials _owner;
    private readonly FileStream _gate;
    private readonly string _folder;
    private readonly GameWriteGuard _guard;
    private readonly Action<string>? _log;
    private TestRunOverlay? _overlay;
    private bool _disposed;

    internal GameTrial(GameTrials owner, FileStream gate, string gameFolder, string folder, GameWriteGuard guard, Action<string>? log)
    {
        _owner = owner;
        _gate = gate;
        GameFolder = gameFolder;
        _folder = folder;
        _guard = guard;
        _log = log;
    }

    public string GameFolder { get; }

    /// <summary>Lays <paramref name="buildFiles"/> of <paramref name="buildFolder"/> over the game (the game's own moved aside).</summary>
    /// <param name="extra">Files the caller writes itself next (<c>steam_appid.txt</c>), deleted on <see cref="Undo"/>.</param>
    /// <exception cref="IOException">A file can't be moved or copied (what was done is undone first).</exception>
    public void Lay(string buildFolder, IReadOnlyList<string> buildFiles, IReadOnlyList<string> extra)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Undo();
        _overlay = TestRunOverlay.Apply(_folder, GameFolder, buildFolder, buildFiles, extra);
    }

    /// <summary>The game as before the trial: the laid files taken off, what the game wrote undone.</summary>
    /// <exception cref="IOException">A laid file can't be moved back.</exception>
    public GameWriteUndo Undo()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _overlay?.Restore();
        _overlay = null;
        TestRunOverlay.Recover(_folder, GameFolder);
        return _guard.Undo(_log);
    }

    /// <summary>Tests: releases the lock and leaves everything else as a crash would.</summary>
    internal void Abandon()
    {
        _disposed = true;
        _gate.Dispose();
    }

    /// <summary>Undoes the trial, drops its folder and journal, and releases the lock. A failure keeps the journal for the next start.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _owner.Finish(GameFolder, _folder, _guard);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Test run: the game folder couldn't be put back yet ({e.Message}); the app tries again on its next start.");
        }
        finally
        {
            _gate.Dispose();
        }
    }
}
