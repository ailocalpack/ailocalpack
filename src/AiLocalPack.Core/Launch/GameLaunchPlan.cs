using System.Globalization;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Launch;

/// <summary>Why a game can't be started on this machine.</summary>
public enum LaunchProblem
{
    /// <summary>The game folder can't be reached (drive unplugged, folder moved).</summary>
    GameUnavailable,

    /// <summary>No program in the folder this app knows how to start.</summary>
    NoExecutable,

    /// <summary>Only a Windows build is there and this isn't Windows (on Linux, Steam with Proton can run it).</summary>
    WindowsOnly,

    /// <summary>Only a Linux build is there and this isn't Linux.</summary>
    LinuxOnly,

    /// <summary>Only a macOS build is there and this isn't macOS.</summary>
    MacOnly,
}

/// <summary>How to start a game: what <see cref="GameLaunchPlanner"/> decided.</summary>
public abstract record GameLaunchPlan;

/// <summary>Hand an address to the OS (Steam's <c>steam://rungameid/&lt;id&gt;</c>).</summary>
public sealed record OpenUrlLaunch(Uri Url) : GameLaunchPlan
{
    public override string ToString() => Url.AbsoluteUri;
}

/// <summary>Start a program with arguments in a folder (no shell).</summary>
public sealed record ProcessLaunch(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory) : GameLaunchPlan
{
    /// <summary>The command as a log line: file and arguments, quoted where they hold spaces, and the folder.</summary>
    public override string ToString() =>
        string.Join(' ', new[] { FileName }.Concat(Arguments).Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a))
        + $" (in {WorkingDirectory})";
}

/// <summary>The game can't be started here.</summary>
/// <param name="Executable">The executable the problem is about (relative), when there is one.</param>
public sealed record CannotLaunch(LaunchProblem Problem, string? Executable = null) : GameLaunchPlan;

/// <summary>A program of a manual game's folder: relative, '/'-separated path and its format.</summary>
public sealed record LaunchCandidate(string RelativePath, ExecutableFormat Format);

/// <summary>Builds the launch command per OS and game source. Pure.</summary>
public static class GameLaunchPlanner
{
    public const string MacOpen = "/usr/bin/open";
    public const string Shell = "/bin/sh";

    /// <summary>
    /// Steam games start through Steam (<c>steam://rungameid/&lt;app id&gt;</c>, so overlay, cloud saves and Proton work).
    /// A manual game starts the first of <paramref name="candidates"/> (the main executable first) this OS runs: Windows
    /// a PE; macOS a bundle (through <c>open</c>; a binary inside a bundle opens the bundle), a Mach-O binary or a script;
    /// Linux an ELF or a script (scripts through <c>/bin/sh</c>, so a missing exec bit doesn't matter). Otherwise the
    /// problem is named after the first candidate's platform.
    /// </summary>
    public static GameLaunchPlan Plan(Game game, OsKind os, IReadOnlyList<LaunchCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(candidates);
        if (!game.IsAvailable)
        {
            return new CannotLaunch(LaunchProblem.GameUnavailable);
        }

        if (game.Id.SteamAppId is { } appId)
        {
            return new OpenUrlLaunch(new Uri("steam://rungameid/" + appId.ToString(CultureInfo.InvariantCulture)));
        }

        foreach (var candidate in candidates)
        {
            if (Start(game.InstallPath, os, candidate) is { } plan)
            {
                return plan;
            }
        }

        var known = candidates.FirstOrDefault(c => c.Format != ExecutableFormat.Unknown);
        return known?.Format switch
        {
            ExecutableFormat.WindowsPe => new CannotLaunch(LaunchProblem.WindowsOnly, known.RelativePath),
            ExecutableFormat.LinuxElf or ExecutableFormat.Script => new CannotLaunch(LaunchProblem.LinuxOnly, known.RelativePath),
            ExecutableFormat.MacMachO or ExecutableFormat.MacBundle => new CannotLaunch(LaunchProblem.MacOnly, known.RelativePath),
            _ => new CannotLaunch(LaunchProblem.NoExecutable),
        };
    }

    private static ProcessLaunch? Start(string folder, OsKind os, LaunchCandidate candidate)
    {
        var full = Full(folder, candidate.RelativePath);
        var directory = Path.GetDirectoryName(full) ?? folder;
        return (os, candidate.Format) switch
        {
            (OsKind.Windows, ExecutableFormat.WindowsPe) => new ProcessLaunch(full, [], directory),
            (OsKind.Linux, ExecutableFormat.LinuxElf) => new ProcessLaunch(full, [], directory),
            (OsKind.MacOS, ExecutableFormat.MacBundle) => new ProcessLaunch(MacOpen, [full], folder),
            (OsKind.MacOS, ExecutableFormat.MacMachO) => Bundle(folder, candidate.RelativePath) is { } bundle
                ? new ProcessLaunch(MacOpen, [bundle], folder)
                : new ProcessLaunch(full, [], directory),
            (OsKind.MacOS or OsKind.Linux, ExecutableFormat.Script) => new ProcessLaunch(Shell, [full], directory),
            _ => null,
        };
    }

    /// <summary>The <c>.app</c> folder holding a binary (a part of its path, or the game folder itself), else <c>null</c>.</summary>
    private static string? Bundle(string folder, string relative)
    {
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                return Full(folder, string.Join('/', parts[..(i + 1)]));
            }
        }

        return Path.TrimEndingDirectorySeparator(folder).EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            && relative.StartsWith("Contents/MacOS/", StringComparison.OrdinalIgnoreCase)
            ? Path.TrimEndingDirectorySeparator(folder)
            : null;
    }

    private static string Full(string folder, string relative) =>
        relative.Length == 0 ? Path.TrimEndingDirectorySeparator(folder)
        : Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
}
