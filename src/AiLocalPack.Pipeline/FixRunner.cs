using System.Globalization;
using System.Text;
using System.Text.Json;
using AiLocalPack.Agents;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Recipes.Agent;

namespace AiLocalPack.Pipeline;

/// <summary>A fix request: the player's words and what they attached.</summary>
public sealed record FixRequest(string Text, IReadOnlyList<FixAttachment> Attachments);

/// <summary>A fix while it runs (streamed to the conversation): the agent turn's steps so far and the build step.</summary>
/// <param name="Steps">Step codes so far (<see cref="FixSteps"/>).</param>
public sealed record FixProgress(int Turn, IReadOnlyList<string> Steps, string? Reply);

/// <summary>Why a fix couldn't start.</summary>
public enum FixProblem
{
    /// <summary>Another fix or a generation run holds the job's lock.</summary>
    Busy,

    /// <summary>The job has no local pack to fix (deleted), or its folder is gone.</summary>
    NoPack,

    /// <summary>The request is empty or too long.</summary>
    BadRequest,

    /// <summary>No such plan or draft, or nothing to do.</summary>
    NotFound,

    /// <summary>The fix files can't be read or written.</summary>
    Disk,

    /// <summary>The job's agent can't be started by this app (unknown to this version, or the app's own path unknown).</summary>
    Agent,
}

public sealed class FixException(FixProblem problem, string message) : Exception(message)
{
    public FixProblem Problem { get; } = problem;
}

/// <summary>
/// The step codes of a fix turn's ✓ list: the agent's tool calls by tool name (an MCP tool's server prefix removed:
/// <c>put_translations</c>, <c>Bash</c>…), then the app's own <see cref="Build"/> and <see cref="TestRun"/>. Consecutive
/// repeats are kept once.
/// </summary>
public static class FixSteps
{
    public const string Build = "app:build";

    public const string Saved = "app:saved";

    /// <summary>The tool's name without <c>mcp__&lt;server&gt;__</c>.</summary>
    public static string Of(string toolName)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        if (toolName.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var cut = toolName.IndexOf("__", 5, StringComparison.Ordinal);
            return cut > 0 ? toolName[(cut + 2)..] : toolName;
        }

        return toolName;
    }

    internal static void Add(List<string> steps, string step)
    {
        if (steps.Count == 0 || steps[^1] != step)
        {
            steps.Add(step);
        }
    }
}

/// <summary>Renders what the player sent and the conversation so far for the <see cref="PromptTemplate.Fix"/> prompt.</summary>
public static class FixPrompt
{
    private const int HistoryTurns = 6;

    // Translations as written (Cyrillic, CJK), as the translate prompts send them.
    private static readonly JsonSerializerOptions LineOptions = new(FixPromptJson.Default.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private const int HistoryText = 600;

    /// <summary>Attachments as prompt text: the session, the log's tail in a fence, the draft's changes, screenshot paths, picked strings as JSON lines.</summary>
    public static string Attachments(IReadOnlyList<FixAttachment> attachments, FixContext context)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(context);
        var text = new StringBuilder();
        foreach (var a in attachments)
        {
            switch (a.Kind)
            {
                case FixAttachmentKind.Session:
                    text.Append("The game session: it closed ")
                        .Append(a.Seconds is { } seconds ? string.Create(CultureInfo.InvariantCulture, $"{seconds:0.#} s after it started") : "soon after it started")
                        .Append(a.ExitCode is { } code ? string.Create(CultureInfo.InvariantCulture, $" with exit code {code}") : string.Empty)
                        .AppendLine(".");
                    break;
                case FixAttachmentKind.GameLog when a.Path is { } log && ReadText(log) is { } lines:
                    text.Append("The end of the game's log `").Append(a.Name).AppendLine("`:").AppendLine("```").AppendLine(lines.TrimEnd()).AppendLine("```");
                    break;
                case FixAttachmentKind.DraftChanges:
                    text.Append("What Draft ").Append(context.Draft.ToString(CultureInfo.InvariantCulture)).Append(" changes in the game: ")
                        .Append(context.Translated.ToString("N0", CultureInfo.InvariantCulture)).Append(" of ")
                        .Append(context.Total.ToString("N0", CultureInfo.InvariantCulture)).Append(" strings translated");
                    if (context.Files.Count > 0)
                    {
                        text.Append(", from ").Append(string.Join(", ", context.Files.Take(20)));
                        if (context.Files.Count > 20)
                        {
                            text.Append(" and ").Append((context.Files.Count - 20).ToString("N0", CultureInfo.InvariantCulture)).Append(" more files");
                        }
                    }

                    text.AppendLine(".");
                    break;
                case FixAttachmentKind.Screenshot when a.Path is { } image:
                    text.Append("A screenshot the player took: `").Append(image).AppendLine("` (look at it with your file reading tool).");
                    break;
                case FixAttachmentKind.Strings when a.Keys is { Count: > 0 } keys:
                    text.AppendLine("Strings the player points at (key, original, your translation):");
                    foreach (var key in keys)
                    {
                        text.AppendLine(JsonSerializer.Serialize(new FixStringLine(key, context.Original(key), context.Translation(key)), LineOptions.GetTypeInfo(typeof(FixStringLine))));
                    }

                    break;
                default:
                    break;
            }
        }

        return text.Length == 0 ? "The player attached nothing." : text.ToString().TrimEnd();
    }

    /// <summary>The last turns before this request, each cut to a few hundred characters.</summary>
    public static string History(IReadOnlyList<FixTurn> earlier)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        if (earlier.Count == 0)
        {
            return "This is the player's first request.";
        }

        var text = new StringBuilder("Earlier in this conversation:\n");
        foreach (var turn in earlier.TakeLast(HistoryTurns))
        {
            var words = turn.Text.ReplaceLineEndings(" ").Trim();
            if (words.Length > HistoryText)
            {
                words = words[..HistoryText] + "…";
            }

            var what = turn.Kind == FixTurnKind.Player ? "Player" : "You";
            var outcome = turn.Kind == FixTurnKind.Agent ? turn.Outcome switch
            {
                FixTurnOutcome.Changed => $" (made Draft {turn.Change?.Draft})",
                FixTurnOutcome.Planned => turn.Plan?.State == FixPlanState.Applied ? " (the player applied your plan)" : " (the player didn't apply your plan)",
                FixTurnOutcome.Failed => " (failed, nothing changed)",
                FixTurnOutcome.Stopped => " (stopped by the player, nothing changed)",
                _ => string.Empty,
            } : string.Empty;
            text.Append("- ").Append(what).Append(outcome).Append(": ").AppendLine(words.Length == 0 ? "(no words)" : words);
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>The reply without its last <c>Choices: a | b | c</c> line, and those choices (two to four, each ≤ 80 characters).</summary>
    public static (string Text, IReadOnlyList<string> Choices) SplitChoices(string? reply)
    {
        var text = (reply ?? string.Empty).ReplaceLineEndings("\n").TrimEnd();
        var cut = text.LastIndexOf('\n');
        var last = text[(cut + 1)..].Trim();
        if (!last.StartsWith("Choices:", StringComparison.OrdinalIgnoreCase))
        {
            return (text.Trim(), []);
        }

        var choices = last["Choices:".Length..].Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(c => c.Trim('`', '"', '«', '»', ' ')).Where(c => c.Length is > 0 and <= 80).Distinct(StringComparer.Ordinal).Take(4).ToList();
        return (cut < 0 ? string.Empty : text[..cut].Trim(), choices.Count >= 2 ? choices : []);
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>What a fix prompt says about the current draft.</summary>
public sealed class FixContext(int draft, int translated, int total, IReadOnlyList<string> files, IReadOnlyDictionary<string, string> originals, IReadOnlyDictionary<string, string> translations)
{
    public int Draft { get; } = draft;

    public int Translated { get; } = translated;

    public int Total { get; } = total;

    public IReadOnlyList<string> Files { get; } = files;

    public string Original(string key) => originals.GetValueOrDefault(key, string.Empty);

    public string Translation(string key) => translations.GetValueOrDefault(key, string.Empty);
}

internal sealed record FixStringLine(string Key, string Original, string Translation);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(FixStringLine))]
internal sealed partial class FixPromptJson : System.Text.Json.Serialization.JsonSerializerContext;

/// <summary>What the app does with fixes (<see cref="FixRunner"/>; fakes in tests).</summary>
public interface IFixRunner
{
    /// <inheritdoc cref="FixRunner.SendAsync"/>
    Task<FixTurn> SendAsync(GenerationJobEntry entry, FixRequest request, IProgress<FixProgress>? progress, CancellationToken cancellationToken);

    /// <inheritdoc cref="FixRunner.ApplyPlanAsync"/>
    Task<FixTurn> ApplyPlanAsync(GenerationJobEntry entry, int turnId, IProgress<FixProgress>? progress, CancellationToken cancellationToken);

    /// <inheritdoc cref="FixRunner.DropPlan"/>
    FixTurn DropPlan(GenerationJobEntry entry, int turnId);
}

/// <summary>
/// Fixes with AI (task 75, design §6e): the player's request about a completed job's translation goes to the job's
/// agent (same agent, quality's model and sandbox; for an unknown engine also its scripts workspace) with the
/// <see cref="PromptTemplate.SystemFix"/> and <see cref="PromptTemplate.Fix"/> prompts, on the job's working copy.
/// The job's strings, glossary and agent scripts are copied to <c>fix/before/</c> first. Afterwards: nothing changed →
/// the reply (maybe a question with choices); more than <see cref="FixRules.PlanThreshold"/> strings changed → the
/// change is moved to <c>fix/pending/</c> and the job put back, as a plan the player applies (<see cref="ApplyPlanAsync"/>)
/// or not; else the Build stage runs again and its local pack becomes the next draft (the current one kept in
/// <c>fix/drafts/&lt;n&gt;/</c>; <see cref="GoBack"/> makes an earlier one current again). A failed, stopped or crashed
/// (test run) fix puts the job and the pack back as they were. The job's lock is held throughout, so a fix never runs
/// beside another or beside a generation run. Every turn is saved in <c>fix/state.json</c> as it is made.
/// </summary>
public sealed class FixRunner(Game game, PipelineAgent agent, IGenerationStage build, LocalPackStore packs, TimeProvider time) : IFixRunner
{
    /// <summary>
    /// Sends the request; returns the agent's turn. Throws <see cref="FixException"/> when it can't start, and
    /// <see cref="OperationCanceledException"/> after recording a <see cref="FixTurnOutcome.Stopped"/> turn.
    /// </summary>
    public async Task<FixTurn> SendAsync(GenerationJobEntry entry, FixRequest request, IProgress<FixProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(request);
        var text = request.Text.Trim();
        if (text.Length == 0 || text.Length > FixRules.MaxRequestLength)
        {
            throw new FixException(FixProblem.BadRequest, "The request is empty or too long.");
        }

        using var held = Lock(entry);
        var fix = new FixFiles(entry.Folder);
        var context = new GenerationStepContext(entry, time);
        var pack = CurrentPack(entry);
        var state = fix.Read();
        var player = FixTurn.FromPlayer(state.NextTurnId, time.GetUtcNow(), text, request.Attachments);
        state = Save(fix, state.With(player));
        var turnId = state.NextTurnId;
        var steps = new List<string>();
        progress?.Report(new FixProgress(turnId, [], null));
        context.Log($"Fix {player.Id}: {Short(text)}");

        Capture(context.Files, fix.BeforeFolder);
        var scriptsBefore = Fingerprint(context.Files);
        SourceStrings? source;
        try
        {
            source = SourceStringsFile.Read(context.Files);
        }
        catch (GenerationFileException e)
        {
            return Finish(fix, state, Failed(turnId, steps, $"{e.Message}"));
        }

        if (source is null)
        {
            return Finish(fix, state, Failed(turnId, steps, "source.json is missing, so the strings can't be read."));
        }

        var scripts = source.Tool == AgentScriptTool.ToolId;
        var shared = SharedAttachments.Share(request.Attachments, scripts ? context.Files.AgentFolder : context.CopyFolder, context.Log);
        var values = PromptValues.Fix(
            game.Name,
            source.Engine,
            source.Strings.Count,
            pack.Report.DraftNumber,
            text,
            FixPrompt.Attachments(shared.Attachments, Context(pack, source, context.Files)),
            FixPrompt.History([.. state.Turns.Where(t => t.Id < player.Id)]),
            scripts);
        var system = new AgentSystemPrompt(PromptTemplate.SystemFix, PromptValues.SystemFix(game.Name, entry.Job.Language, scripts ? context.CopyFolder : null));
        var extras = new AgentRunExtras(system, e =>
        {
            if (e is AgentToolCall call)
            {
                lock (steps)
                {
                    FixSteps.Add(steps, FixSteps.Of(call.Name));
                    progress?.Report(new FixProgress(turnId, [.. steps], null));
                }
            }
        }, "fix");
        var tools = request.Attachments.Any(a => a.Kind == FixAttachmentKind.Screenshot) ? new HashSet<AgentBuiltinTool> { AgentBuiltinTool.ReadFiles } : null;

        PipelineAgentResult run;
        try
        {
            run = await agent.RunAsync(context, PromptTemplate.Fix, values, game.Name, cancellationToken,
                workspace: scripts ? new AgentWorkspace(context.Files.AgentFolder) : null, logTag: "Fix", extraTools: tools, extras: extras).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            PutBack(fix, context.Files);
            Finish(fix, state, new FixTurn(turnId, FixTurnKind.Agent, time.GetUtcNow(), string.Empty, [], [.. steps], [], FixTurnOutcome.Stopped));
            throw;
        }
        finally
        {
            shared.Remove(context.Log);
        }

        var model = PipelineProgressFile.Read(context.Files).Agent?.Model;
        if (run.Stop is GenerationStepResult.Failed failed)
        {
            PutBack(fix, context.Files);
            return Finish(fix, state, Failed(turnId, steps, failed.Message) with { Model = model });
        }

        if (run.Stop is GenerationStepResult.LimitReached limit)
        {
            PutBack(fix, context.Files);
            var reset = limit.ResetsAt is { } at ? $" It resets at {at.ToUniversalTime():u}." : string.Empty;
            return Finish(fix, state, Failed(turnId, steps, $"{agent.DisplayName}'s usage limit was reached.{reset}") with { Model = model });
        }

        var (reply, choices) = FixPrompt.SplitChoices(run.Reply);
        var diff = Diff(fix.BeforeFolder, context.Files, source);
        var scriptsChanged = Fingerprint(context.Files) != scriptsBefore;
        context.Log($"Fix {player.Id}: {diff.Strings.Count} strings and {diff.Terms} glossary entries changed" + (scriptsChanged ? ", scripts changed." : "."));
        if (diff.Strings.Count == 0 && diff.Terms == 0 && !scriptsChanged)
        {
            FolderCopy.Delete(fix.BeforeFolder);
            return Finish(fix, state, new FixTurn(turnId, FixTurnKind.Agent, time.GetUtcNow(), reply, [], [.. steps], choices, FixTurnOutcome.Answered, Model: model));
        }

        if (diff.Strings.Count > FixRules.PlanThreshold)
        {
            Capture(context.Files, fix.PendingFolder);
            PutBack(fix, context.Files);
            var plan = new FixPlan(diff.Strings.Count, diff.Terms, scriptsChanged, Samples(diff.Strings));
            return Finish(fix, state, new FixTurn(turnId, FixTurnKind.Agent, time.GetUtcNow(), reply, [], [.. steps], [], FixTurnOutcome.Planned, Plan: plan, Model: model));
        }

        var turn = new FixTurn(turnId, FixTurnKind.Agent, time.GetUtcNow(), reply, [], [.. steps], choices, Model: model);
        return await BuildAsync(entry, fix, state, turn, diff, scriptsChanged, steps, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies the plan of <paramref name="turnId"/>: its held changes go into the job and the next draft is built (no agent run).</summary>
    public async Task<FixTurn> ApplyPlanAsync(GenerationJobEntry entry, int turnId, IProgress<FixProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using var held = Lock(entry);
        var fix = new FixFiles(entry.Folder);
        var state = fix.Read();
        if (state.WaitingPlan is not { Id: var id, Plan: { } plan } turn || id != turnId || !Directory.Exists(fix.PendingFolder))
        {
            throw new FixException(FixProblem.NotFound, "No plan waits for the player.");
        }

        CurrentPack(entry);
        var files = new GenerationFiles(entry.Folder);
        SourceStrings source;
        try
        {
            source = SourceStringsFile.Read(files) ?? throw new GenerationFileException(files.SourcePath, "missing");
        }
        catch (GenerationFileException e)
        {
            throw new FixException(FixProblem.Disk, e.Message);
        }

        Capture(files, fix.BeforeFolder);
        Restore(fix.PendingFolder, files);
        FolderCopy.Delete(fix.PendingFolder);
        var diff = Diff(fix.BeforeFolder, files, source);
        var steps = new List<string>(turn.Steps);
        var applied = turn with { Plan = plan with { State = FixPlanState.Applied }, Outcome = FixTurnOutcome.Planned };
        state = Save(fix, state.With(applied));
        var built = await BuildAsync(entry, fix, state, applied, diff, plan.Scripts, steps, progress, cancellationToken, keepPlan: true).ConfigureAwait(false);
        return built;
    }

    /// <summary>"Change the request": the plan of <paramref name="turnId"/> is dropped and its held changes deleted.</summary>
    public FixTurn DropPlan(GenerationJobEntry entry, int turnId)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using var held = Lock(entry);
        var fix = new FixFiles(entry.Folder);
        var state = fix.Read();
        if (state.WaitingPlan is not { Plan: { } plan } turn || turn.Id != turnId)
        {
            throw new FixException(FixProblem.NotFound, "No plan waits for the player.");
        }

        FolderCopy.Delete(fix.PendingFolder);
        var dropped = turn with { Plan = plan with { State = FixPlanState.Dropped } };
        Save(fix, state.With(dropped));
        return dropped;
    }

    /// <summary>
    /// Makes draft <paramref name="number"/> current again: the current one is kept in <c>fix/drafts/</c>, the chosen
    /// one's pack and job state put back (its report dated now, so the installed pack reads as another draft).
    /// </summary>
    public static LocalPack GoBack(GenerationJobEntry entry, int number, LocalPackStore packs, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(time);
        using var held = Lock(entry);
        var fix = new FixFiles(entry.Folder);
        var state = fix.Read();
        var pack = CurrentPack(packs, entry);
        if (number == pack.Report.DraftNumber || state.Drafts.All(d => d.Number != number) || !Directory.Exists(fix.DraftPackFolder(number)))
        {
            throw new FixException(FixProblem.NotFound, $"Draft {number} isn't kept.");
        }

        var files = new GenerationFiles(entry.Folder);
        var current = pack.Report.DraftNumber;
        try
        {
            KeepCurrent(packs, fix, files, pack, current, fromJob: true);
            var restored = packs.Restore(fix.DraftPackFolder(number), r => r with { Created = time.GetUtcNow(), Draft = number > 1 ? number : null, Published = pack.Report.Published })
                ?? throw new IOException($"Draft {number} can't be read.");
            Restore(fix.DraftJobFolder(number), files);
            Save(fix, state with { Current = number });
            new GenerationStepContext(entry, time).Log($"Fix: went back to Draft {number} (Draft {current} kept).");
            return restored;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new FixException(FixProblem.Disk, e.Message);
        }
    }

    /// <summary>
    /// After a crash or a kill during a fix (<c>fix/before/</c> left, the lock free): the job is put back and the
    /// request gets a failed turn. Returns whether there was anything to recover. Never throws for disk state.
    /// </summary>
    public static bool Recover(GenerationJobEntry entry, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(time);
        var fix = new FixFiles(entry.Folder);
        if (!Directory.Exists(fix.BeforeFolder))
        {
            return false;
        }

        using var held = GenerationJobLock.TryAcquire(entry.Folder);
        if (held is null)
        {
            return false;
        }

        try
        {
            FixJobState.Restore(fix.BeforeFolder, new GenerationFiles(entry.Folder));
            FolderCopy.Delete(fix.BeforeFolder);
            var state = fix.Read();
            if (state.Turns.LastOrDefault() is { Kind: FixTurnKind.Player })
            {
                fix.Write(state.With(new FixTurn(state.NextTurnId, FixTurnKind.Agent, time.GetUtcNow(), string.Empty, [], [], [], FixTurnOutcome.Failed,
                    Problem: "The app closed while the agent worked; nothing changed. Send the request again.")));
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<FixTurn> BuildAsync(
        GenerationJobEntry entry,
        FixFiles fix,
        FixState state,
        FixTurn turn,
        StringDiff diff,
        bool scriptsChanged,
        List<string> steps,
        IProgress<FixProgress>? progress,
        CancellationToken cancellationToken,
        bool keepPlan = false)
    {
        var context = new GenerationStepContext(entry, time);
        var pack = CurrentPack(entry);
        var current = pack.Report.DraftNumber;
        FixSteps.Add(steps, FixSteps.Build);
        progress?.Report(new FixProgress(turn.Id, [.. steps], null));
        GenerationStepResult result;
        try
        {
            KeepCurrent(packs, fix, context.Files, pack, current, fromJob: false);
            result = await build.RunStepAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            PutBack(fix, context.Files, pack, current);
            Finish(fix, state, turn with { Steps = [.. steps], Outcome = keepPlan ? turn.Outcome : FixTurnOutcome.Stopped, Problem = keepPlan ? "Stopped while building; nothing changed." : null });
            throw;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            PutBack(fix, context.Files, pack, current);
            return Finish(fix, state, Failed(turn.Id, steps, $"The draft can't be kept: {e.Message}") with { Text = turn.Text, Model = turn.Model, Plan = turn.Plan });
        }

        if (result is not GenerationStepResult.StageDone)
        {
            var message = result switch
            {
                GenerationStepResult.Failed f => f.Message,
                GenerationStepResult.LimitReached => $"{agent.DisplayName}'s usage limit was reached while rebuilding.",
                _ => "The build stopped.",
            };
            PutBack(fix, context.Files, pack, current);
            return Finish(fix, state, turn with { Steps = [.. steps], Outcome = FixTurnOutcome.Failed, Problem = message });
        }

        var number = state.NextDraft;
        var made = packs.FindByJob(game.Id, entry.Job.Id);
        if (made is null)
        {
            PutBack(fix, context.Files, pack, current);
            return Finish(fix, state, turn with { Steps = [.. steps], Outcome = FixTurnOutcome.Failed, Problem = "The build saved no pack." });
        }

        made = packs.UpdateReport(made.Manifest.Game, made.Manifest.Language, made.Manifest.Id, r => r with
        {
            Draft = number,
            Published = pack.Report.Published,
            ShareDismissed = pack.Report.ShareDismissed,
        }) ?? made;
        FolderCopy.Delete(fix.BeforeFolder);
        FixSteps.Add(steps, FixSteps.Saved);
        var drafts = state.Drafts.Count == 0 ? new List<FixDraft> { new(1, pack.Report.Created) } : [.. state.Drafts];
        if (drafts.All(d => d.Number != current))
        {
            drafts.Add(new FixDraft(current, pack.Report.Created));
        }

        drafts.Add(new FixDraft(number, made.Report.Created, turn.Id));
        var change = new FixChange(number, diff.Strings.Count, diff.Terms, scriptsChanged, made.Report.TestRun.Outcome, Samples(diff.Strings));
        context.Log($"Fix {turn.Id}: Draft {number} built (test run {made.Report.TestRun.Outcome.ToString().ToLowerInvariant()}); Draft {current} kept.");
        var done = turn with { Steps = [.. steps], Outcome = keepPlan ? FixTurnOutcome.Planned : FixTurnOutcome.Changed, Change = change };
        return Finish(fix, state with { Drafts = [.. drafts.OrderBy(d => d.Number)], Current = number }, done);
    }

    /// <summary>Keeps the current draft in <c>fix/drafts/&lt;n&gt;/</c>: its pack, and the job state (from <c>fix/before/</c> while a fix changed the job).</summary>
    private static void KeepCurrent(LocalPackStore packs, FixFiles fix, GenerationFiles files, LocalPack pack, int current, bool fromJob)
    {
        if (!packs.Snapshot(pack.Manifest.Game, pack.Manifest.Language, pack.Manifest.Id, fix.DraftPackFolder(current)))
        {
            throw new IOException("The current draft's pack is gone.");
        }

        if (fromJob)
        {
            FixJobState.Capture(files, fix.DraftJobFolder(current));
        }
        else
        {
            FolderCopy.Replace(fix.BeforeFolder, fix.DraftJobFolder(current));
        }
    }

    /// <summary>Puts the job (and, after a build, the current draft's pack) back as before the fix.</summary>
    private void PutBack(FixFiles fix, GenerationFiles files, LocalPack? pack = null, int current = 0)
    {
        try
        {
            if (Directory.Exists(fix.BeforeFolder))
            {
                FixJobState.Restore(fix.BeforeFolder, files);
                FolderCopy.Delete(fix.BeforeFolder);
            }

            if (pack is not null && Directory.Exists(fix.DraftPackFolder(current)))
            {
                packs.Restore(fix.DraftPackFolder(current), r => r with { Tried = pack.Report.Tried, Crash = pack.Report.Crash, Published = pack.Report.Published, ShareDismissed = pack.Report.ShareDismissed });
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new FixException(FixProblem.Disk, $"The job can't be put back as it was: {e.Message}");
        }
    }

    private LocalPack CurrentPack(GenerationJobEntry entry) => CurrentPack(packs, entry);

    private static LocalPack CurrentPack(LocalPackStore packs, GenerationJobEntry entry) =>
        packs.FindByJob(entry.Job.Game, entry.Job.Id) is { } pack && pack.Manifest.Language == entry.Job.Language
            ? pack
            : throw new FixException(FixProblem.NoPack, "The job's translation is gone from the local packs.");

    private static GenerationJobLock Lock(GenerationJobEntry entry) =>
        GenerationJobLock.TryAcquire(entry.Folder) ?? throw new FixException(FixProblem.Busy, "The job is busy: a fix or a generation run works on it.");

    private static void Capture(GenerationFiles files, string folder)
    {
        try
        {
            FixJobState.Capture(files, folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new FixException(FixProblem.Disk, $"The job's state can't be copied aside: {e.Message}");
        }
    }

    private static void Restore(string folder, GenerationFiles files)
    {
        try
        {
            FixJobState.Restore(folder, files);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new FixException(FixProblem.Disk, $"The job's state can't be put back: {e.Message}");
        }
    }

    private static string? Fingerprint(GenerationFiles files)
    {
        try
        {
            return FixJobState.ScriptsFingerprint(files);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static FixState Save(FixFiles fix, FixState state)
    {
        try
        {
            fix.Write(state);
            return state;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new FixException(FixProblem.Disk, $"The conversation can't be saved: {e.Message}");
        }
    }

    private static FixTurn Finish(FixFiles fix, FixState state, FixTurn turn)
    {
        Save(fix, state.With(turn));
        return turn;
    }

    private FixTurn Failed(int id, List<string> steps, string problem) =>
        new(id, FixTurnKind.Agent, time.GetUtcNow(), string.Empty, [], [.. steps], [], FixTurnOutcome.Failed, Problem: problem);

    private static FixContext Context(LocalPack pack, SourceStrings source, GenerationFiles files)
    {
        StringTable? table;
        try
        {
            table = files.ReadStrings();
        }
        catch (GenerationFileException)
        {
            table = null;
        }

        var originals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in source.Strings)
        {
            originals.TryAdd(s.Key, s.Original);
        }

        var translations = table?.Strings ?? new Dictionary<string, string>();
        return new FixContext(pack.Report.DraftNumber, pack.Manifest.Coverage?.Translated ?? translations.Count, source.Strings.Count, source.Files, originals, translations);
    }

    private sealed record StringDiff(IReadOnlyList<FixStringChange> Strings, int Terms);

    /// <summary>Translations and glossary entries that differ between the captured state and the job now.</summary>
    private static StringDiff Diff(string before, GenerationFiles files, SourceStrings source)
    {
        StringTable? was, now;
        Glossary? oldGlossary, newGlossary;
        try
        {
            was = FixJobState.ReadStrings(before);
            now = files.ReadStrings();
            oldGlossary = FixJobState.ReadGlossary(before);
            newGlossary = files.ReadGlossary();
        }
        catch (GenerationFileException e)
        {
            throw new FixException(FixProblem.Disk, e.Message);
        }

        var a = was?.Strings ?? new Dictionary<string, string>();
        var b = now?.Strings ?? new Dictionary<string, string>();
        var originals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in source.Strings)
        {
            originals.TryAdd(s.Key, s.Original);
        }

        var changed = a.Keys.Union(b.Keys, StringComparer.Ordinal)
            .Where(k => !string.Equals(a.GetValueOrDefault(k), b.GetValueOrDefault(k), StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => new FixStringChange(k, originals.GetValueOrDefault(k, string.Empty), a.GetValueOrDefault(k, string.Empty), b.GetValueOrDefault(k, string.Empty)))
            .ToList();
        return new StringDiff(changed, GlossaryChanges(oldGlossary, newGlossary));
    }

    internal static int GlossaryChanges(Glossary? before, Glossary? after)
    {
        var a = (before?.Entries ?? []).GroupBy(e => e.Term, Glossary.TermComparer).ToDictionary(g => g.Key, g => g.First(), Glossary.TermComparer);
        var b = (after?.Entries ?? []).GroupBy(e => e.Term, Glossary.TermComparer).ToDictionary(g => g.Key, g => g.First(), Glossary.TermComparer);
        var entries = a.Keys.Union(b.Keys, Glossary.TermComparer).Count(k => !Equals(a.GetValueOrDefault(k) is { } x ? x with { Edited = false } : null, b.GetValueOrDefault(k) is { } y ? y with { Edited = false } : null));
        var tone = before?.Tone != after?.Tone || before?.FormOfAddress != after?.FormOfAddress ? 1 : 0;
        return entries + tone;
    }

    private static List<FixStringChange> Samples(IReadOnlyList<FixStringChange> changes) =>
        [.. changes.Where(c => c.After.Length > 0).Concat(changes.Where(c => c.After.Length == 0)).Take(FixRules.Samples)];

    private static string Short(string text)
    {
        var flat = text.ReplaceLineEndings(" ");
        return flat.Length <= 200 ? flat : flat[..200] + "…";
    }
}

/// <summary>
/// Screenshots copied into the agent's working folder for one fix run (<c>.fix-attachments/</c>: the job's <c>agent/</c>
/// workspace, else the working copy): the agent's file tools reach only that folder (Claude Code's <c>--restricted</c>),
/// so a path in <c>fix/attachments/</c> can't be read. Removed when the run ends, before the rebuild and the test run.
/// </summary>
internal sealed class SharedAttachments
{
    public const string FolderName = ".fix-attachments";

    private readonly string? _folder;

    private SharedAttachments(IReadOnlyList<FixAttachment> attachments, string? folder)
    {
        Attachments = attachments;
        _folder = folder;
    }

    /// <summary>The attachments with each screenshot's path in the working folder (one that can't be copied keeps its own).</summary>
    public IReadOnlyList<FixAttachment> Attachments { get; }

    public static SharedAttachments Share(IReadOnlyList<FixAttachment> attachments, string workingFolder, Action<string> log)
    {
        if (!attachments.Any(a => a.Kind == FixAttachmentKind.Screenshot && a.Path is not null))
        {
            return new(attachments, null);
        }

        var folder = Path.Combine(workingFolder, FolderName);
        var shared = new List<FixAttachment>();
        foreach (var (a, i) in attachments.Select((a, i) => (a, i)))
        {
            if (a.Kind != FixAttachmentKind.Screenshot || a.Path is not { } path)
            {
                shared.Add(a);
                continue;
            }

            try
            {
                Directory.CreateDirectory(folder);
                var copy = Path.Combine(folder, $"{i + 1}-{Path.GetFileName(path)}");
                File.Copy(path, copy, overwrite: true);
                shared.Add(a with { Path = copy });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log($"Fix: the screenshot {path} can't be copied for the agent: {e.Message}");
                shared.Add(a);
            }
        }

        return new(shared, folder);
    }

    public void Remove(Action<string> log)
    {
        if (_folder is null || !Directory.Exists(_folder))
        {
            return;
        }

        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"Fix: {_folder} can't be removed: {e.Message}");
        }
    }
}
