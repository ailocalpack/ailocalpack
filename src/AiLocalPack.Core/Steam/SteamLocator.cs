using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Steam;

/// <summary>
/// Finds Steam installations: folders that contain <c>steamapps/</c>. Everything is read through the injected
/// <see cref="OsEnvironment"/> (home folder, variables) and <see cref="IWindowsRegistry"/>, so tests point it at a temp tree.
/// </summary>
public sealed class SteamLocator(OsEnvironment env, IWindowsRegistry registry)
{
    /// <summary>Use only this Steam folder and skip discovery (dev, tests, unusual installs).</summary>
    public const string RootVariable = "AILOCALPACK_STEAM_ROOT";

    private const int MaxLinkDepth = 40;

    public SteamLocator(OsEnvironment env)
        : this(env, WindowsRegistry.Instance)
    {
    }

    /// <summary>
    /// Existing Steam roots in priority order (usually one; on Linux a native and a Flatpak install can coexist).
    /// Paths that lead to the same folder through symlinks are reported once, under the first candidate's path.
    /// Nothing found gives an empty list; this never throws for a missing, unreadable or odd path.
    /// </summary>
    public IReadOnlyList<string> FindRoots()
    {
        var candidates = env.GetVariable(RootVariable) is { } overridden ? [overridden] : Candidates();
        var comparer = env.Os == OsKind.Linux ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var seen = new HashSet<string>(comparer);
        var roots = new List<string>();
        foreach (var candidate in candidates)
        {
            if (TryResolve(candidate) is var (path, real) && seen.Add(real))
            {
                roots.Add(path);
            }
        }

        return roots;
    }

    private IEnumerable<string> Candidates() => env.Os switch
    {
        OsKind.Windows => WindowsCandidates(),
        OsKind.MacOS => [Path.Combine(env.HomeDirectory, "Library", "Application Support", "Steam")],
        OsKind.Linux => LinuxCandidates(),
        _ => [],
    };

    private IEnumerable<string> WindowsCandidates()
    {
        // Steam writes SteamPath with forward slashes: "c:/program files (x86)/steam".
        if (registry.GetCurrentUserString(@"Software\Valve\Steam", "SteamPath") is { Length: > 0 } fromRegistry)
        {
            yield return fromRegistry;
        }

        var programFiles = env.GetVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)";
        yield return Path.Combine(programFiles, "Steam");
    }

    private IEnumerable<string> LinuxCandidates()
    {
        var home = env.HomeDirectory;
        var dataHome = env.GetVariable("XDG_DATA_HOME") is { } xdg && Path.IsPathRooted(xdg)
            ? xdg
            : Path.Combine(home, ".local", "share");
        yield return Path.Combine(dataHome, "Steam");
        // Symlinks the Steam bootstrapper keeps; usually point at the folder above.
        yield return Path.Combine(home, ".steam", "steam");
        yield return Path.Combine(home, ".steam", "root");
        // Debian/Ubuntu "steam-installer" package.
        yield return Path.Combine(home, ".steam", "debian-installation");
        yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
        yield return Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam");
    }

    /// <summary>The candidate as an absolute path plus its symlink-free form, or <c>null</c> when it is not a Steam root.</summary>
    private static (string Path, string Real)? TryResolve(string candidate)
    {
        try
        {
            if (!Path.IsPathRooted(candidate))
            {
                return null;
            }

            var full = Path.GetFullPath(candidate);
            if (!Directory.Exists(Path.Combine(full, "steamapps")))
            {
                return null;
            }

            return (Path.TrimEndingDirectorySeparator(full), RealPath(full, depth: 0));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="path"/> with every symlinked component resolved (not only the last one, which is all
    /// <see cref="FileSystemInfo.ResolveLinkTarget"/> does): <c>~/.steam/steam</c> and <c>~/.local/share/Steam</c> become equal.
    /// </summary>
    internal static string RealPath(string path, int depth)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (depth > MaxLinkDepth)
        {
            return full;
        }

        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        var parts = full[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            var info = new DirectoryInfo(current);
            if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                // The target may itself pass through symlinked folders.
                current = RealPath(target.FullName, depth + 1);
            }
        }

        return Path.TrimEndingDirectorySeparator(current);
    }
}
