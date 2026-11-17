namespace AiLocalPack.Pipeline;

/// <summary>
/// What a test run's game writes into the working copy itself (caches, settings, saves, logs), undone so the copy stays
/// the game as extracted: <see cref="Take"/> notes every file's size and last write time before the game runs,
/// <see cref="Undo"/> deletes the files it added and takes the ones it changed or deleted back from the game folder the
/// copy was made from (copies keep the last write time), when the game folder's file is still the one the copy had.
/// Links are never followed or touched. Best effort: never throws for disk state; what can't be put back is logged.
/// </summary>
internal sealed class TestRunCopyGuard
{
    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    private readonly string _copy;
    private readonly Dictionary<string, (long Length, DateTime Written)> _files;
    private readonly HashSet<string> _folders;

    private TestRunCopyGuard(string copy, Dictionary<string, (long, DateTime)> files, HashSet<string> folders)
    {
        _copy = copy;
        _files = files;
        _folders = folders;
    }

    /// <summary>The copy's files as they are now.</summary>
    public static TestRunCopyGuard Take(string copyFolder)
    {
        var files = new Dictionary<string, (long, DateTime)>(StringComparer.Ordinal);
        var folders = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var info in new DirectoryInfo(copyFolder).EnumerateFileSystemInfos("*", Walk))
            {
                var relative = Path.GetRelativePath(copyFolder, info.FullName);
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

        return new TestRunCopyGuard(copyFolder, files, folders);
    }

    /// <summary>Puts the copy back as <see cref="Take"/> saw it; returns the files that couldn't be (relative, '/'-separated).</summary>
    /// <param name="gameFolder">The game folder the copy was made from.</param>
    public IReadOnlyList<string> Undo(string gameFolder, Action<string> log)
    {
        var now = Take(_copy);
        var left = new List<string>();
        var undone = 0;
        foreach (var (relative, state) in now._files)
        {
            if (!_files.TryGetValue(relative, out var before))
            {
                undone += Try(() => File.Delete(Path.Combine(_copy, relative)), relative, left);
            }
            else if (state != before)
            {
                undone += Restore(gameFolder, relative, before, left);
            }
        }

        foreach (var (relative, before) in _files)
        {
            if (!now._files.ContainsKey(relative))
            {
                undone += Restore(gameFolder, relative, before, left);
            }
        }

        foreach (var folder in now._folders.Where(f => !_folders.Contains(f)).OrderByDescending(f => f.Length))
        {
            Try(() => Directory.Delete(Path.Combine(_copy, folder)), folder, null);
        }

        if (undone > 0)
        {
            log($"Test run: undid {undone} file{(undone == 1 ? "" : "s")} the game wrote into the working copy.");
        }

        if (left.Count > 0)
        {
            log($"Test run: the game changed {left.Count} file{(left.Count == 1 ? "" : "s")} of the working copy that can't be put back (the game folder's file differs now): {string.Join(", ", left.Take(10))}{(left.Count > 10 ? ", …" : "")}.");
        }

        return left;
    }

    private int Restore(string gameFolder, string relative, (long Length, DateTime Written) before, List<string> left)
    {
        var source = Path.Combine(gameFolder, relative);
        try
        {
            var info = new FileInfo(source);
            if (!info.Exists || info.Length != before.Length || info.LastWriteTimeUtc != before.Written)
            {
                left.Add(relative.Replace('\\', '/'));
                return 0;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            left.Add(relative.Replace('\\', '/'));
            return 0;
        }

        var target = Path.Combine(_copy, relative);
        return Try(
            () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
                File.SetLastWriteTimeUtc(target, before.Written);
            },
            relative,
            left);
    }

    private static int Try(Action action, string relative, List<string>? left)
    {
        try
        {
            action();
            return 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            left?.Add(relative.Replace('\\', '/'));
            return 0;
        }
    }
}
