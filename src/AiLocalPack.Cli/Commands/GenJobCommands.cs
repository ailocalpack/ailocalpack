using System.CommandLine;
using System.Globalization;
using AiLocalPack.Agents;
using AiLocalPack.Agents.Fake;
using AiLocalPack.Agents.Processes;
using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Languages;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Generation;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Cli.Commands;

/// <summary>
/// Generation jobs (task 47): <c>gen run &lt;game&gt; --fake-agent &lt;recording&gt; [--lang] [--review-glossary] [--wait]</c>
/// starts one and follows it; <c>gen resume &lt;game&gt; --fake-agent &lt;recording&gt; [--wait]</c> goes on from the saved
/// stage and chunk (or retries a failed stage, or approves a glossary under review); <c>gen status [&lt;game&gt;]</c> lists
/// jobs; <c>gen cancel &lt;game&gt;</c> cancels the open one. Until tasks 49–51 bring the real stages, jobs run the
/// <see cref="RehearsalStages"/> and only with the fake agent. Ctrl+C / SIGTERM leaves the job interrupted.
/// </summary>
internal static class GenJobCommands
{
    public static IEnumerable<Command> Build(OsEnvironment env) => [BuildRun(env), BuildResume(env), BuildStatus(env), BuildCancel(env)];

    private static Option<string> FakeAgentOption() => new("--fake-agent")
    {
        Description = "Run the stages with the fake agent replaying this JSONL recording (real agents come with tasks 49–51).",
        Required = true,
    };

    private static Option<bool> WaitOption() => new("--wait")
    {
        Description = "When the agent's limit pauses the job, wait for its reset time and go on.",
    };

    private static Command BuildRun(OsEnvironment env)
    {
        var game = GameArgument();
        var fake = FakeAgentOption();
        var language = new Option<string?>("--lang") { Description = "Target language tag (default: the app's setting, else the OS language)." };
        var review = new Option<bool>("--review-glossary") { Description = "Wait for the player's review after the glossary." };
        var allFiles = new Option<bool>("--all-files") { Description = "Copy every file into the working copy." };
        var wait = WaitOption();
        var command = new Command("run", "Start a generation job for a game and follow it.") { game, fake, language, review, allFiles, wait };
        command.SetAction(async (result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            if (Open(env, result.GetValue(game)!, error) is not { } context)
            {
                return 1;
            }

            var tag = result.GetValue(language) ?? GameLanguages.Resolve(context.Settings.GameLanguage, CultureInfo.CurrentUICulture);
            if (!GameLanguages.Tags.Contains(tag, StringComparer.Ordinal))
            {
                error.WriteLine($"'{tag}' is not a supported language tag.");
                return 1;
            }

            var options = new GenerationOptions(result.GetValue(review), result.GetValue(allFiles) ? WorkingCopyScope.AllFiles : null);
            var recording = Path.GetFullPath(result.GetValue(fake)!);
            GenerationRun run;
            try
            {
                var estimate = context.Estimator.Estimate(context.Game.InstallPath, Engine(context.Game), options.Scope, cancellationToken);
                run = context.Jobs.Start(
                    context.Game,
                    context.Policy,
                    context.Index,
                    tag,
                    FakeAgentAdapter.AgentId,
                    options,
                    entry => context.Stages(entry, recording),
                    (estimate.Work.TimeLow + estimate.Work.TimeHigh) / 2,
                    cancellationToken);
            }
            catch (Exception e) when (e is GameBlockedException or GenerationJobException or WorkingCopyException or IOException)
            {
                error.WriteLine(e.Message);
                return 1;
            }

            output.WriteLine($"Job {run.Job.Id} for {context.Game.Name} into {tag}: {run.Folder}");
            return await FollowAsync(context, run, recording, result.GetValue(wait), output, cancellationToken);
        });
        return command;
    }

    private static Command BuildResume(OsEnvironment env)
    {
        var game = GameArgument();
        var fake = FakeAgentOption();
        var wait = WaitOption();
        var command = new Command("resume", "Resume the game's paused or interrupted job from its stage and chunk, retry a failed stage, or approve the glossary under review.")
        {
            game, fake, wait,
        };
        command.SetAction(async (result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            if (Open(env, result.GetValue(game)!, error) is not { } context)
            {
                return 1;
            }

            if (context.Jobs.Store.FindOpen(context.Game.Id) is not { } entry)
            {
                error.WriteLine($"{context.Game.Name} has no open generation job: start one with 'gen run'.");
                return 1;
            }

            var recording = Path.GetFullPath(result.GetValue(fake)!);
            GenerationRun run;
            try
            {
                run = Resume(context, entry, recording, cancellationToken);
            }
            catch (Exception e) when (e is GameBlockedException or GenerationJobException or IOException)
            {
                error.WriteLine(e.Message);
                return 1;
            }

            output.WriteLine($"Job {run.Job.Id} resumed at {Stage(run.Job)}.");
            return await FollowAsync(context, run, recording, result.GetValue(wait), output, cancellationToken);
        });
        return command;
    }

    private static Command BuildStatus(OsEnvironment env)
    {
        var game = new Argument<string?>("game") { Description = "Only this game's jobs.", Arity = ArgumentArity.ZeroOrOne };
        var command = new Command("status", "List generation jobs with their stage, state and progress.") { game };
        command.SetAction(result =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            GameId? only = null;
            if (result.GetValue(game) is { } text)
            {
                if (!GameId.TryParse(text, out var id))
                {
                    error.WriteLine($"'{text}' is not a game ID (steam:<app id> or fp:<64 lowercase hex>).");
                    return 1;
                }

                only = id;
            }

            var store = new GenerationJobStore(AppPaths.Resolve(env));
            store.Recover();
            var jobs = store.List().Where(e => only is null || e.Job.Game == only).ToList();
            if (jobs.Count == 0)
            {
                output.WriteLine("No generation jobs.");
                return 0;
            }

            foreach (var (folder, job) in jobs)
            {
                output.WriteLine($"{job.Game} {job.Id} {job.Language} ({job.GameName}): {Describe(job)}");
                output.WriteLine($"  {folder}");
            }

            return 0;
        });
        return command;
    }

    private static Command BuildCancel(OsEnvironment env)
    {
        var game = GameArgument();
        var delete = new Option<bool>("--delete") { Description = "Also delete the job folder with its working copy." };
        var command = new Command("cancel", "Cancel the game's open generation job (not while it runs).") { game, delete };
        command.SetAction(result =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            if (!GameId.TryParse(result.GetValue(game)!, out var id))
            {
                error.WriteLine($"'{result.GetValue(game)}' is not a game ID (steam:<app id> or fp:<64 lowercase hex>).");
                return 1;
            }

            var paths = AppPaths.Resolve(env);
            var store = new GenerationJobStore(paths);
            if (store.FindOpen(id) is not { } entry)
            {
                error.WriteLine($"{id} has no open generation job.");
                return 1;
            }

            try
            {
                var job = new GenerationJobs(store, new GenerationRunner()).Cancel(entry.Folder);
                if (result.GetValue(delete))
                {
                    new WorkingCopies(paths, FileCloner.Create(), new SystemDiskSpace()).Delete(entry.Folder);
                    output.WriteLine($"Job {job.Id} cancelled; its folder was deleted.");
                }
                else
                {
                    output.WriteLine($"Job {job.Id} cancelled; its files stay in {entry.Folder}.");
                }
            }
            catch (Exception e) when (e is GenerationJobException or IOException or UnauthorizedAccessException)
            {
                error.WriteLine(e.Message);
                return 1;
            }

            return 0;
        });
        return command;
    }

    private static GenerationRun Resume(JobContext context, GenerationJobEntry entry, string recording, CancellationToken cancellationToken) =>
        context.Jobs.Resume(entry.Folder, context.Game, context.Policy, context.Index, e => context.Stages(e, recording), cancellationToken);

    /// <summary>Prints each change; with <paramref name="wait"/> a limit pause is waited out and the job resumed.</summary>
    private static async Task<int> FollowAsync(JobContext context, GenerationRun run, string recording, bool wait, TextWriter output, CancellationToken cancellationToken)
    {
        while (true)
        {
            var last = "";
            run.Changed += (_, job) =>
            {
                var line = Describe(job);
                if (line != last)
                {
                    last = line;
                    output.WriteLine(line);
                }
            };
            var job = await run.Completion;
            if (!wait || job is not { Status: GenerationJobStatus.Paused, PauseReason: GenerationPauseReason.Limit, ResumeAt: { } at })
            {
                return job.Status is GenerationJobStatus.Failed or GenerationJobStatus.Interrupted ? 1 : 0;
            }

            output.WriteLine($"Waiting until {at.ToLocalTime():yyyy-MM-dd HH:mm} for the limit to reset…");
            var scheduler = new GenerationResumeScheduler(
                TimeProvider.System,
                context.Jobs.Store.List,
                e =>
                {
                    run = Resume(context, e, recording, cancellationToken);
                    return Task.CompletedTask;
                });
            while (true)
            {
                var delay = at - DateTimeOffset.UtcNow;
                try
                {
                    await Task.Delay(delay > TimeSpan.Zero ? Min(delay, TimeSpan.FromSeconds(30)) : TimeSpan.Zero, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    output.WriteLine("Stopped waiting; the job stays paused.");
                    return 0;
                }

                if ((await scheduler.TickAsync()).Any(e => e.Job.Id == job.Id))
                {
                    break;
                }
            }
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static string Stage(GenerationJob job) => job.Stage == GenerationStage.Translate && job.Counters.ChunksDone < job.Counters.Chunks
        ? $"{job.Stage} (chunk {job.Counters.ChunksDone + 1} of {job.Counters.Chunks})"
        : job.Stage.ToString();

    internal static string Describe(GenerationJob job)
    {
        var progress = GenerationProgress.Of(job);
        var eta = job.Status == GenerationJobStatus.Running && progress.Remaining is { } left ? $", about {Duration(left)} left" : "";
        var counters = job.Counters.Strings > 0 ? $", {job.Counters.Translated} of {job.Counters.Strings} strings" : "";
        var state = job.Status switch
        {
            GenerationJobStatus.Running => $"running {Stage(job)}",
            GenerationJobStatus.Paused when job.PauseReason == GenerationPauseReason.Limit =>
                $"paused at {Stage(job)} by the agent's limit, resumes at {job.ResumeAt?.ToLocalTime():yyyy-MM-dd HH:mm}",
            GenerationJobStatus.Paused => $"paused at {Stage(job)}",
            GenerationJobStatus.WaitingForReview => "waiting for the glossary review ('gen resume' approves it)",
            GenerationJobStatus.Interrupted => $"interrupted at {Stage(job)} ('gen resume' goes on)",
            GenerationJobStatus.Failed => $"{job.Failure?.Stage} failed: {job.Failure?.Message}",
            GenerationJobStatus.Cancelled => "cancelled",
            _ => "completed",
        };
        return $"{progress.Percent}% {state}{counters}{eta}";
    }

    private static string Duration(TimeSpan time) => time.TotalMinutes < 60
        ? string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, Math.Round(time.TotalMinutes)):0} min")
        : string.Create(CultureInfo.InvariantCulture, $"{time.TotalHours:0.#} h");

    private static Argument<string> GameArgument() => new("game") { Description = "The game (steam:<app id> or fp:<fingerprint>), as listed by 'games list'." };

    private static EngineKind? Engine(Game game) => EngineKind.TryParse(game.Engine, out var kind) ? kind : null;

    private static JobContext? Open(OsEnvironment env, string text, TextWriter error)
    {
        if (!GameId.TryParse(text, out var id))
        {
            error.WriteLine($"'{text}' is not a game ID (steam:<app id> or fp:<64 lowercase hex>).");
            return null;
        }

        var paths = AppPaths.Resolve(env);
        if (PackCommand.FindGame(env, paths, id) is not { } game)
        {
            error.WriteLine($"{id} is not in the game catalog: install it in Steam or add its folder in the app, then run 'games list'.");
            return null;
        }

        if (!game.IsAvailable || !Directory.Exists(game.InstallPath))
        {
            error.WriteLine($"{game.Name} ({id}) is not available: its folder {game.InstallPath} is missing.");
            return null;
        }

        var store = new GenerationJobStore(paths);
        store.Recover();
        var tools = RecipeTools.Create(env);
        var copies = new WorkingCopies(paths, FileCloner.Create(), new SystemDiskSpace());
        return new JobContext(
            game,
            new Core.Settings.SettingsStore(paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<Core.Settings.SettingsStore>.Instance, TimeProvider.System).Load(),
            new GenerationJobs(store, new GenerationRunner()),
            new GenerationEstimator(tools, copies),
            tools,
            copies,
            AntiCheatPolicy.Bundled,
            DevCommand.SavedIndex(env, paths));
    }

    private sealed record JobContext(
        Game Game,
        Core.Settings.AppSettings Settings,
        GenerationJobs Jobs,
        GenerationEstimator Estimator,
        RecipeTools Tools,
        WorkingCopies Copies,
        AntiCheatPolicy Policy,
        RegistryIndex? Index)
    {
        public GenerationStages Stages(GenerationJobEntry entry, string recording) =>
            RehearsalStages.Create(
                Game,
                Estimator,
                Copies,
                Tools,
                new FakeAgentAdapter(new ProcessRunner(), FakeAgentReplayer.ForCurrentProcess("agent", "replay"), recording));
    }
}

/// <summary>
/// Stand-ins for the stages of tasks 49–51, so a job can run end to end with the fake agent (task 47's check): Setup
/// makes the working copy (a half-made one left by a crash is made again), Extract counts the toolkit's strings and
/// plans chunks of <see cref="GenerationEstimateModel.StringsPerChunk"/>, Glossary runs the agent once, Translate once
/// per chunk (the prompt names the chunk; nothing is stored), Build does nothing.
/// </summary>
internal static class RehearsalStages
{
    public static GenerationStages Create(Game game, GenerationEstimator estimator, WorkingCopies copies, RecipeTools tools, IAgentAdapter agent) => new(
    [
        new Stage(GenerationStage.Setup, (context, ct) =>
        {
            try
            {
                var estimate = estimator.Estimate(game.InstallPath, EngineKind.TryParse(game.Engine, out var kind) ? kind : null, context.Job.Options.Scope, ct);
                copies.DeleteCopy(context.Folder);
                var copy = copies.Create(estimate.Copy, context.Folder, estimate.ReserveBytes, cancellationToken: ct);
                context.Log($"Working copy: {copy.Files} files, {copy.ClonedBytes} bytes cloned, {copy.CopiedBytes} copied.");
                return Task.FromResult<GenerationStepResult>(new GenerationStepResult.StageDone());
            }
            catch (WorkingCopyException e)
            {
                return Task.FromResult<GenerationStepResult>(new GenerationStepResult.Failed(GenerationFailureKind.WorkingCopy, e.Message));
            }
        }),
        new Stage(GenerationStage.Extract, (context, ct) =>
        {
            var detection = new EngineDetector().Detect(context.CopyFolder);
            if (detection.Match is not { } match || tools.For(match.Kind) is not { } tool)
            {
                return Task.FromResult<GenerationStepResult>(new GenerationStepResult.Failed(
                    GenerationFailureKind.Toolkit, "The toolkit doesn't read this game's engine; unknown engines come with task 57."));
            }

            int strings;
            try
            {
                strings = tool.Extract(context.CopyFolder, ct).Strings.Count;
            }
            catch (RecipeException e)
            {
                return Task.FromResult<GenerationStepResult>(new GenerationStepResult.Failed(GenerationFailureKind.Toolkit, e.Message));
            }

            var chunks = (strings + GenerationEstimateModel.StringsPerChunk - 1) / GenerationEstimateModel.StringsPerChunk;
            context.Log($"Extracted {strings} strings with {tool.Id} {tool.Version.ToString(3)}: {chunks} chunks.");
            return Task.FromResult<GenerationStepResult>(new GenerationStepResult.StageDone(new GenerationCounters(strings, 0, chunks, 0)));
        }),
        new Stage(GenerationStage.Glossary, async (context, ct) =>
            await RunAgentAsync(agent, context, "Glossary stage (rehearsal).", ct) ?? new GenerationStepResult.StageDone()),
        new Stage(GenerationStage.Translate, async (context, ct) =>
        {
            var counters = context.Job.Counters;
            var chunk = counters.ChunksDone;
            var size = Math.Min(GenerationEstimateModel.StringsPerChunk, counters.Strings - (chunk * GenerationEstimateModel.StringsPerChunk));
            return await RunAgentAsync(agent, context, $"Translate stage (rehearsal): chunk {chunk + 1} of {counters.Chunks}.", ct)
                ?? new GenerationStepResult.ChunkDone(Math.Max(0, size));
        }),
        new Stage(GenerationStage.Build, (_, _) => Task.FromResult<GenerationStepResult>(new GenerationStepResult.StageDone())),
    ]);

    private static async Task<GenerationStepResult?> RunAgentAsync(IAgentAdapter agent, GenerationStepContext context, string prompt, CancellationToken ct)
    {
        if (await agent.DetectAsync(ct) is not { } installation)
        {
            return new GenerationStepResult.Failed(GenerationFailureKind.AgentMissing, $"{agent.DisplayName} isn't installed.");
        }

        context.Log($"Agent: {prompt}");
        var outcome = await AgentStepRun.RunAsync(
            agent,
            installation,
            new AgentRunRequest(prompt, context.CopyFolder),
            e =>
            {
                if (e is AgentMessage message)
                {
                    context.Log($"Agent says: {message.Text}");
                }
            },
            ct);
        return outcome.Stop;
    }

    private sealed class Stage(GenerationStage stage, Func<GenerationStepContext, CancellationToken, Task<GenerationStepResult>> step) : IGenerationStage
    {
        GenerationStage IGenerationStage.Stage => stage;

        public Task<GenerationStepResult> RunStepAsync(GenerationStepContext context, CancellationToken cancellationToken) => step(context, cancellationToken);
    }
}
