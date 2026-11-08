using AiLocalPack.Core.Storage;

namespace AiLocalPack.Agents.Claude;

/// <summary>
/// Where Claude Code may be installed (task 41), in the order detection tries them: every <c>PATH</c> folder (Windows
/// <c>claude.exe</c> before npm's <c>claude.cmd</c>), then the folders its installers use, because an app started from the
/// desktop (Finder, a Start menu shortcut, a <c>.desktop</c> file) doesn't get the shell's <c>PATH</c>: the native installer's
/// <c>~/.local/bin</c>, the old local install <c>~/.claude/local</c>, Homebrew (<c>/opt/homebrew/bin</c>,
/// <c>/usr/local/bin</c>), npm's global folder (Windows <c>%APPDATA%\npm</c>, Unix <c>~/.npm-global/bin</c>); last the
/// app-managed folder (task 43). A path is listed once.
/// </summary>
public static class ClaudeCodeLocations
{
    /// <summary>Folders outside the home that installers put Claude Code in (Homebrew on Apple silicon and Intel / Linux).</summary>
    public static IReadOnlyList<string> UnixSystemFolders { get; } = ["/opt/homebrew/bin", "/usr/local/bin"];

    /// <param name="environment">The OS, home and variables.</param>
    /// <param name="managedDirectory">The app-managed folder, tried last.</param>
    /// <param name="systemFolders">Folders outside the home to try on Unix (default <see cref="UnixSystemFolders"/>).</param>
    public static IReadOnlyList<(string Path, AgentSource Source)> Candidates(
        OsEnvironment environment,
        string? managedDirectory,
        IReadOnlyList<string>? systemFolders = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var windows = environment.Os == OsKind.Windows;
        string[] names = windows ? ["claude.exe", "claude.cmd"] : ["claude"];
        var comparer = windows || environment.Os == OsKind.MacOS ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        var candidates = new List<(string, AgentSource)>();

        void Add(string? folder, AgentSource source)
        {
            if (string.IsNullOrWhiteSpace(folder) || !IsAbsolute(folder.Trim(), windows))
            {
                return;
            }

            foreach (var name in names)
            {
                var path = Join(folder.Trim(), name, windows);
                if (seen.Add(path))
                {
                    candidates.Add((path, source));
                }
            }
        }

        foreach (var folder in (environment.GetVariable("PATH") ?? string.Empty).Split(windows ? ';' : ':'))
        {
            Add(folder.Trim('"'), AgentSource.Path);
        }

        var home = environment.HomeDirectory;
        Add(Join(home, ".local/bin", windows), AgentSource.Path);
        if (windows)
        {
            Add(environment.GetVariable("APPDATA") is { } appData ? Join(appData, "npm", windows) : null, AgentSource.Path);
        }
        else
        {
            Add(Join(home, ".claude/local", windows), AgentSource.Path);
            foreach (var folder in systemFolders ?? UnixSystemFolders)
            {
                Add(folder, AgentSource.Path);
            }

            Add(Join(home, ".npm-global/bin", windows), AgentSource.Path);
        }

        Add(managedDirectory, AgentSource.Managed);
        return candidates;
    }

    /// <summary>Whether <paramref name="path"/> is a file this OS can run (on Unix: with an execute bit).</summary>
    public static bool IsProgram(string path, OsKind os)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            if (os == OsKind.Windows || OperatingSystem.IsWindows())
            {
                return true;
            }

            const UnixFileMode execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & execute) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsAbsolute(string folder, bool windows) =>
        windows ? folder.Length >= 3 && char.IsAsciiLetter(folder[0]) && folder[1] == ':' && folder[2] is '\\' or '/' || folder.StartsWith(@"\\", StringComparison.Ordinal)
        : folder.StartsWith('/');

    private static string Join(string folder, string relative, bool windows)
    {
        var separator = windows ? '\\' : '/';
        var tail = windows ? relative.Replace('/', '\\') : relative;
        return folder.TrimEnd('/', '\\') + separator + tail;
    }
}
