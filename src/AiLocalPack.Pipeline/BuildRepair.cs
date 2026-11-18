using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AiLocalPack.Agents;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Recipes.Agent;

namespace AiLocalPack.Pipeline;

/// <summary>What a repair run did.</summary>
/// <param name="Stop">The agent hit its limit (the job pauses), else <c>null</c>.</param>
/// <param name="Problem">The agent's run failed (in English), else <c>null</c>.</param>
/// <param name="Changed">It changed translations, the glossary or its scripts (the stage builds and tests again).</param>
/// <param name="Strings">Translations it changed.</param>
/// <param name="Reply">Its reply to the player.</param>
public sealed record BuildRepairResult(GenerationStepResult? Stop, string? Problem, bool Changed, int Strings, bool Scripts, string Reply);

/// <summary>
/// The Build stage's repair run (decided 2026-11-17 after Barony, whose translated game crashed in every test run while
/// retrying only repeated the same build): when the translated game crashes, the job's agent gets what the test run
/// found (<see cref="BuildFailureReport"/>: the report and the crashed run's output copied into its working folder,
/// the job's <c>agent/</c>), its earlier attempts on this job (<c>build-repairs.md</c>) and <c>test_run</c>
/// (<see cref="FixTestRuns"/>, at most <see cref="FixTestRuns.MaxRuns"/> game runs), with
/// <see cref="PromptTemplate.SystemRepair"/> and <see cref="PromptTemplate.RepairBuild"/>; it changes translations,
/// the glossary or (an unknown engine) its scripts. The stage then builds and tests once more. The attempt and the
/// agent's reply are added to <c>build-repairs.md</c>; the transcript goes to <c>logs/agent-repair.jsonl</c>.
/// </summary>
public sealed class BuildRepair(Game game, PipelineAgent agent, RecipeTools tools, BuildServices services)
{
    /// <summary>The job's repair attempts so far (Markdown, one section per attempt).</summary>
    public const string AttemptsFile = "build-repairs.md";

    /// <summary>The copies in the agent's working folder.</summary>
    public const string ReportCopy = "build-failure.json";

    public const string LogCopy = "test-run.log";

    /// <summary>Full game runs the agent may make in one repair (each opens the game on the player's screen).</summary>
    public const int MaxFullRuns = FixTestRuns.MaxRuns;

    /// <summary>Quick runs the agent may make besides (<see cref="TestRunRequest.Quick"/>).</summary>
    public const int MaxQuickRuns = FixTestRuns.MaxQuickRuns;

    /// <summary>All the game runs of one repair (the progress card counts them together).</summary>
    public const int MaxTestRuns = MaxFullRuns + MaxQuickRuns;

    private const int AttemptsChars = 4000;

    public async Task<BuildRepairResult> RunAsync(GenerationStepContext context, string failure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var files = context.Files;
        SourceStrings? source;
        try
        {
            source = SourceStringsFile.Read(files);
        }
        catch (GenerationFileException e)
        {
            return new BuildRepairResult(null, e.Message, false, 0, false, string.Empty);
        }

        if (source is null)
        {
            return new BuildRepairResult(null, "source.json is missing.", false, 0, false, string.Empty);
        }

        var scripts = source.Tool == AgentScriptTool.ToolId;
        var working = files.AgentFolder;
        var copies = new List<string>();
        try
        {
            Directory.CreateDirectory(working);
            copies.Add(CopyInto(files.BuildFailurePath, working, ReportCopy));
            copies.Add(CopyInto(Path.Combine(context.Folder, "logs", "test-run.log"), working, LogCopy));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new BuildRepairResult(null, $"What the test run found can't be handed to the agent: {e.Message}", false, 0, false, string.Empty);
        }

        var before = Snapshot(files);
        var attempt = Attempts(context.Folder).Count(l => l.StartsWith("## ", StringComparison.Ordinal)) + 1;
        context.Log($"Repair {attempt}: the translated game crashed in the test run; {agent.DisplayName} looks for the cause.");
        var bisect = await NarrowAsync(context, cancellationToken).ConfigureAwait(false);
        BuildStage.Step(context, new BuildProgress(BuildStep.Repairing));

        var values = PromptValues.RepairBuild(
            game.Name,
            source.Engine,
            source.Strings.Count,
            failure,
            BuildFailureReport.DescribeFiles(files),
            ReportCopy,
            LogCopy,
            AttemptsText(context.Folder),
            bisect?.Summary ?? "The app didn't narrow the crash down to a file.",
            MaxFullRuns,
            MaxQuickRuns,
            scripts);
        var system = new AgentSystemPrompt(PromptTemplate.SystemRepair, PromptValues.SystemRepair(game.Name, context.Job.Language, scripts ? context.GameFolder : null));
        var host = new FixTestRuns(game, tools, services, context, source, working, (number, _) =>
            BuildStage.Step(context, new BuildProgress(BuildStep.Repairing, RepairRuns: number)));
        host.Clean();
        var extras = new AgentRunExtras(system, null, "repair", FixTestRuns.MaxRuns);
        using var serving = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var served = host.ServeAsync(serving.Token);
        PipelineAgentResult run;
        try
        {
            run = await agent.RunAsync(context, PromptTemplate.RepairBuild, values, game.Name, cancellationToken,
                workspace: scripts ? new AgentWorkspace(working, services.Originals(context.GameFolder)) : null,
                logTag: "Repair",
                extraTools: new HashSet<AgentBuiltinTool> { AgentBuiltinTool.ReadFiles },
                extras: extras).ConfigureAwait(false);
        }
        finally
        {
            await serving.CancelAsync().ConfigureAwait(false);
            await served.ConfigureAwait(false);
            host.Clean();
            foreach (var copy in copies)
            {
                TryDelete(copy);
            }
        }

        if (run.Stop is GenerationStepResult.LimitReached)
        {
            AddAttempt(context, attempt, "The agent's usage limit was reached; the job goes on when it resets.");
            return new BuildRepairResult(run.Stop, null, false, 0, false, string.Empty);
        }

        if (run.Stop is GenerationStepResult.Failed failed)
        {
            AddAttempt(context, attempt, $"The agent's run failed: {failed.Message}");
            return new BuildRepairResult(null, failed.Message, false, 0, false, string.Empty);
        }

        var after = Snapshot(files);
        var strings = ChangedStrings(before.Strings, after.Strings);
        var scriptsChanged = before.Scripts != after.Scripts;
        var changed = strings > 0 || scriptsChanged || before.Glossary != after.Glossary;
        var reply = (run.Reply ?? string.Empty).Trim();
        context.Log($"Repair {attempt}: {strings} translations changed" + (scriptsChanged ? ", scripts changed" : "") + (before.Glossary != after.Glossary ? ", glossary changed" : "") + ".");
        AddAttempt(context, attempt, (bisect is { Crashing.Count: > 0 } found ? $"The app found the game crashes with {string.Join(", ", found.Crashing.Take(5).Select(f => $"`{f}`"))}. " : string.Empty)
            + (changed ? $"Changed {strings} translations" + (scriptsChanged ? " and the scripts" : "") + "." : "Changed nothing.")
            + (reply.Length > 0 ? "\n\n" + reply : string.Empty));
        return new BuildRepairResult(null, null, changed, strings, scriptsChanged, reply);
    }

    /// <summary>
    /// Narrows the crash down to the rebuilt file that causes it with quick runs (<see cref="TestRunBisect"/>) when more
    /// than one file was rebuilt; <c>null</c> when it wasn't tried. The build folder still holds the crashed build.
    /// </summary>
    private async Task<TestRunBisectResult?> NarrowAsync(GenerationStepContext context, CancellationToken cancellationToken)
    {
        var files = BuildFailureReport.Files(context.Files);
        if (files.Count <= 1 || !Directory.Exists(context.Files.BuildFolder))
        {
            return null;
        }

        BuildStage.Step(context, new BuildProgress(BuildStep.Narrowing));
        try
        {
            return await new TestRunBisect(services.TestRunner).RunAsync(
                game,
                context.GameFolder,
                context.Files.BuildFolder,
                files,
                context.Folder,
                context.Log,
                (number, _) => BuildStage.Step(context, new BuildProgress(BuildStep.Narrowing, RepairRuns: number)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            context.Log($"Narrowing the crash down stopped: {e.Message}");
            return null;
        }
    }

    /// <summary>Adds what the test run after a repair showed to its section of <c>build-repairs.md</c>.</summary>
    public static void AddOutcome(GenerationStepContext context, string outcome)
    {
        ArgumentNullException.ThrowIfNull(context);
        Append(context.Folder, $"\nTest run after it: {outcome}\n");
    }

    /// <summary>The job's earlier attempts as prompt text (the newest part when long).</summary>
    public static string AttemptsText(string jobFolder)
    {
        var text = string.Join('\n', Attempts(jobFolder)).Trim();
        if (text.Length == 0)
        {
            return "This is the first attempt to fix this crash.";
        }

        if (text.Length > AttemptsChars)
        {
            text = "…" + text[^AttemptsChars..];
        }

        return "Your earlier attempts on this job (the crash above came after the last one):\n\n" + text;
    }

    private static IReadOnlyList<string> Attempts(string jobFolder)
    {
        try
        {
            var path = Path.Combine(jobFolder, AttemptsFile);
            return File.Exists(path) ? File.ReadAllLines(path) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void AddAttempt(GenerationStepContext context, int attempt, string text) =>
        Append(context.Folder, string.Create(CultureInfo.InvariantCulture, $"## Attempt {attempt} ({context.Time.GetUtcNow().UtcDateTime:yyyy-MM-dd HH:mm} UTC)\n\n{text.Trim()}\n"));

    private static void Append(string jobFolder, string text)
    {
        try
        {
            File.AppendAllText(Path.Combine(jobFolder, AttemptsFile), text.ReplaceLineEndings("\n") + "\n", Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string CopyInto(string source, string folder, string name)
    {
        var target = Path.Combine(folder, name);
        if (File.Exists(source))
        {
            File.Copy(source, target, overwrite: true);
        }

        return target;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record State(IReadOnlyDictionary<string, string> Strings, string? Glossary, string? Scripts);

    private static State Snapshot(GenerationFiles files)
    {
        IReadOnlyDictionary<string, string> strings;
        try
        {
            strings = files.ReadStrings()?.Strings ?? new Dictionary<string, string>();
        }
        catch (GenerationFileException)
        {
            strings = new Dictionary<string, string>();
        }

        string? scripts;
        try
        {
            scripts = FixJobState.ScriptsFingerprint(files);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            scripts = null;
        }

        return new State(new Dictionary<string, string>(strings, StringComparer.Ordinal), Hash(files.GlossaryPath), scripts);
    }

    private static string? Hash(string path)
    {
        try
        {
            return File.Exists(path) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int ChangedStrings(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Keys.Union(b.Keys, StringComparer.Ordinal).Count(k => !string.Equals(a.GetValueOrDefault(k), b.GetValueOrDefault(k), StringComparison.Ordinal));
}
