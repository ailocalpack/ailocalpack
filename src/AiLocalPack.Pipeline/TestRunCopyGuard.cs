namespace AiLocalPack.Pipeline;

/// <summary>
/// What a test run's game writes into the working copy itself (caches, settings, saves, logs), undone so the copy stays
/// the game as extracted. <see cref="Take"/> notes every file's size and last write time before the game runs and saves
/// the copy's own versions of the files the game is known to write (earlier runs of the game told which) into the job's
/// saved originals (<see cref="Core.Generation.WorkingCopies.SavedOriginalsFolder"/>), once per copy. <see cref="Undo"/>
/// deletes the files the game added and takes the ones it changed or deleted back from the saved originals, else from the
/// game folder the copy was made from when that file is still the one the copy had (saved then too). The game folder
/// alone isn't enough: the player's own sessions rewrite the same files there (Barony's compiled books, its settings).
/// Copies keep the last write time. Links are never followed or touched. Best effort: never throws for disk state; what
/// can't be put back is logged.
/// </summary>
internal sealed class TestRunCopyGuard
{
    /// <summary>A file bigger than this isn't saved ahead of a run (a save or cache that size is left to the game folder).</summary>
    internal const long MaxSavedFile = 64L * 1024 * 1024;

    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    private readonly string _copy;
    private readonly string _saved;
    private readonly Dictionary<string, (long Length, DateTime Written)> _files;
    private readonly HashSet<string> _folders;

    private TestRunCopyGuard(string copy, string saved, Dictionary<string, (long Length, DateTime Written)> files, HashSet<string> folders)
    {
        _copy = copy;
        _saved = saved;
        _files = files;
        _folders = folders;
    }

    /// <summary>The copy's files as they are now; the ones in <paramref name="known"/> saved unless they already are.</summary>
    /// <param name="savedFolder">The job's saved originals.</param>
    /// <param name="known">Files earlier runs of this game wrote (relative, '/'-separated).</param>
    public static TestRunCopyGuard Take(string copyFolder, string savedFolder, IEnumerable<string> known)
    {
        ArgumentNullException.ThrowIfNull(known);
        var (files, folders) = List(copyFolder);
        var guard = new TestRunCopyGuard(copyFolder, savedFolder, files, folders);
        foreach (var file in known)
        {
            var relative = Native(file);
            if (relative is not null && files.TryGetValue(relative, out var state) && state.Length <= MaxSavedFile)
            {
                guard.Save(Path.Combine(copyFolder, relative), relative, state.Written);
            }
        }

        return guard;
    }

    /// <summary>Puts the copy back as <see cref="Take"/> saw it.</summary>
    /// <param name="gameFolder">The game folder the copy was made from.</param>
    public TestRunCopyUndo Undo(string gameFolder, Action<string> log)
    {
        var (now, nowFolders) = List(_copy);
        var left = new List<string>();
        var written = new List<string>();
        var undone = 0;
        foreach (var (relative, state) in now)
        {
            if (!_files.TryGetValue(relative, out var before))
            {
                undone += Try(() => File.Delete(Path.Combine(_copy, relative)), relative, left);
            }
            else if (state != before)
            {
                written.Add(Slashed(relative));
                undone += Restore(gameFolder, relative, before, left);
            }
        }

        foreach (var (relative, before) in _files)
        {
            if (!now.ContainsKey(relative))
            {
                written.Add(Slashed(relative));
                undone += Restore(gameFolder, relative, before, left);
            }
        }

        foreach (var folder in nowFolders.Where(f => !_folders.Contains(f)).OrderByDescending(f => f.Length))
        {
            Try(() => Directory.Delete(Path.Combine(_copy, folder)), folder, null);
        }

        if (undone > 0)
        {
            log($"Test run: undid {undone} file{(undone == 1 ? "" : "s")} the game wrote into the working copy.");
        }

        if (left.Count > 0)
        {
            log($"Test run: the game changed {left.Count} file{(left.Count == 1 ? "" : "s")} of the working copy that can't be put back (no saved original, and the game folder's file differs now): {string.Join(", ", left.Take(10))}{(left.Count > 10 ? ", …" : "")}.");
        }

        return new TestRunCopyUndo(left, written);
    }

    private static (Dictionary<string, (long Length, DateTime Written)> Files, HashSet<string> Folders) List(string folder)
    {
        var files = new Dictionary<string, (long Length, DateTime Written)>(StringComparer.Ordinal);
        var folders = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", Walk))
            {
                var relative = Path.GetRelativePath(folder, info.FullName);
                if (info is FileInfo file)
                {
                    files[relative] = (file.Length, file.LastWriteTimeUtc);
                }
                else
                {
                    folders.Add(relative);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A copy that can't be read can't be guarded; Undo then changes nothing it doesn't know.
        }

        return (files, folders);
    }

    private int Restore(string gameFolder, string relative, (long Length, DateTime Written) before, List<string> left)
    {
        var target = Path.Combine(_copy, relative);
        var saved = Path.Combine(_saved, relative);
        if (File.Exists(saved))
        {
            return Try(() => CopyKeepingTime(saved, target, File.GetLastWriteTimeUtc(saved)), relative, left);
        }

        var source = Path.Combine(gameFolder, relative);
        try
        {
            var info = new FileInfo(source);
            if (!info.Exists || info.Length != before.Length || info.LastWriteTimeUtc != before.Written)
            {
                left.Add(Slashed(relative));
                return 0;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            left.Add(Slashed(relative));
            return 0;
        }

        var done = Try(() => CopyKeepingTime(source, target, before.Written), relative, left);
        if (done > 0 && before.Length <= MaxSavedFile)
        {
            Save(target, relative, before.Written);
        }

        return done;
    }

    /// <summary>Saves the copy's file as its original unless one is saved already (the first one saved is the copy's own).</summary>
    private void Save(string source, string relative, DateTime written)
    {
        var saved = Path.Combine(_saved, relative);
        if (File.Exists(saved))
        {
            return;
        }

        var temp = saved + ".tmp";
        try
        {
            CopyKeepingTime(source, temp, written);
            File.Move(temp, saved);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not saved: this file is taken from the game folder again, when it still can be.
            try
            {
                File.Delete(temp);
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void CopyKeepingTime(string source, string target, DateTime written)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
        File.SetLastWriteTimeUtc(target, written);
    }

    /// <summary>A game-relative path in this OS's form, or <c>null</c> for one that leaves the copy.</summary>
    private static string? Native(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            return null;
        }

        var parts = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 || parts.Any(p => p is "." or "..") ? null : Path.Combine(parts);
    }

    private static string Slashed(string relative) => relative.Replace('\\', '/');

    private static int Try(Action action, string relative, List<string>? left)
    {
        try
        {
            action();
            return 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            left?.Add(Slashed(relative));
            return 0;
        }
    }
}

/// <summary>What <see cref="TestRunCopyGuard.Undo"/> did.</summary>
/// <param name="Left">Files it couldn't put back (relative, '/'-separated).</param>
/// <param name="Written">Files of the copy the game changed or deleted (relative, '/'-separated): saved ahead of the game's next runs.</param>
internal sealed record TestRunCopyUndo(IReadOnlyList<string> Left, IReadOnlyList<string> Written);
