using System.Text;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Engines;

namespace AiLocalPack.Toolkit.Generation;

/// <summary>The end of a game's log file (task 75: attached to a fix request after a crash).</summary>
/// <param name="Path">The file.</param>
/// <param name="Lines">Its last lines, oldest first.</param>
public sealed record GameLogTail(string Path, IReadOnlyList<string> Lines)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Finds the log a game wrote during a session (task 75), best effort, never throws for disk state. Candidates: the
/// engine's own place (Unity's <c>Player.log</c> under the company and product of <c>app.info</c>; Godot's
/// <c>app_userdata/*/logs/godot.log</c>; a packaged Unreal game's <c>Saved/Logs</c>, on Windows under
/// <c>%LOCALAPPDATA%</c>), then any <c>*.log</c>, <c>log.txt</c>, <c>traceback.txt</c>, <c>errors.txt</c> or
/// <c>crash*.txt</c> in the game folder (4 levels deep, 5 000 entries at most: Ren'Py, RPG Maker, custom engines). Only
/// files written since the session started count (a minute's slack); the newest wins. Its last <see cref="MaxLines"/>
/// lines are read from at most the last <see cref="MaxBytes"/>.
/// </summary>
public static class GameLogs
{
    public const int MaxLines = 200;

    public const int MaxBytes = 64 * 1024;

    private const int MaxDepth = 4;
    private const int MaxEntries = 5_000;
    private static readonly TimeSpan Slack = TimeSpan.FromMinutes(1);

    /// <summary>The newest log written since <paramref name="since"/>, or <c>null</c>.</summary>
    public static GameLogTail? Find(string gameFolder, GameEngine engine, OsEnvironment environment, DateTimeOffset since)
    {
        ArgumentException.ThrowIfNullOrEmpty(gameFolder);
        ArgumentNullException.ThrowIfNull(environment);
        var after = since.UtcDateTime - Slack;
        var best = Candidates(gameFolder, engine, environment)
            .Select(path => (path, time: WriteTime(path)))
            .Where(c => c.time is { } t && t >= after)
            .OrderByDescending(c => c.time)
            .FirstOrDefault();
        return best.path is null ? null : Tail(best.path);
    }

    /// <summary>The last <see cref="MaxLines"/> lines of the file, or <c>null</c> when it can't be read.</summary>
    public static GameLogTail? Tail(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - MaxBytes);
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[stream.Length - start];
            stream.ReadExactly(buffer);
            var text = Encoding.UTF8.GetString(buffer).ReplaceLineEndings("\n");
            var lines = text.Split('\n').ToList();
            if (start > 0 && lines.Count > 0)
            {
                lines.RemoveAt(0);
            }

            while (lines.Count > 0 && lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            return new GameLogTail(path, lines.Count > MaxLines ? lines[^MaxLines..] : lines);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static IEnumerable<string> Candidates(string gameFolder, GameEngine engine, OsEnvironment environment)
    {
        var home = environment.HomeDirectory;
        if (engine == GameEngine.Unity && UnityNames(gameFolder) is var (company, product))
        {
            yield return environment.Os switch
            {
                OsKind.Windows => Path.Combine(home, "AppData", "LocalLow", company, product, "Player.log"),
                OsKind.MacOS => Path.Combine(home, "Library", "Logs", company, product, "Player.log"),
                _ => Path.Combine(home, ".config", "unity3d", company, product, "Player.log"),
            };
        }

        if (engine == GameEngine.Godot)
        {
            var userData = environment.Os switch
            {
                OsKind.Windows => Path.Combine(environment.GetVariable("APPDATA") ?? Path.Combine(home, "AppData", "Roaming"), "Godot", "app_userdata"),
                OsKind.MacOS => Path.Combine(home, "Library", "Application Support", "Godot", "app_userdata"),
                _ => Path.Combine(environment.GetVariable("XDG_DATA_HOME") ?? Path.Combine(home, ".local", "share"), "godot", "app_userdata"),
            };
            foreach (var log in Safe(() => Directory.EnumerateDirectories(userData)).Select(d => Path.Combine(d, "logs", "godot.log")))
            {
                yield return log;
            }
        }

        if (engine == GameEngine.Unreal && environment.Os == OsKind.Windows && environment.GetVariable("LOCALAPPDATA") is { } local)
        {
            foreach (var project in Safe(() => Directory.EnumerateDirectories(gameFolder)).Where(d => Directory.Exists(Path.Combine(d, "Content"))))
            {
                foreach (var log in Safe(() => Directory.EnumerateFiles(Path.Combine(local, Path.GetFileName(project), "Saved", "Logs"), "*.log")))
                {
                    yield return log;
                }
            }
        }

        foreach (var file in Walk(gameFolder))
        {
            yield return file;
        }
    }

    /// <summary>Log-like files in the game folder, breadth first, within the depth and entry limits.</summary>
    private static IEnumerable<string> Walk(string root)
    {
        var queue = new Queue<(string Folder, int Depth)>();
        queue.Enqueue((root, 0));
        var seen = 0;
        while (queue.Count > 0 && seen < MaxEntries)
        {
            var (folder, depth) = queue.Dequeue();
            foreach (var entry in Safe(() => new DirectoryInfo(folder).EnumerateFileSystemInfos()))
            {
                if (++seen > MaxEntries)
                {
                    yield break;
                }

                if (entry.LinkTarget is not null)
                {
                    continue;
                }

                if (entry is DirectoryInfo && depth + 1 < MaxDepth)
                {
                    queue.Enqueue((entry.FullName, depth + 1));
                }
                else if (entry is FileInfo && IsLogName(entry.Name))
                {
                    yield return entry.FullName;
                }
            }
        }
    }

    internal static bool IsLogName(string name) =>
        name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
        || name.Equals("log.txt", StringComparison.OrdinalIgnoreCase)
        || name.Equals("traceback.txt", StringComparison.OrdinalIgnoreCase)
        || name.Equals("errors.txt", StringComparison.OrdinalIgnoreCase)
        || (name.StartsWith("crash", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));

    /// <summary>Company and product from the first <c>app.info</c> (its first two lines), as Unity names the log folder.</summary>
    internal static (string Company, string Product)? UnityNames(string gameFolder)
    {
        var info = Safe(() => Directory.EnumerateFiles(gameFolder, "app.info", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4 }))
            .FirstOrDefault();
        if (info is null)
        {
            return null;
        }

        try
        {
            var lines = File.ReadLines(info).Take(2).Select(l => l.Trim()).ToList();
            return lines.Count == 2 && lines.All(l => l.Length > 0 && l.IndexOfAny(Path.GetInvalidFileNameChars()) < 0) ? (lines[0], lines[1]) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTime? WriteTime(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<T> Safe<T>(Func<IEnumerable<T>> list)
    {
        try
        {
            return [.. list()];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }
}
