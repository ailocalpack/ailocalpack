using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Folders;

namespace AiLocalPack.Toolkit.Engines;

/// <summary>
/// Tells the OS a game's installed files are for (<see cref="Core.Games.Game.Platform"/>) from its executables: the
/// engine's main executable, then the folder's programs in launch order (<see cref="FolderRecognizer.ListExecutables"/>);
/// the first one whose format names an OS wins (PE → Windows, ELF → Linux, Mach-O or a <c>.app</c> bundle → macOS;
/// scripts and unknown files are skipped). A Steam install holds one platform's depot, so its programs agree. Best effort:
/// <c>null</c> when nothing tells, never an exception for disk state.
/// </summary>
public static class GamePlatformDetector
{
    /// <param name="gameFolder">The game's install folder.</param>
    /// <param name="engineExecutable">The engine detection's main executable (relative, '/'-separated), if any.</param>
    /// <param name="mainExecutable">The catalog's main executable of a manual game, if any.</param>
    /// <param name="gameName">The game's name (ranks the executable named like it first).</param>
    public static OsKind? Detect(string gameFolder, string? engineExecutable, string? mainExecutable, string? gameName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);
        try
        {
            if (!Directory.Exists(gameFolder))
            {
                return null;
            }

            var candidates = new List<string>();
            if (engineExecutable is not null)
            {
                candidates.Add(engineExecutable);
            }

            candidates.AddRange(FolderRecognizer.ListExecutables(gameFolder, mainExecutable, gameName));
            foreach (var relative in candidates.Distinct(StringComparer.Ordinal))
            {
                OsKind? os = ExecutableFormats.Detect(Path.Combine(gameFolder, relative.Replace('/', Path.DirectorySeparatorChar))) switch
                {
                    ExecutableFormat.WindowsPe => OsKind.Windows,
                    ExecutableFormat.LinuxElf => OsKind.Linux,
                    ExecutableFormat.MacMachO or ExecutableFormat.MacBundle => OsKind.MacOS,
                    _ => null,
                };
                if (os is not null)
                {
                    return os;
                }
            }

            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
