using System.Text.RegularExpressions;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Agents.Processes;

/// <summary>Where an agent's program may be installed (tasks 41, 42), as one adapter describes it.</summary>
/// <param name="Name">The program's name without extension (<c>claude</c>, <c>codex</c>).</param>
/// <param name="HomeFolders">Folders under the home its installers use, <c>/</c>-separated, tried after <c>PATH</c> on every OS.</param>
/// <param name="UnixHomeFolders">More such folders on macOS and Linux only.</param>
public sealed record AgentProgramLayout(string Name, IReadOnlyList<string> HomeFolders, IReadOnlyList<string> UnixHomeFolders);

/// <summary>
/// Finding and starting an agent's program, the same for every process-driven agent (tasks 41, 42).
/// <list type="bullet">
/// <item><see cref="Candidates"/>: every <c>PATH</c> folder (Windows <c>&lt;name&gt;.exe</c> before npm's
/// <c>&lt;name&gt;.cmd</c>; quoted entries unquoted, relative ones ignored), then the installers' folders, because an app
/// started from the desktop (Finder, a Start menu shortcut, a <c>.desktop</c> file) doesn't get the shell's <c>PATH</c>:
/// the layout's home folders, then on Unix its Unix ones, Homebrew (<c>/opt/homebrew/bin</c>, <c>/usr/local/bin</c>) and
/// npm's <c>~/.npm-global/bin</c>, on Windows npm's <c>%APPDATA%\npm</c>; last the app-managed folder (task 43). A path is
/// listed once.</item>
/// <item><see cref="Process"/>: a program runs directly with its argument list; a Windows batch file through
/// <c>cmd.exe</c> (<see cref="WindowsCommandLine"/>).</item>
/// </list>
/// </summary>
public static partial class AgentPrograms
{
    /// <summary>Folders outside the home that installers put agents in (Homebrew on Apple silicon and Intel / Linux).</summary>
    public static IReadOnlyList<string> UnixSystemFolders { get; } = ["/opt/homebrew/bin", "/usr/local/bin"];

    /// <param name="layout">The program and its installers' folders.</param>
    /// <param name="environment">The OS, home and variables.</param>
    /// <param name="managedDirectory">The app-managed folder, tried last.</param>
    /// <param name="systemFolders">Folders outside the home to try on Unix (default <see cref="UnixSystemFolders"/>).</param>
    public static IReadOnlyList<(string Path, AgentSource Source)> Candidates(
        AgentProgramLayout layout,
        OsEnvironment environment,
        string? managedDirectory,
        IReadOnlyList<string>? systemFolders = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(environment);
        var windows = environment.Os == OsKind.Windows;
        string[] names = windows ? [layout.Name + ".exe", layout.Name + ".cmd"] : [layout.Name];
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
        foreach (var folder in layout.HomeFolders)
        {
            Add(Join(home, folder, windows), AgentSource.Path);
        }

        if (windows)
        {
            Add(environment.GetVariable("APPDATA") is { } appData ? Join(appData, "npm", windows) : null, AgentSource.Path);
        }
        else
        {
            foreach (var folder in layout.UnixHomeFolders)
            {
                Add(Join(home, folder, windows), AgentSource.Path);
            }

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

    /// <summary>
    /// The process running <paramref name="executable"/> with <paramref name="arguments"/>: directly, or on Windows a batch
    /// file (npm's <c>claude.cmd</c>, <c>codex.cmd</c>) through <c>cmd.exe</c> with its own escaping. The program's own folder
    /// goes first on the process's <c>PATH</c>: an npm install is a script run by <c>node</c> (<c>#!/usr/bin/env node</c>,
    /// a <c>.cmd</c> shim), and node sits next to it (Homebrew, nvm, the Node installer) while an app started from the desktop
    /// doesn't have that folder on its <c>PATH</c>. Other settings of <paramref name="template"/> (folder, environment,
    /// standard input) are kept.
    /// </summary>
    public static ProcessSpec Process(string executable, IReadOnlyList<string> arguments, OsEnvironment environment, ProcessSpec? template = null)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);
        var windows = environment.Os == OsKind.Windows;
        var spec = (template ?? new ProcessSpec(executable, [])) with { FileName = executable, Arguments = arguments, RawArguments = null };
        var cut = windows ? executable.LastIndexOfAny(['\\', '/']) : executable.LastIndexOf('/');
        if (cut > 0)
        {
            var folder = executable[..cut];
            var inherited = environment.GetVariable("PATH");
            var variables = new Dictionary<string, string?>(spec.Environment)
            {
                ["PATH"] = inherited is null ? folder : folder + (windows ? ';' : ':') + inherited,
            };
            spec = spec with { Environment = variables };
        }

        return windows && WindowsCommandLine.IsBatchFile(executable)
            ? WindowsCommandLine.ForBatchFile(executable, arguments, spec)
            : spec;
    }

    /// <summary>The first <c>major.minor.patch[-pre]</c> in a <c>--version</c> output, else <c>null</c>.</summary>
    public static string? ParseVersion(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var match = VersionText().Match(output);
        return match.Success ? match.Value : null;
    }

    /// <summary>Whether <paramref name="version"/> (as <see cref="ParseVersion"/> gives it) is at least <paramref name="minimum"/>.</summary>
    public static bool IsAtLeast(string? version, Version minimum)
    {
        ArgumentNullException.ThrowIfNull(minimum);
        if (version is null)
        {
            return false;
        }

        var core = version.Split('-', '+')[0];
        return System.Version.TryParse(core, out var parsed) && parsed >= minimum;
    }

    /// <summary>
    /// The installation of the first candidate that is a program and answers <c>--version</c> (10 s); when none answers, the
    /// first program found, without a version; <c>null</c> when there is none.
    /// </summary>
    public static async Task<AgentInstallation?> DetectAsync(
        string agentId,
        IReadOnlyList<(string Path, AgentSource Source)> candidates,
        IProcessRunner runner,
        OsEnvironment environment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(environment);
        AgentInstallation? firstFound = null;
        foreach (var (path, source) in candidates)
        {
            if (!IsProgram(path, environment.Os))
            {
                continue;
            }

            var found = new AgentInstallation(agentId, path, null, source);
            firstFound ??= found;
            try
            {
                var result = await runner.RunToEndAsync(Process(path, ["--version"], environment), TimeSpan.FromSeconds(10), cancellationToken);
                if (result.ExitCode == 0 && ParseVersion(result.StandardOutput) is { } version)
                {
                    return found with { Version = version };
                }
            }
            catch (ProcessStartException)
            {
                // A broken candidate gives way to the next.
            }
        }

        return firstFound;
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

    [GeneratedRegex(@"\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?")]
    private static partial Regex VersionText();
}
