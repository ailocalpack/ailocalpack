using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Launch;

/// <summary>A running program: process ID and the full path of its executable.</summary>
public sealed record RunningProcess(int Id, string Path);

/// <summary>Finds the processes of a game: those whose executable lies inside the game folder. Pure.</summary>
public static class GameProcessMatcher
{
    /// <summary>
    /// <paramref name="processPath"/> is inside <paramref name="gameFolder"/> (not the folder itself, not a sibling
    /// named alike: <c>Game2</c> isn't inside <c>Game</c>). Case-insensitive on Windows and macOS (their file systems
    /// are by default); Windows accepts either separator and drops the <c>\\?\</c> prefix. Both paths should be
    /// absolute with symlinks resolved (<see cref="PathNames.RealPath"/>).
    /// </summary>
    public static bool IsInside(string processPath, string gameFolder, OsKind os)
    {
        ArgumentNullException.ThrowIfNull(processPath);
        ArgumentNullException.ThrowIfNull(gameFolder);
        var process = Normalize(processPath, os);
        var folder = Normalize(gameFolder, os).TrimEnd('/');
        if (folder.Length == 0)
        {
            return false;
        }

        var comparison = os == OsKind.Linux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return process.Length > folder.Length + 1
            && process[folder.Length] == '/'
            && process.StartsWith(folder, comparison);
    }

    /// <summary>The processes of <paramref name="processes"/> running from inside <paramref name="gameFolder"/>.</summary>
    public static IReadOnlyList<RunningProcess> Find(IEnumerable<RunningProcess> processes, string gameFolder, OsKind os)
    {
        ArgumentNullException.ThrowIfNull(processes);
        return [.. processes.Where(p => IsInside(p.Path, gameFolder, os))];
    }

    private static string Normalize(string path, OsKind os)
    {
        if (os != OsKind.Windows)
        {
            return path;
        }

        path = path.Replace('\\', '/');
        return path.StartsWith("//?/", StringComparison.Ordinal) ? path[4..] : path;
    }
}
