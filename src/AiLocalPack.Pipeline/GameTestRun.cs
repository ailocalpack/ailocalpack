using System.Diagnostics;
using System.Globalization;
using AiLocalPack.Agents.Processes;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Folders;
using AiLocalPack.Toolkit.Generation;

namespace AiLocalPack.Pipeline;

/// <summary>How the test run tries a game: how long it watches and what counts as loaded.</summary>
/// <param name="Enabled"><c>false</c> = every test run is skipped (<see cref="TestRunSkip.TurnedOff"/>).</param>
public sealed record TestRunOptions(bool Enabled = true)
{
    /// <summary>The shortest watch of a translated run (a game that loads at once still gets this long).</summary>
    public TimeSpan MinWatch { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The longest watch, also the cap of a game that never reads as loaded.</summary>
    public TimeSpan MaxWatch { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>A loading time longer than this reads as "never loaded" (the watch is then <see cref="MaxWatch"/>).</summary>
    public TimeSpan MaxReady { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The watch is the original's loading time × this + <see cref="Margin"/>.</summary>
    public double Factor { get; init; } = 1.5;

    public TimeSpan Margin { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How often the running game is looked at.</summary>
    public TimeSpan Sample { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Loaded = a picture on the window and less than <see cref="QuietBytes"/> read from disk during this long.</summary>
    public TimeSpan QuietSpan { get; init; } = TimeSpan.FromSeconds(5);

    public long QuietBytes { get; init; } = 1024 * 1024;

    /// <summary>Nothing counts as loaded this early (a splash screen before the game reads its data).</summary>
    public TimeSpan MinReady { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>When no picture ever shows (a window the probe can't read), the disk reads alone decide after this long.</summary>
    public TimeSpan NoPictureAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the translated game's last picture is taken while it runs.</summary>
    public TimeSpan ShotEvery { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The default: 30 s – 3 min by the original's loading time.</summary>
    public static TestRunOptions Default { get; } = new();

    /// <summary>The watch of a translated run for a game that loads in <paramref name="ready"/> (<c>null</c> = never seen loaded).</summary>
    public TimeSpan WatchFor(TimeSpan? ready)
    {
        if (ready is not { } r || r > MaxReady)
        {
            return MaxWatch;
        }

        var watch = TimeSpan.FromTicks((long)(r.Ticks * Factor)) + Margin;
        return watch < MinWatch ? MinWatch : watch > MaxWatch ? MaxWatch : watch;
    }
}

/// <summary>What to try: the game, its working copy (untouched) and the rebuilt files laid over it for the run.</summary>
/// <param name="BuildFolder">The rebuild, laid out like the game folder.</param>
/// <param name="BuildFiles">Its files (relative, '/'-separated).</param>
/// <param name="JobFolder">The job folder: <c>test-run/</c> keeps what the run moved aside, <c>logs/test-run.log</c> the game's output.</param>
public sealed record TestRunRequest(Game Game, string CopyFolder, string BuildFolder, IReadOnlyList<string> BuildFiles, string JobFolder, Action<string> Log)
{
    /// <summary>Watch the translated game at least this long (the agent asked; never past <see cref="TestRunOptions.MaxWatch"/>).</summary>
    public TimeSpan? AtLeast { get; init; }

    /// <summary>Where the screenshots go; <c>null</c> = <c>test-shots/&lt;UTC time&gt;/</c> in the job folder.</summary>
    public string? ShotsFolder { get; init; }

    /// <summary>Told when a run starts: the untranslated copy (<c>false</c>) or the translation (<c>true</c>), and how long it is watched at most.</summary>
    public Action<bool, TimeSpan>? Starting { get; init; }
}

/// <summary>Tries the translated game (task 51, reworked); behind an interface so stages are tested without games.</summary>
public interface IGameTestRunner
{
    /// <summary>The working copy is as before when it returns.</summary>
    /// <exception cref="IOException">The rebuilt files can't be laid over the copy, or the copy can't be put back.</exception>
    Task<TestRunResult> RunAsync(TestRunRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The test run: tries the translated game long enough to see it loaded.
/// <list type="number">
/// <item><b>The original first</b> (once per game version, <see cref="TestRunBaselines"/>): the untranslated working copy
/// runs until it has loaded, which is a picture on its window (<see cref="GameFrame.HasContent"/>) while it reads less
/// than <see cref="TestRunOptions.QuietBytes"/> from disk in <see cref="TestRunOptions.QuietSpan"/>
/// (<see cref="IGameProbe"/>); its screenshot then is the comparison. It is watched for
/// <see cref="TestRunOptions.WatchFor"/> its loading time (× 1.5 + 20 s, 30 s – 3 min). When it closes by itself within
/// that, the game can't be tried from a copy here (it needs its store, or the movies and audio left out of the copy):
/// skipped, and nothing is remembered.</item>
/// <item><b>Then the translation</b>, with the rebuilt files laid over the copy (<see cref="TestRunOverlay"/>), for the
/// same watch (longer when <see cref="TestRunRequest.AtLeast"/> asks): still running at the end = passed; closed earlier
/// = crashed, with the last error lines and what the OS recorded about it (<see cref="CrashReports"/>). A first launch
/// that passes is followed by a <b>second launch</b> for the same watch, on the files the first one wrote (a cache built
/// from the translated files that only breaks the next start, as Barony's compiled books did). Screenshots: once loaded
/// (<see cref="TestRunShotKind.Translated"/>, the first launch) and the last one (<see cref="TestRunShotKind.Last"/>: at
/// the end, or the last before it closed).</item>
/// </list>
/// What the game writes into the working copy itself is undone after the original's run and after the translation's
/// (<see cref="TestRunCopyGuard"/>), so every run starts from the copy as extracted.
/// Skipped before starting: no program this OS runs (<see cref="GameLaunchPlanner.PlanProgram"/>, a bundle's own binary
/// so the process can be watched), a Windows program wrapped in Steam's DRM (<see cref="SteamStub"/>), the OS refusing to
/// start it. A Steam game gets <c>steam_appid.txt</c> next to its program and <c>SteamAppId</c> / <c>SteamGameId</c> in
/// its environment, so the Steam API doesn't restart the game through Steam (which would start the installed game instead
/// of the copy). Standard output and error go to <c>logs/test-run.log</c>. The game may write its settings and saves where
/// the installed game does.
/// </summary>
public sealed class GameTestRunner(
    IProcessRunner runner,
    OsKind os,
    TestRunOptions? options = null,
    IGameProbe? probe = null,
    TestRunBaselines? baselines = null,
    OsEnvironment? environment = null,
    TimeProvider? time = null) : IGameTestRunner
{
    private const int TailLines = 10;
    private const int MaxLogLines = 5_000;

    private readonly TestRunOptions _options = options ?? TestRunOptions.Default;

    /// <summary>The test run of this machine: its OS probe, the baselines in <see cref="AppPaths.TestRuns"/>, its crash reports.</summary>
    public static GameTestRunner ForMachine(IProcessRunner runner, OsEnvironment environment, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(paths);
        return new GameTestRunner(runner, environment.Os, probe: GameProbes.For(environment.Os), baselines: new TestRunBaselines(paths.TestRuns), environment: environment);
    }
    private readonly IGameProbe _probe = probe ?? NoGameProbe.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

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
        var shots = request.ShotsFolder ?? Path.Combine(request.JobFolder, "test-shots", _time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        var logs = Path.Combine(request.JobFolder, "logs");
        Directory.CreateDirectory(logs);
        await using var log = new StreamWriter(Path.Combine(logs, "test-run.log"), append: false);
        var guard = TestRunCopyGuard.Take(request.CopyFolder);
        try
        {
            var baseline = baselines?.Find(request.Game);
            if (baseline is null)
            {
                request.Log($"Test run: {launch}: measuring how the untranslated copy loads (up to {Seconds(_options.MaxWatch)}).");
                request.Starting?.Invoke(false, _options.MaxWatch);
                var originalShot = baselines?.ScreenshotPath(request.Game.Id) ?? Path.Combine(shots, "original.png");
                var overlay = TestRunOverlay.Apply(request.JobFolder, request.CopyFolder, request.BuildFolder, [], added);
                Watch original;
                try
                {
                    await WriteAddedAsync(request, added, cancellationToken).ConfigureAwait(false);
                    original = await WatchAsync(spec, "untranslated", log, null, originalShot, null, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    overlay.Restore();
                    guard.Undo(request.Game.InstallPath, request.Log);
                }

                var watch = _options.WatchFor(original.Ready);
                var survived = original.StillRunning && original.Ran >= watch - _options.Sample;
                baseline = new TestRunBaseline(
                    TestRunBaselines.VersionKey(request.Game),
                    original.Ready,
                    watch,
                    survived,
                    original.Ran,
                    _time.GetUtcNow(),
                    original.StillRunning ? null : original.ExitCode,
                    original.ReadyShot);
                request.Log(survived
                    ? $"Test run: the untranslated copy loads in {(original.Ready is { } r ? Seconds(r) : "an unknown time")}; the translation is watched for {Seconds(watch)}."
                    : $"Test run: the untranslated copy closed by itself after {Seconds(original.Ran)} with exit code {original.ExitCode}.");
                if (survived)
                {
                    baselines?.Save(request.Game.Id, baseline);
                }
            }

            if (!baseline.Survived)
            {
                return TestRunResult.Skipped(
                    TestRunSkip.ClosesUntranslated,
                    $"the untranslated copy closes by itself (after {Seconds(baseline.Ran)}, exit code {baseline.ExitCode}): the game doesn't run from a copy here (it may need its store, or the movies and audio the copy leaves out)",
                    baseline.Ran,
                    baseline.ExitCode);
            }

            var limit = request.AtLeast is { } atLeast && atLeast > baseline.Watch ? (atLeast > _options.MaxWatch ? _options.MaxWatch : atLeast) : baseline.Watch;
            request.Log($"Test run: {launch} with the translation for {Seconds(limit)}.");
            request.Starting?.Invoke(true, limit);
            var started = _time.GetUtcNow();
            var translatedOverlay = TestRunOverlay.Apply(request.JobFolder, request.CopyFolder, request.BuildFolder, request.BuildFiles, added);
            Watch translated;
            Watch? first = null;
            try
            {
                await WriteAddedAsync(request, added, cancellationToken).ConfigureAwait(false);
                translated = await WatchAsync(spec, "translated", log, limit, Path.Combine(shots, "translated.png"), Path.Combine(shots, "last.png"), cancellationToken).ConfigureAwait(false);
                if (translated.StillRunning)
                {
                    // A game may write a cache from the translated files on its first launch (Barony compiles its books)
                    // that only breaks the launches after it: start it once more on what the first launch left.
                    request.Log($"Test run: {launch} with the translation once more, on the files its first launch wrote, for {Seconds(limit)}.");
                    request.Starting?.Invoke(true, limit);
                    first = translated;
                    started = _time.GetUtcNow();
                    translated = await WatchAsync(spec, "translated, second launch", log, limit, null, Path.Combine(shots, "last.png"), cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                translatedOverlay.Restore();
                guard.Undo(request.Game.InstallPath, request.Log);
            }

            var list = new List<TestRunShot>();
            if (baseline.Screenshot is { } originalPicture)
            {
                list.Add(new TestRunShot(TestRunShotKind.Original, originalPicture));
            }

            if ((first ?? translated).ReadyShot is { } translatedPicture)
            {
                list.Add(new TestRunShot(TestRunShotKind.Translated, translatedPicture));
            }

            if ((translated.LastShot ?? first?.LastShot) is { } lastPicture)
            {
                list.Add(new TestRunShot(TestRunShotKind.Last, lastPicture));
            }

            var loaded = translated.Ready is { } ready ? $"loaded after {Seconds(ready)}" : "never read as loaded";
            var originalLoads = baseline.Ready is { } originalReady ? $"the untranslated copy loads in {Seconds(originalReady)}" : "the untranslated copy never read as loaded either";
            if (translated.StillRunning)
            {
                var firstLoaded = first?.Ready is { } firstReady ? $"loaded after {Seconds(firstReady)}" : loaded;
                return new TestRunResult(
                    TestRunOutcome.Passed,
                    Detail: first is null
                        ? $"still running after {Seconds(translated.Ran)} ({loaded}; {originalLoads})"
                        : $"still running after {Seconds(first.Ran)}, and again after {Seconds(translated.Ran)} on a second launch ({firstLoaded}; {originalLoads})",
                    Ran: translated.Ran,
                    Ready: first?.Ready ?? translated.Ready,
                    Watch: limit,
                    Shots: list);
            }

            var crash = environment is null ? null : CrashReports.Find(environment, launch.FileName, started);
            var tail = translated.Tail.Count > 0 ? ": " + string.Join(" | ", translated.Tail) : ".";
            var code = CrashReports.ExitCodeText(translated.ExitCode);
            var which = first is null ? "translated game" : "translated game's second launch";
            request.Log($"Test run: the {which} closed after {Seconds(translated.Ran)} with exit code {code}" + (crash is null ? "." : $"; {crash.Name} has a report."));
            var secondLaunch = first is null ? "" : $"on its second launch, with the files its first launch wrote (that one kept running for {Seconds(first.Ran)}), ";
            return new TestRunResult(
                TestRunOutcome.Crashed,
                Detail: $"{secondLaunch}the game closed with exit code {code} {Seconds(translated.Ran)} after start ({loaded}), while the untranslated copy kept running for {Seconds(baseline.Watch)} ({originalLoads}){tail}",
                Ran: translated.Ran,
                ExitCode: translated.ExitCode,
                Ready: translated.Ready,
                Watch: limit,
                Shots: list,
                CrashReport: crash is null ? null : string.Join('\n', crash.Lines));
        }
        catch (ProcessStartException e)
        {
            return TestRunResult.Skipped(TestRunSkip.StartFailed, $"{launch.FileName} couldn't be started: {e.Message}");
        }
    }

    private static async Task WriteAddedAsync(TestRunRequest request, List<string> added, CancellationToken cancellationToken)
    {
        foreach (var file in added)
        {
            await File.WriteAllTextAsync(Path.Combine(request.CopyFolder, file), request.Game.Id.SteamAppId!.Value.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The program to start in the working copy, or why there is none.</summary>
    internal static (ProcessLaunch? Launch, TestRunResult? Skip) Plan(Game game, string copyFolder, OsKind os)
    {
        var main = new EngineDetector().Detect(copyFolder).Match?.MainExecutable ?? game.MainExecutable;
        var candidates = FolderRecognizer.ListExecutables(copyFolder, main, game.Name)
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

    /// <summary>
    /// Runs the program and looks at it every <see cref="TestRunOptions.Sample"/> until it closes or the watch ends
    /// (<paramref name="limit"/>; <c>null</c> = the watch its own loading time gives, <see cref="TestRunOptions.WatchFor"/>).
    /// </summary>
    private async Task<Watch> WatchAsync(ProcessSpec spec, string label, StreamWriter log, TimeSpan? limit, string? readyShot, string? lastShot, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var clock = Stopwatch.StartNew();
        await log.WriteLineAsync($"== {label} run: {spec.FileName} ==").ConfigureAwait(false);
        var tail = new Queue<string>();
        await using var process = runner.Start(spec, stop.Token);
        var reading = ReadAsync(process, log, tail);
        var reads = new List<(TimeSpan At, long Bytes)>();
        TimeSpan? ready = null;
        TimeSpan? firstPicture = null;
        string? savedReady = null;
        string? savedLast = null;
        GameFrame? last = null;
        var lastTaken = TimeSpan.Zero;
        while (true)
        {
            var wait = Task.Delay(_options.Sample, _time, cancellationToken);
            if (await Task.WhenAny(wait, process.Exit).ConfigureAwait(false) == process.Exit)
            {
                break;
            }

            await wait.ConfigureAwait(false);
            var now = clock.Elapsed;
            var frame = _probe.CanCapture ? _probe.Capture(process.Id) : null;
            if (frame is { HasContent: true })
            {
                firstPicture ??= now;
                if (lastShot is not null && (last is null || now - lastTaken >= _options.ShotEvery))
                {
                    last = frame;
                    lastTaken = now;
                }
            }

            if (_probe.ReadBytes(process.Id) is { } bytes)
            {
                reads.Add((now, bytes));
            }

            if (ready is null && now >= _options.MinReady && IsLoaded(now, firstPicture, reads))
            {
                ready = now;
                if (readyShot is not null && frame is { HasContent: true } && Save(frame, readyShot))
                {
                    savedReady = readyShot;
                }
            }

            if (now >= (limit ?? (ready is { } r ? _options.WatchFor(r) : _options.MaxWatch)))
            {
                if (_probe.CanCapture && _probe.Capture(process.Id) is { HasContent: true } final)
                {
                    last = final;
                }

                break;
            }
        }

        var stillRunning = !process.Exit.IsCompleted;
        var ran = clock.Elapsed;
        if (stillRunning)
        {
            await stop.CancelAsync().ConfigureAwait(false);
        }

        var exit = await process.Exit.ConfigureAwait(false);
        await reading.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (lastShot is not null && last is not null && Save(last, lastShot))
        {
            savedLast = lastShot;
        }

        await log.WriteLineAsync(ready is { } loaded ? $"== loaded after {Seconds(loaded)} ==" : "== never read as loaded ==").ConfigureAwait(false);
        await log.WriteLineAsync(stillRunning ? $"== still running after {Seconds(ran)}: closed ==" : $"== closed by itself after {Seconds(ran)}, exit code {exit.ExitCode} ==").ConfigureAwait(false);
        await log.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new Watch(stillRunning, ran, exit.ExitCode, [.. tail], ready, savedReady, savedLast);
    }

    /// <summary>
    /// Loaded: a picture on the window (or none expected: the probe can't take one, or none came for
    /// <see cref="TestRunOptions.NoPictureAfter"/>) and calm disk reads over <see cref="TestRunOptions.QuietSpan"/>; with
    /// no read counts, the picture shown for that long.
    /// </summary>
    private bool IsLoaded(TimeSpan now, TimeSpan? firstPicture, List<(TimeSpan At, long Bytes)> reads)
    {
        var picture = firstPicture is not null || !_probe.CanCapture || now >= _options.NoPictureAfter;
        if (!picture)
        {
            return false;
        }

        if (reads.Count == 0)
        {
            return _probe.CanCapture && firstPicture is { } first && now - first >= _options.QuietSpan;
        }

        var latest = reads[^1];
        var earlier = reads.LastOrDefault(r => latest.At - r.At >= _options.QuietSpan);
        return earlier != default && latest.Bytes - earlier.Bytes < _options.QuietBytes;
    }

    private static bool Save(GameFrame frame, string path)
    {
        try
        {
            frame.SavePng(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task ReadAsync(IRunningProcess process, StreamWriter log, Queue<string> tail)
    {
        var lines = 0;
        await foreach (var line in process.ReadLinesAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (++lines <= MaxLogLines)
            {
                await log.WriteLineAsync($"[{(line.Stream == ProcessStream.StandardError ? "err" : "out")}] {line.Text}").ConfigureAwait(false);
            }

            if (line.Stream == ProcessStream.StandardError && line.Text.Trim().Length > 0)
            {
                lock (tail)
                {
                    tail.Enqueue(line.Text.Trim());
                    if (tail.Count > TailLines)
                    {
                        tail.Dequeue();
                    }
                }
            }
        }
    }

    internal static string Seconds(TimeSpan time) => string.Create(CultureInfo.InvariantCulture, $"{time.TotalSeconds:0.#} s");

    private sealed record Watch(bool StillRunning, TimeSpan Ran, int ExitCode, IReadOnlyList<string> Tail, TimeSpan? Ready, string? ReadyShot, string? LastShot);
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
