using System.Diagnostics;
using System.Globalization;
using AiLocalPack.Agents.Processes;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Folders;

namespace AiLocalPack.Pipeline;

/// <summary>How the Build stage tries the translated game.</summary>
/// <param name="Watch">How long the game must keep running to pass (it is closed then).</param>
/// <param name="Enabled"><c>false</c> = every test run is skipped (<see cref="TestRunSkip.TurnedOff"/>).</param>
public sealed record TestRunOptions(TimeSpan Watch, bool Enabled = true)
{
    /// <summary>15 s: past a crash at start (missing font, broken data file), short enough to wait for.</summary>
    public static TestRunOptions Default { get; } = new(TimeSpan.FromSeconds(15));
}

/// <summary>What to try: the game, its working copy (untouched) and the rebuilt files laid over it for the run.</summary>
/// <param name="BuildFolder">The rebuild, laid out like the game folder.</param>
/// <param name="BuildFiles">Its files (relative, '/'-separated).</param>
/// <param name="JobFolder">The job folder: <c>test-run/</c> keeps what the run moved aside, <c>logs/test-run.log</c> the game's output.</param>
public sealed record TestRunRequest(Game Game, string CopyFolder, string BuildFolder, IReadOnlyList<string> BuildFiles, string JobFolder, Action<string> Log);

/// <summary>Tries the translated game for a moment (task 51); behind an interface so stages are tested without games.</summary>
public interface IGameTestRunner
{
    /// <summary>The working copy is as before when it returns.</summary>
    /// <exception cref="IOException">The rebuilt files can't be laid over the copy, or the copy can't be put back.</exception>
    Task<TestRunResult> RunAsync(TestRunRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The Build stage's test run (task 51): starts the working copy's program with the rebuilt files laid over it
/// (<see cref="TestRunOverlay"/>) and watches it for <see cref="TestRunOptions.Watch"/>. Still running then = passed
/// (the process tree is closed). Closed earlier = the untranslated copy is tried the same way: when it keeps running the
/// translation broke the game (<see cref="TestRunOutcome.Crashed"/>), when it closes too the game can't run from a copy
/// here (it needs its store, or the movies and audio left out of the copy): skipped. Skipped before starting: no program
/// this OS runs (<see cref="GameLaunchPlanner.PlanProgram"/>, a bundle's own binary so the process can be watched), a
/// Windows program wrapped in Steam's DRM (<see cref="SteamStub"/>), the OS refusing to start it. A Steam game gets
/// <c>steam_appid.txt</c> next to its program and <c>SteamAppId</c> / <c>SteamGameId</c> in its environment, so the
/// Steam API doesn't restart the game through Steam (which would start the installed game instead of the copy).
/// Standard output and error go to <c>logs/test-run.log</c>; the last error lines are the crash detail. The game may
/// write its settings and saves where the installed game does.
/// </summary>
public sealed class GameTestRunner(IProcessRunner runner, OsKind os, TestRunOptions? options = null) : IGameTestRunner
{
    private const int TailLines = 10;
    private const int MaxLogLines = 5_000;

    private readonly TestRunOptions _options = options ?? TestRunOptions.Default;

    public async Task<TestRunResult> RunAsync(TestRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_options.Enabled)
        {
            return TestRunResult.Skipped(TestRunSkip.TurnedOff, "Test runs are turned off.");
        }

        var plan = Plan(request.Game, request.CopyFolder, os);
        if (plan.Skip is { } skip)
        {
            return skip;
        }

        var launch = plan.Launch!;
        var added = new List<string>();
        if (request.Game.Id.SteamAppId is not null)
        {
            var appIdFile = Path.GetRelativePath(request.CopyFolder, Path.Combine(launch.WorkingDirectory, "steam_appid.txt")).Replace('\\', '/');
            if (!appIdFile.StartsWith("..", StringComparison.Ordinal) && !File.Exists(Path.Combine(request.CopyFolder, appIdFile)))
            {
                added.Add(appIdFile);
            }
        }

        var spec = Spec(launch, request.Game);
        request.Log($"Test run: {launch} for {Seconds(_options.Watch)}.");
        var logs = Path.Combine(request.JobFolder, "logs");
        Directory.CreateDirectory(logs);
        await using var log = new StreamWriter(Path.Combine(logs, "test-run.log"), append: false);
        var overlay = TestRunOverlay.Apply(request.JobFolder, request.CopyFolder, request.BuildFolder, request.BuildFiles, added);
        try
        {
            foreach (var file in added)
            {
                await File.WriteAllTextAsync(Path.Combine(request.CopyFolder, file), request.Game.Id.SteamAppId!.Value.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            }

            Watch translated;
            try
            {
                translated = await WatchAsync(spec, "translated", log, cancellationToken).ConfigureAwait(false);
            }
            catch (ProcessStartException e)
            {
                return TestRunResult.Skipped(TestRunSkip.StartFailed, $"{launch.FileName} couldn't be started: {e.Message}");
            }

            if (translated.StillRunning)
            {
                return TestRunResult.Passed(translated.Ran, $"still running after {Seconds(translated.Ran)}");
            }

            request.Log($"Test run: the translated game closed after {Seconds(translated.Ran)} with exit code {translated.ExitCode}; trying the untranslated copy.");
            overlay.RestoreOriginals();
            var untranslated = await WatchAsync(spec, "untranslated", log, cancellationToken).ConfigureAwait(false);
            var tail = translated.Tail.Count > 0 ? ": " + string.Join(" | ", translated.Tail) : ".";
            return untranslated.StillRunning
                ? TestRunResult.Crashed(translated.Ran, translated.ExitCode, $"the game closed with exit code {translated.ExitCode} {Seconds(translated.Ran)} after start, while the untranslated copy kept running{tail}")
                : TestRunResult.Skipped(
                    TestRunSkip.ClosesUntranslated,
                    $"the untranslated copy closes by itself too (after {Seconds(untranslated.Ran)}, exit code {untranslated.ExitCode}): the game doesn't run from a copy here (it may need its store, or the movies and audio the copy leaves out)",
                    translated.Ran,
                    translated.ExitCode);
        }
        catch (ProcessStartException e)
        {
            return TestRunResult.Skipped(TestRunSkip.StartFailed, $"{launch.FileName} couldn't be started: {e.Message}");
        }
        finally
        {
            overlay.Restore();
        }
    }

    /// <summary>The program to start in the working copy, or why there is none.</summary>
    internal static (ProcessLaunch? Launch, TestRunResult? Skip) Plan(Game game, string copyFolder, OsKind os)
    {
        var main = new EngineDetector().Detect(copyFolder).Match?.MainExecutable ?? game.MainExecutable;
        var candidates = FolderRecognizer.ListExecutables(copyFolder, main)
            .Select(exe => new LaunchCandidate(exe, ExecutableFormats.Detect(Path.Combine(copyFolder, exe.Replace('/', Path.DirectorySeparatorChar)))))
            .ToList();
        switch (GameLaunchPlanner.PlanProgram(copyFolder, os, candidates, watchable: true))
        {
            case ProcessLaunch launch when os == OsKind.Windows && SteamStub.IsWrapped(launch.FileName):
                return (null, TestRunResult.Skipped(TestRunSkip.SteamDrm, $"{Path.GetFileName(launch.FileName)} is wrapped in Steam's DRM, which only runs it from Steam."));
            case ProcessLaunch launch:
                return (launch, null);
            case CannotLaunch { Problem: LaunchProblem.WindowsOnly or LaunchProblem.LinuxOnly or LaunchProblem.MacOnly } cannot:
                return (null, TestRunResult.Skipped(TestRunSkip.OtherPlatform, $"{cannot.Executable} is a {Platform(cannot.Problem)} program, which doesn't run on {OsName(os)}."));
            default:
                return (null, TestRunResult.Skipped(TestRunSkip.NoExecutable, "No program in the working copy this app knows how to start."));
        }
    }

    private static string OsName(OsKind os) => os switch
    {
        OsKind.Windows => "Windows",
        OsKind.MacOS => "macOS",
        _ => "Linux",
    };

    private static string Platform(LaunchProblem problem) => problem switch
    {
        LaunchProblem.WindowsOnly => "Windows",
        LaunchProblem.LinuxOnly => "Linux",
        _ => "macOS",
    };

    private static ProcessSpec Spec(ProcessLaunch launch, Game game)
    {
        var environment = new Dictionary<string, string?>();
        if (game.Id.SteamAppId is { } appId)
        {
            environment["SteamAppId"] = appId.ToString(CultureInfo.InvariantCulture);
            environment["SteamGameId"] = appId.ToString(CultureInfo.InvariantCulture);
        }

        return new ProcessSpec(launch.FileName, launch.Arguments) { WorkingDirectory = launch.WorkingDirectory, Environment = environment };
    }

    private async Task<Watch> WatchAsync(ProcessSpec spec, string label, StreamWriter log, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_options.Watch);
        var clock = Stopwatch.StartNew();
        await log.WriteLineAsync($"== {label} run: {spec.FileName} ==").ConfigureAwait(false);
        var tail = new Queue<string>();
        var lines = 0;
        await using (var process = runner.Start(spec, limit.Token))
        {
            await foreach (var line in process.ReadLinesAsync(CancellationToken.None).ConfigureAwait(false))
            {
                if (++lines <= MaxLogLines)
                {
                    await log.WriteLineAsync($"[{(line.Stream == ProcessStream.StandardError ? "err" : "out")}] {line.Text}").ConfigureAwait(false);
                }

                if (line.Stream == ProcessStream.StandardError && line.Text.Trim().Length > 0)
                {
                    tail.Enqueue(line.Text.Trim());
                    if (tail.Count > TailLines)
                    {
                        tail.Dequeue();
                    }
                }
            }

            var exit = await process.Exit.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var ran = clock.Elapsed;
            await log.WriteLineAsync(exit.Killed ? $"== still running after {Seconds(ran)}: closed ==" : $"== closed by itself after {Seconds(ran)}, exit code {exit.ExitCode} ==").ConfigureAwait(false);
            await log.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new Watch(exit.Killed, ran, exit.ExitCode, [.. tail]);
        }
    }

    private static string Seconds(TimeSpan time) => string.Create(CultureInfo.InvariantCulture, $"{time.TotalSeconds:0.#} s");

    private sealed record Watch(bool StillRunning, TimeSpan Ran, int ExitCode, IReadOnlyList<string> Tail);
}

/// <summary>
/// The rebuilt files laid over the working copy for a test run, undone afterwards so the copy stays the game as it was
/// (a stage run again extracts and rebuilds from it). In the job folder's <c>test-run/</c>: <c>added.txt</c> (files the
/// run adds, listed before they are written) and <c>originals/</c> (the copy's files the rebuild replaces, moved aside).
/// <see cref="Recover"/> undoes what a crash left; every step is safe to repeat.
/// </summary>
public sealed class TestRunOverlay
{
    private readonly string _folder;
    private readonly string _copy;

    private TestRunOverlay(string jobFolder, string copyFolder)
    {
        _folder = Path.Combine(jobFolder, "test-run");
        _copy = copyFolder;
    }

    /// <param name="extra">Files the run writes itself besides the rebuild (<c>steam_appid.txt</c>), removed afterwards.</param>
    /// <exception cref="IOException">A file can't be moved or copied (what was done is undone first).</exception>
    public static TestRunOverlay Apply(string jobFolder, string copyFolder, string buildFolder, IReadOnlyList<string> buildFiles, IReadOnlyList<string> extra)
    {
        ArgumentNullException.ThrowIfNull(buildFiles);
        ArgumentNullException.ThrowIfNull(extra);
        var overlay = new TestRunOverlay(jobFolder, copyFolder);
        overlay.Restore();
        try
        {
            var added = buildFiles.Where(f => !File.Exists(overlay.CopyPath(f))).Concat(extra).ToList();
            Directory.CreateDirectory(overlay._folder);
            AtomicFile.WriteAllBytes(overlay.AddedList, System.Text.Encoding.UTF8.GetBytes(string.Join('\n', added)));
            foreach (var file in buildFiles)
            {
                var target = overlay.CopyPath(file);
                if (File.Exists(target))
                {
                    var original = overlay.OriginalPath(file);
                    Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                    File.Move(target, original, overwrite: true);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(buildFolder, file.Replace('/', Path.DirectorySeparatorChar)), target, overwrite: true);
            }
        }
        catch
        {
            overlay.Restore();
            throw;
        }

        return overlay;
    }

    /// <summary>Undoes an overlay a crash or a killed app left in the job folder (nothing when there is none).</summary>
    /// <exception cref="IOException">A file can't be moved back.</exception>
    public static void Recover(string jobFolder, string copyFolder) => new TestRunOverlay(jobFolder, copyFolder).Restore();

    private string AddedList => Path.Combine(_folder, "added.txt");

    private string Originals => Path.Combine(_folder, "originals");

    /// <summary>The copy's own files back in place; the rebuilt ones they replaced are gone, the added ones stay.</summary>
    public void RestoreOriginals()
    {
        if (!Directory.Exists(Originals))
        {
            return;
        }

        foreach (var original in Directory.EnumerateFiles(Originals, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(_copy, Path.GetRelativePath(Originals, original));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(original, target, overwrite: true);
        }

        Directory.Delete(Originals, recursive: true);
    }

    /// <summary>The copy as before the overlay: originals back, added files deleted, <c>test-run/</c> removed.</summary>
    public void Restore()
    {
        if (!Directory.Exists(_folder))
        {
            return;
        }

        RestoreOriginals();
        if (File.Exists(AddedList))
        {
            foreach (var file in File.ReadAllText(AddedList).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                File.Delete(CopyPath(file));
            }
        }

        Directory.Delete(_folder, recursive: true);
    }

    private string CopyPath(string relative) => Inside(_copy, relative);

    private string OriginalPath(string relative) => Inside(Originals, relative);

    private static string Inside(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new IOException($"'{relative}' is outside the working copy.");
        }

        return full;
    }
}
