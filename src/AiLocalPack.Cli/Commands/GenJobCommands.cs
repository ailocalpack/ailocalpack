using System.CommandLine;
using System.Globalization;
using AiLocalPack.Agents;
using AiLocalPack.Agents.Fake;
using AiLocalPack.Agents.Processes;
using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Background;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Languages;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;
using AiLocalPack.Pipeline;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Generation;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Cli.Commands;

/// <summary>
/// Generation jobs (tasks 47, 49): <c>gen run &lt;game&gt; [--agent claude|codex | --fake-agent &lt;recording&gt;] [--lang]
/// [--review-glossary] [--until &lt;stage&gt;] [--wait]</c> starts one and follows it; <c>gen resume &lt;game&gt; [--fake-agent]
/// [--until] [--wait]</c> goes on from the saved stage and chunk (or retries a failed stage, or approves a glossary under
/// review); <c>gen status [&lt;game&gt;]</c> lists jobs; <c>gen cancel &lt;game&gt;</c> cancels the open one. Every stage is
/// the real one (<see cref="GenerationPipeline"/>), with the fake agent too. After each chunk the strings just translated
/// and the agent's usage so far are printed; a completed job prints its local pack (task 51). Ctrl+C / SIGTERM leaves
/// the job interrupted.
/// </summary>
internal static class GenJobCommands
{
    public static IEnumerable<Command> Build(OsEnvironment env) => [BuildRun(env), BuildResume(env), BuildStatus(env), BuildCancel(env)];

    private static Option<string?> FakeAgentOption() => new("--fake-agent")
    {
        Description = "Run the stages with the fake agent replaying this JSONL recording (development, tests).",
    };

    private static Option<string?> UntilOption() => new("--until")
    {
        Description = "Pause the job once this stage is done: setup, extract, glossary or translate ('gen resume' goes on).",
    };

    /// <summary>The stage to pause before for <c>--until &lt;stage&gt;</c>, or the reason the value is wrong.</summary>
    internal static (GenerationStage? PauseBefore, string? Error) Until(string? text) => text?.ToLowerInvariant() switch
    {
        null => (null, null),
        "setup" => (GenerationStage.Extract, null),
        "extract" => (GenerationStage.Glossary, null),
        "glossary" => (GenerationStage.Translate, null),
        "translate" => (GenerationStage.Build, null),
        _ => (null, $"--until takes setup, extract, glossary or translate, not '{text}'."),
    };

    private static Option<bool> WaitOption() => new("--wait")
    {
        Description = "When the agent's limit pauses the job, wait for its reset time and go on.",
    };

    private static Command BuildRun(OsEnvironment env)
    {
        var game = GameArgument();
        var agentOption = new Option<string?>("--agent") { Description = $"The agent ({string.Join(", ", AgentAdapters.Ids)}; default: the app's default agent, else claude)." };
        var fake = FakeAgentOption();
        var language = new Option<string?>("--lang") { Description = "Target language tag (default: the app's setting, else the OS language)." };
        var review = new Option<bool>("--review-glossary") { Description = "Wait for the player's review after the glossary." };
        var allFiles = new Option<bool>("--all-files") { Description = "Copy every file into the working copy." };
        var model = new Option<string?>("--model") { Description = "The agent's model (default: the agent's own)." };
        var update = new Option<bool>("--update") { Description = "Translate only the new and changed strings on top of the installed pack (its next revision, in its language)." };
        var until = UntilOption();
        var wait = WaitOption();
        var command = new Command("run", "Start a generation job for a game and follow it.") { game, agentOption, fake, language, review, allFiles, model, update, until, wait };
        command.SetAction(async (result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            if (Open(env, result.GetValue(game)!, error) is not { } context)
            {
                return 1;
            }

            UpdateBase? updateBase = null;
            InstalledPack? installed = null;
            if (result.GetValue(update))
            {
                installed = new InstalledPackStore(context.Paths).Find(context.Game.Id);
                if (installed is null)
                {
                    error.WriteLine($"{context.Game.Name} has no installed pack to update.");
                    return 1;
                }

                var load = UpdateBaseLoader.Load(installed, context.LocalPacks, new PackCache(context.Paths));
                if (load.Base is null)
                {
                    error.WriteLine($"The installed pack {installed.PackId} rev {installed.Revision} can't be updated: {load.Detail}");
                    return 1;
                }

                updateBase = load.Base;
                if (result.GetValue(language) is { } asked && asked != installed.Language)
                {
                    error.WriteLine($"The installed pack is in '{installed.Language}': an update keeps its language.");
                    return 1;
                }
            }

            var tag = installed?.Language ?? result.GetValue(language) ?? GameLanguages.Resolve(context.Settings.GameLanguage, CultureInfo.CurrentUICulture);
            if (!GameLanguages.Tags.Contains(tag, StringComparer.Ordinal))
            {
                error.WriteLine($"'{tag}' is not a supported language tag.");
                return 1;
            }

            var (pauseBefore, untilError) = Until(result.GetValue(until));
            if (untilError is not null)
            {
                error.WriteLine(untilError);
                return 1;
            }

            var recording = result.GetValue(fake) is { } path ? Path.GetFullPath(path) : null;
            var agentId = recording is not null ? FakeAgentAdapter.AgentId : (result.GetValue(agentOption) ?? context.Settings.DefaultAgent ?? AgentAdapters.Ids[0]).ToLowerInvariant();
            if (recording is not null && result.GetValue(agentOption) is not null)
            {
                error.WriteLine("Give either --agent or --fake-agent, not both.");
                return 1;
            }

            if (recording is null && !AgentAdapters.Ids.Contains(agentId))
            {
                error.WriteLine($"'{agentId}' is not an agent: use {string.Join(" or ", AgentAdapters.Ids)}.");
                return 1;
            }

            var options = new GenerationOptions(
                result.GetValue(review),
                result.GetValue(allFiles) ? WorkingCopyScope.AllFiles : null,
                result.GetValue(model),
                updateBase?.Reference(installed!.Local));
            var plan = new StagePlan(agentId, recording, pauseBefore);
            GenerationRun run;
            try
            {
                var estimate = context.Estimator.Estimate(context.Game.InstallPath, Engine(context.Game), options.Scope, cancellationToken, updateBase?.Strings);
                if (estimate.Update is { } diff)
                {
                    output.WriteLine($"Update of {updateBase!.Manifest.Id} rev {updateBase.Manifest.Revision}: {diff.Added.Count} new strings, {diff.Changed.Count} changed, {diff.Removed.Count} removed, {diff.Unchanged.Count} kept.");
                }

                run = context.Jobs.Start(
                    context.Game,
                    context.Policy,
                    context.Index,
                    tag,
                    agentId,
                    options,
                    _ => context.Stages(plan),
                    (estimate.Work.TimeLow + estimate.Work.TimeHigh) / 2,
                    cancellationToken,
                    updateBase);
            }
            catch (Exception e) when (e is GameBlockedException or GenerationJobException or WorkingCopyException or IOException)
            {
                error.WriteLine(e.Message);
                return 1;
            }

            output.WriteLine($"Job {run.Job.Id} for {context.Game.Name} into {tag} with {agentId}: {run.Folder}");
            return await FollowAsync(context, run, plan, result.GetValue(wait), output, cancellationToken);
        });
        return command;
    }

    private static Command BuildResume(OsEnvironment env)
    {
        var game = GameArgument();
        var fake = FakeAgentOption();
        var until = UntilOption();
        var wait = WaitOption();
        var command = new Command("resume", "Resume the game's paused or interrupted job from its stage and chunk, retry a failed stage, or approve the glossary under review.")
        {
            game, fake, until, wait,
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

            var (pauseBefore, untilError) = Until(result.GetValue(until));
            if (untilError is not null)
            {
                error.WriteLine(untilError);
                return 1;
            }

            var recording = result.GetValue(fake) is { } path ? Path.GetFullPath(path) : null;
            if (recording is null && entry.Job.Agent == FakeAgentAdapter.AgentId)
            {
                error.WriteLine("The job runs with the fake agent: give its recording with --fake-agent.");
                return 1;
            }

            if (recording is null && !AgentAdapters.Ids.Contains(entry.Job.Agent))
            {
                error.WriteLine($"The job's agent '{entry.Job.Agent}' is unknown to this version.");
                return 1;
            }

            var plan = new StagePlan(recording is null ? entry.Job.Agent : FakeAgentAdapter.AgentId, recording, pauseBefore);
            GenerationRun run;
            try
            {
                run = Resume(context, entry, plan, cancellationToken);
            }
            catch (Exception e) when (e is GameBlockedException or GenerationJobException or IOException)
            {
                error.WriteLine(e.Message);
                return 1;
            }

            output.WriteLine($"Job {run.Job.Id} resumed at {Stage(run.Job)}.");
            return await FollowAsync(context, run, plan, result.GetValue(wait), output, cancellationToken);
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

    private static GenerationRun Resume(JobContext context, GenerationJobEntry entry, StagePlan plan, CancellationToken cancellationToken) =>
        context.Jobs.Resume(entry.Folder, context.Game, context.Policy, context.Index, _ => context.Stages(plan), cancellationToken);

    /// <summary>Prints each change; with <paramref name="wait"/> a limit pause is waited out and the job resumed.</summary>
    private static async Task<int> FollowAsync(JobContext context, GenerationRun run, StagePlan plan, bool wait, TextWriter output, CancellationToken cancellationToken)
    {
        while (true)
        {
            var last = "";
            var chunks = run.Job.Counters.ChunksDone;
            IReadOnlyList<RecentTranslation> shown = [];
            run.Changed += (_, job) =>
            {
                var line = Describe(job);
                if (line != last)
                {
                    last = line;
                    output.WriteLine(line);
                }

                if (job.Counters.ChunksDone > chunks)
                {
                    chunks = job.Counters.ChunksDone;
                    var progress = PipelineProgressFile.Read(new GenerationFiles(run.Folder));
                    WriteProgress(progress, progress.Recent.SequenceEqual(shown) ? [] : progress.Recent, output);
                    shown = progress.Recent;
                }
            };
            var job = await run.Completion;
            if (job.Status == GenerationJobStatus.Completed)
            {
                WriteLocalPack(context, job, output);
            }

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
                    run = Resume(context, e, plan, cancellationToken);
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

    /// <summary>The completed job's local pack: folder, coverage, test run, fonts, warnings.</summary>
    private static void WriteLocalPack(JobContext context, GenerationJob job, TextWriter output)
    {
        if (context.LocalPacks.FindByJob(job.Game, job.Id) is not { } pack)
        {
            output.WriteLine("The job's local pack wasn't found.");
            return;
        }

        output.WriteLine($"Local pack {pack.Manifest.Id}: {pack.Folder}");
        foreach (var line in PackCommand.DescribeLocal(pack))
        {
            output.WriteLine("  " + line);
        }
    }

    /// <summary>The "just translated" lines (when new) and the usage so far, after a chunk.</summary>
    private static void WriteProgress(PipelineProgress progress, IReadOnlyList<RecentTranslation> recentLines, TextWriter output)
    {
        foreach (var recent in recentLines)
        {
            output.WriteLine($"  {Flat(recent.Original)} → {Flat(recent.Translation)}");
        }

        var usage = progress.Usage;
        if (usage.Runs > 0)
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  agent so far: {usage.Runs} runs, {usage.InputTokens:N0} input + {usage.CacheReadTokens:N0} cached, {usage.OutputTokens:N0} output tokens")
                + (usage.CostUsd is { } cost ? string.Create(CultureInfo.InvariantCulture, $", ${cost:0.00}") : ""));
        }
    }

    private static string Flat(string text)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= 80 ? flat : flat[..80] + "…";
    }

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
            DevCommand.SavedIndex(env, paths),
            env,
            new LocalPackStore(paths),
            paths);
    }

    /// <summary>The agent of a run (<paramref name="Recording"/> = the fake agent's) and where it pauses.</summary>
    private sealed record StagePlan(string AgentId, string? Recording, GenerationStage? PauseBefore);

    private sealed record JobContext(
        Game Game,
        Core.Settings.AppSettings Settings,
        GenerationJobs Jobs,
        GenerationEstimator Estimator,
        RecipeTools Tools,
        WorkingCopies Copies,
        AntiCheatPolicy Policy,
        RegistryIndex? Index,
        OsEnvironment Env,
        LocalPackStore LocalPacks,
        AppPaths Paths)
    {
        /// <summary>Every stage, with the plan's agent.</summary>
        public GenerationStages Stages(StagePlan plan)
        {
            IAgentAdapter adapter = plan.Recording is { } recording
                ? new FakeAgentAdapter(new ProcessRunner(), FakeAgentReplayer.ForCurrentProcess("agent", "replay"), recording)
                : AgentCommand.Adapter(Env, plan.AgentId)!;
            // The entry assembly's path only matters under `dotnet ailocalpack.dll`; the single-file CLI (empty
            // Location) is started by its own executable, which AppLaunch takes from the process path.
#pragma warning disable IL3000
            var entryAssembly = System.Reflection.Assembly.GetEntryAssembly()?.Location;
#pragma warning restore IL3000
            var cli = AppLaunch.ForCurrentProcess(Env, Environment.ProcessPath, entryAssembly)
                ?? throw new IOException("The CLI's own path is unknown, so the agent can't start its toolkit server.");
            var build = new BuildServices(RecipeCommands.CreateFixer(Env), LocalPacks, new GameTestRunner(new ProcessRunner(), Env.Os));
            var services = new PipelineServices(Game, new PipelineAgent(adapter, ToolkitServerProgram.Cli(cli, Env)), Tools, Estimator, Copies, Build: build);
            return GenerationPipeline.Create(services, pauseBefore: plan.PauseBefore);
        }
    }
}
