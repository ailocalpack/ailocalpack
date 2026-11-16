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
    /// <summary>Game runs a fix may make.</summary>
    public const int MaxRuns = 5;

    /// <summary>The screenshots' folder in the agent's working folder; removed when the fix run ends.</summary>
    public const string FolderName = ".test-runs";

    private const int LogLines = 80;
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(400);

    private int _runs;

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
                    answer = new TestRunAnswer("failed", $"The app couldn't build or start the game: {e.Message}", MaxRuns - _runs);
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

    private async Task<TestRunAnswer> AnswerAsync(TestRunAsk ask, CancellationToken cancellationToken)
    {
        if (_runs >= MaxRuns)
        {
            return new TestRunAnswer("refused", $"This fix has used all its {MaxRuns} test runs; tell the player what you found and what to try in game.", 0);
        }

        var prepared = await PrepareAsync(cancellationToken).ConfigureAwait(false);
        if (prepared.Answer is { } early)
        {
            return early;
        }

        _runs++;
        onRun(_runs, MaxRuns);
        var shots = Path.Combine(workingFolder, FolderName, $"run-{_runs}");
        var started = DateTimeOffset.UtcNow;
        context.Log($"Fix: test run {_runs} of {MaxRuns} for the agent" + (ask.Seconds is { } s ? $" (at least {s} s)" : "") + ".");
        var result = await services.TestRunner.RunAsync(
            new TestRunRequest(game, context.CopyFolder, BuildFolder, prepared.Files, context.Folder, context.Log)
            {
                AtLeast = ask.Seconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
                ShotsFolder = shots,
            },
            cancellationToken).ConfigureAwait(false);
        return Answer(result, shots, started);
    }

    private async Task<(IReadOnlyList<string> Files, TestRunAnswer? Answer)> PrepareAsync(CancellationToken cancellationToken)
    {
        var stored = context.Files.ReadStrings();
        PatchBuild.DeleteFolder(BuildFolder);
        if (source.Tool == AgentScriptTool.ToolId)
        {
            var collected = LocalPackBuilder.Collect(source, stored, SourceMarkup.Of(RecipeTools.Builtin, source));
            if (collected.Translations.Count == 0)
            {
                return ([], new TestRunAnswer("failed", "No string has an accepted translation yet.", MaxRuns - _runs));
            }

            var files = context.Files;
            if (PatchBuild.WriteTranslations(files, context.Job.Language, collected) || !Directory.Exists(files.AgentOutputFolder))
            {
                return ([], new TestRunAnswer(
                    "rebuildFirst",
                    "translations.json in your workspace now holds the stored translations; run your rebuild script (it writes out/), then call test_run again. This call didn't count.",
                    MaxRuns - _runs));
            }

            if (PatchBuild.Changed(files.AgentOutputFolder, context.CopyFolder, out var problem) is not { } changed)
            {
                return ([], new TestRunAnswer("failed", problem!, MaxRuns - _runs));
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
            return ([], new TestRunAnswer("failed", $"The toolkit has no '{source.Tool}' tool any more.", MaxRuns - _runs));
        }

        var accepted = LocalPackBuilder.Collect(source, stored, tool.Markup);
        if (accepted.Translations.Count == 0)
        {
            return ([], new TestRunAnswer("failed", "No string has an accepted translation yet.", MaxRuns - _runs));
        }

        var output = new DirectoryRebuildOutput(BuildFolder);
        var rebuilt = tool.Rebuild(context.CopyFolder, context.Job.Language, accepted.Translations, output, cancellationToken);
        var fonts = await services.Fonts.ApplyAsync(tool, context.CopyFolder, context.Job.Language, accepted.Translations, [], output, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (fonts.Failure is { } failure)
        {
            return ([], new TestRunAnswer("failed", $"The font the translation needs couldn't be downloaded ({failure.Problem}).", MaxRuns - _runs));
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
            log = GameLogs.Find(context.CopyFolder, engine, environment, started);
        }

        var outcome = result.Outcome switch
        {
            TestRunOutcome.Passed => "passed",
            TestRunOutcome.Crashed => "crashed",
            _ => "skipped",
        };
        var message = result.Outcome switch
        {
            TestRunOutcome.Passed => $"The translated game kept running for {Round(result.Watch ?? result.Ran)} s"
                + (result.Ready is null ? " but never read as loaded (no picture, or still reading from disk): look at the last screenshot." : ". Look at the screenshots to check the translation shows."),
            TestRunOutcome.Crashed => $"The translated game closed after {Round(result.Ran)} s while the untranslated one kept running: {result.Detail}",
            _ => $"The game couldn't be tried here: {result.Detail}",
        };
        return new TestRunAnswer(
            outcome,
            message,
            MaxRuns - _runs,
            result.Ran is { } ran ? Round(ran) : null,
            result.Ready is { } ready ? Round(ready) : null,
            result.Watch is { } watch ? Round(watch) : null,
            result.ExitCode is { } code && result.Outcome == TestRunOutcome.Crashed ? CrashReports.ExitCodeText(code) : null,
            pictures,
            log?.Path,
            log is null ? null : [.. log.Lines.TakeLast(LogLines)],
            result.CrashReport);
    }

    private static double Round(TimeSpan? time) => Math.Round((time ?? TimeSpan.Zero).TotalSeconds, 1);
}
