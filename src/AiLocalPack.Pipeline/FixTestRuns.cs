using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Generation;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Recipes.Agent;

namespace AiLocalPack.Pipeline;

/// <summary>
/// The app's side of the agent's <c>test_run</c> during a fix with AI: while the agent runs, the asks the toolkit server
/// leaves in <see cref="TestRunMailbox"/> are answered one at a time. Each builds the translation as the Build stage
/// would (the recipe tool's rebuild and font swap into the job's <c>test-build/</c>; for an unknown engine the files the
/// agent's own rebuild wrote into <c>out/</c>, after <c>translations.json</c> is brought up to date: when it changed, the
/// answer is <c>rebuildFirst</c>, not counted) and tries it with <see cref="IGameTestRunner"/>. The screenshots go into
/// the agent's working folder (<see cref="FolderName"/>, the only place its file tools reach), with the game's log
/// written since the start and what the OS recorded about a crash. At most <see cref="MaxRuns"/> game runs per fix: each
/// opens the game on the player's screen.
/// </summary>
internal sealed class FixTestRuns(
    Game game,
    RecipeTools tools,
    BuildServices services,
    GenerationStepContext context,
    SourceStrings source,
    string workingFolder,
    Action<int, int> onRun)
{
    /// <summary>Full game runs a fix may make.</summary>
    public const int MaxRuns = 5;

    /// <summary>Quick runs (<see cref="TestRunRequest.Quick"/>) a fix may make besides.</summary>
    public const int MaxQuickRuns = 15;

    /// <summary>The screenshots' folder in the agent's working folder; removed when the fix run ends.</summary>
    public const string FolderName = ".test-runs";

    private const int LogLines = 80;
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(400);

    private int _runs;
    private int _quickRuns;

    private int RunsLeft => MaxRuns - _runs;

    private int QuickLeft => MaxQuickRuns - _quickRuns;

    private TestRunMailbox Mailbox => new(context.Folder);

    private string BuildFolder => Path.Combine(context.Folder, "test-build");

    /// <summary>Answers asks until <paramref name="cancellationToken"/> fires (the agent's run ended or was stopped).</summary>
    public async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var ask in Mailbox.Pending())
            {
                TestRunAnswer answer;
                try
                {
                    answer = await AnswerAsync(ask, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or RecipeException)
                {
                    answer = Answer("failed", $"The app couldn't build or start the game: {e.Message}");
                }

                try
                {
                    Mailbox.Reply(ask.Number, answer);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    context.Log($"Fix: the test run's answer can't be written: {e.Message}");
                }
            }

            try
            {
                await Task.Delay(Poll, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Removes what the runs left: the mailbox, <c>test-build/</c> and the screenshots in the working folder.</summary>
    public void Clean()
    {
        Mailbox.Clear();
        foreach (var folder in new[] { BuildFolder, Path.Combine(workingFolder, FolderName) })
        {
            try
            {
                PatchBuild.DeleteFolder(folder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                context.Log($"Fix: {folder} can't be removed: {e.Message}");
            }
        }
    }

    private TestRunAnswer Answer(string outcome, string message) => new(outcome, message, RunsLeft, QuickRunsLeft: QuickLeft);

    private async Task<TestRunAnswer> AnswerAsync(TestRunAsk ask, CancellationToken cancellationToken)
    {
        if (ask.Quick ? _quickRuns >= MaxQuickRuns : _runs >= MaxRuns)
        {
            return Answer("refused", ask.Quick
                ? $"All {MaxQuickRuns} quick runs are used; a full run is still possible while runsLeft > 0."
                : $"All {MaxRuns} full test runs are used; say what you found and what to try in game.");
        }

        if (ask.Untranslated is { Count: > 0 } && source.Tool == AgentScriptTool.ToolId)
        {
            return Answer("failed", "`untranslated` works only for a game the toolkit reads; with your own scripts, empty the translations to try (and put them back), or lay only some files with `files`. This call didn't count.");
        }

        var prepared = await PrepareAsync(ask.Untranslated, cancellationToken).ConfigureAwait(false);
        if (prepared.Answer is { } early)
        {
            return early;
        }

        var laid = prepared.Files;
        if (ask.Files is { Count: > 0 } wanted)
        {
            var asked = wanted.Select(f => f.Replace('\\', '/')).Distinct(StringComparer.Ordinal).ToList();
            var known = laid.ToHashSet(StringComparer.Ordinal);
            var unknown = asked.Where(f => !known.Contains(f)).ToList();
            if (unknown.Count > 0)
            {
                return Answer("failed", $"Not among the rebuilt files: {string.Join(", ", unknown.Take(10))}. The rebuilt files: {string.Join(", ", laid.Take(40))}{(laid.Count > 40 ? ", …" : "")}. This call didn't count.") with { Laid = laid };
            }

            laid = [.. asked.Order(StringComparer.Ordinal)];
        }

        if (ask.Quick)
        {
            _quickRuns++;
        }
        else
        {
            _runs++;
        }

        onRun(_runs + _quickRuns, MaxRuns + MaxQuickRuns);
        var shots = Path.Combine(workingFolder, FolderName, $"run-{_runs + _quickRuns}");
        var started = DateTimeOffset.UtcNow;
        context.Log($"Fix: {(ask.Quick ? $"quick run {_quickRuns} of {MaxQuickRuns}" : $"test run {_runs} of {MaxRuns}")} for the agent"
            + (ask.Seconds is { } s ? $" (at least {s} s)" : "")
            + (ask.Files is { Count: > 0 } ? $", {laid.Count} of {prepared.Files.Count} files" : "")
            + (ask.Untranslated is { Count: > 0 } u ? $", {u.Count} keys untranslated" : "") + ".");
        var result = await services.TestRunner.RunAsync(
            new TestRunRequest(game, context.GameFolder, BuildFolder, laid, context.Folder, context.Log)
            {
                AtLeast = ask.Seconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
                ShotsFolder = shots,
                Quick = ask.Quick,
            },
            cancellationToken).ConfigureAwait(false);
        return Answer(result, shots, started) with { Laid = laid };
    }

    private async Task<(IReadOnlyList<string> Files, TestRunAnswer? Answer)> PrepareAsync(IReadOnlyList<string>? untranslated, CancellationToken cancellationToken)
    {
        var stored = context.Files.ReadStrings();
        PatchBuild.DeleteFolder(BuildFolder);
        if (source.Tool == AgentScriptTool.ToolId)
        {
            var collected = LocalPackBuilder.Collect(source, stored, SourceMarkup.Of(RecipeTools.Builtin, source));
            if (collected.Translations.Count == 0)
            {
                return ([], Answer("failed", "No string has an accepted translation yet."));
            }

            var files = context.Files;
            if (PatchBuild.WriteTranslations(files, context.Job.Language, collected) || !Directory.Exists(files.AgentOutputFolder))
            {
                return ([], Answer(
                    "rebuildFirst",
                    "translations.json in your workspace now holds the stored translations; run your rebuild script (it writes out/), then call test_run again. This call didn't count."));
            }

            if (PatchBuild.Changed(files.AgentOutputFolder, context.GameFolder, out var problem) is not { } changed)
            {
                return ([], Answer("failed", problem!));
            }

            foreach (var file in changed)
            {
                var target = PatchBuild.Local(BuildFolder, file);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(PatchBuild.Local(files.AgentOutputFolder, file), target);
            }

            return (changed, null);
        }

        if (tools.Find(source.Tool) is not { } tool)
        {
            return ([], Answer("failed", $"The toolkit has no '{source.Tool}' tool any more."));
        }

        var all = LocalPackBuilder.Collect(source, stored, tool.Markup);
        var accepted = untranslated is { Count: > 0 } left
            ? all with { Translations = all.Translations.Where(t => !left.Contains(t.Key, StringComparer.Ordinal)).ToDictionary(StringComparer.Ordinal) }
            : all;
        if (accepted.Translations.Count == 0)
        {
            return ([], Answer("failed", "No string has an accepted translation yet."));
        }

        var output = new DirectoryRebuildOutput(BuildFolder);
        var original = services.Read(context.GameFolder);
        var rebuilt = tool.Rebuild(original, context.Job.Language, accepted.Translations, output, cancellationToken);
        var fonts = await services.Fonts.ApplyAsync(tool, original, context.Job.Language, accepted.Translations, context.Files.ReadReferences().FontsFor(context.Job.Language), output, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (fonts.Failure is { } failure)
        {
            return ([], Answer("failed", $"The font the translation needs couldn't be downloaded ({failure.Problem})."));
        }

        return ([.. rebuilt.ChangedFiles.Union(fonts.Files.Files, StringComparer.Ordinal).Order(StringComparer.Ordinal)], null);
    }

    private TestRunAnswer Answer(TestRunResult result, string shots, DateTimeOffset started)
    {
        var original = result.Shot(TestRunShotKind.Original)?.Path;
        if (original is not null && !original.StartsWith(shots, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var copy = Path.Combine(shots, "original.png");
                Directory.CreateDirectory(shots);
                File.Copy(original, copy, overwrite: true);
                original = copy;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                original = null;
            }
        }

        var pictures = result.Shots is { Count: > 0 }
            ? new TestRunPictures(original, result.Shot(TestRunShotKind.Translated)?.Path, result.Shot(TestRunShotKind.Last)?.Path)
            : null;
        GameLogTail? log = null;
        if (services.Environment is { } environment && result.Outcome != TestRunOutcome.Skipped)
        {
            var engine = EngineKind.TryParse(game.Engine, out var kind) ? kind.Engine : source.Engine.Engine;
            log = GameLogs.Find(context.GameFolder, engine, environment, started);
        }

        var outcome = result.Outcome switch
        {
            TestRunOutcome.Passed => "passed",
            TestRunOutcome.Crashed => "crashed",
            _ => "skipped",
        };
        var message = result.Outcome switch
        {
            TestRunOutcome.Passed when result.Detail?.StartsWith("quick run", StringComparison.Ordinal) == true => $"Quick run: the translated game got past loading and kept running for {Round(result.Ran)} s"
                + (result.Ready is null ? " (it never read as loaded: look at the last screenshot)." : "."),
            TestRunOutcome.Passed => $"The translated game kept running for {Round(result.Watch ?? result.Ran)} s"
                + (result.Ready is null ? " but never read as loaded (no picture, or still reading from disk): look at the last screenshot." : ". Look at the screenshots to check the translation shows."),
            TestRunOutcome.Crashed => $"The translated game closed after {Round(result.Ran)} s while the untranslated one kept running: {result.Detail}",
            _ => $"The game couldn't be tried here: {result.Detail}",
        };
        return new TestRunAnswer(
            outcome,
            message,
            RunsLeft,
            result.Ran is { } ran ? Round(ran) : null,
            result.Ready is { } ready ? Round(ready) : null,
            result.Watch is { } watch ? Round(watch) : null,
            result.ExitCode is { } code && result.Outcome == TestRunOutcome.Crashed ? CrashReports.ExitCodeText(code) : null,
            pictures,
            log?.Path,
            log is null ? null : [.. log.Lines.TakeLast(LogLines)],
            result.CrashReport,
            QuickLeft);
    }

    private static double Round(TimeSpan? time) => Math.Round((time ?? TimeSpan.Zero).TotalSeconds, 1);
}
