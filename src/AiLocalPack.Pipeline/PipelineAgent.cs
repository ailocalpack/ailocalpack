using System.Globalization;
using AiLocalPack.Agents;
using AiLocalPack.Core.Generation;

namespace AiLocalPack.Pipeline;

/// <summary>How a stage's agent run ended.</summary>
/// <param name="Stop">The step's result when the run didn't succeed (missing, signed out, limit, failed); <c>null</c> = succeeded.</param>
/// <param name="Reply">The agent's final answer, when it gave one.</param>
public sealed record PipelineAgentResult(GenerationStepResult? Stop, string? Reply)
{
    public bool Succeeded => Stop is null;
}

/// <summary>
/// A run in the agent's own workspace (task 57, a game the toolkit can't read): the job's <c>agent/</c> folder as the
/// working folder, reading files, editing them and running commands there (each adapter's sandbox keeps writes inside
/// it and has no network), the <see cref="PromptTemplate.SystemScripts"/> system prompt; the toolkit server still reads
/// the working copy.
/// </summary>
public sealed record AgentWorkspace(string Folder)
{
    public static IReadOnlySet<AgentBuiltinTool> Tools { get; } =
        new HashSet<AgentBuiltinTool> { AgentBuiltinTool.ReadFiles, AgentBuiltinTool.EditFiles, AgentBuiltinTool.RunCommands };
}

/// <summary>
/// Runs the job's agent for a stage: finds the program and checks its sign-in first (a signed-out Codex retries for half
/// a minute before saying so), then runs it headless in the working copy with the toolkit server as its only tools
/// (no built-in file, edit or shell tools), the <see cref="PromptTemplate.System"/> prompt appended and the job's model.
/// Every event goes to <c>logs/agent-&lt;stage&gt;.jsonl</c> (<see cref="AgentEventJson"/>) and a readable line of each
/// message, tool call, tool error and the usage to the job log; the usage is also summed into <c>progress.json</c>
/// (<see cref="PipelineProgressFile"/>). A cancelled run throws <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class PipelineAgent(IAgentAdapter adapter, ToolkitServerProgram server)
{
    private const int LogTextLength = 400;

    // Translate chunks running side by side append to the same transcript.
    private static readonly Lock TranscriptGate = new();

    public string Id => adapter.Id;

    public string DisplayName => adapter.DisplayName;

    public async Task<PipelineAgentResult> RunAsync(
        GenerationStepContext context,
        PromptTemplate prompt,
        IReadOnlyDictionary<string, string> values,
        string gameName,
        CancellationToken cancellationToken,
        AgentWorkspace? workspace = null,
        string? logTag = null,
        IReadOnlySet<AgentBuiltinTool>? extraTools = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(values);
        var task = prompt.Render(values);
        var systemTemplate = workspace is null ? PromptTemplate.System : PromptTemplate.SystemScripts;
        var system = workspace is null
            ? PromptTemplate.System.Render(PromptValues.System(gameName, context.Job.Language))
            : PromptTemplate.SystemScripts.Render(PromptValues.SystemScripts(gameName, context.Job.Language, context.CopyFolder));
        string? brief;
        try
        {
            brief = BriefFor(context);
        }
        catch (GenerationFileException e)
        {
            return Stop(context, logTag, new GenerationStepResult.Failed(GenerationFailureKind.Disk, $"{e.Message}; run the Study stage again."));
        }

        if (brief is not null)
        {
            system += PromptTemplate.SystemBrief.Render(PromptValues.SystemBrief(brief));
        }

        var choice = ModelFor(context.Job, Id, workspace is not null);
        if (await adapter.DetectAsync(cancellationToken).ConfigureAwait(false) is not { } installation)
        {
            return Stop(context, logTag, new GenerationStepResult.Failed(
                GenerationFailureKind.AgentMissing, $"{adapter.DisplayName} isn't installed: install it in Settings → AI agents."));
        }

        var auth = await adapter.GetAuthStatusAsync(installation, cancellationToken).ConfigureAwait(false);
        if (auth.State == AgentAuthState.SignedOut)
        {
            return Stop(context, logTag, new GenerationStepResult.Failed(
                GenerationFailureKind.SignedOut, $"{adapter.DisplayName} isn't signed in: sign in in Settings → AI agents."));
        }

        if (workspace is not null)
        {
            try
            {
                Directory.CreateDirectory(workspace.Folder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Stop(context, logTag, new GenerationStepResult.Failed(GenerationFailureKind.Disk, $"The agent's workspace {workspace.Folder} can't be made: {e.Message}"));
            }
        }

        var request = new AgentRunRequest(task, workspace?.Folder ?? context.CopyFolder)
        {
            SystemPrompt = system,
            BuiltinTools = Tools(workspace, extraTools),
            McpServers = [server.For(context.CopyFolder, context.Folder, context.Job.Language)],
            Model = choice.Model,
            Effort = choice.Effort,
        };
        Log(context, logTag, $"Agent {adapter.Id} {installation.Version ?? "(version unknown)"} ({installation.Source}), prompt {prompt.Label} + {systemTemplate.Label}"
            + (brief is null ? "" : $" + {PromptTemplate.SystemBrief.Label}")
            + (workspace is null ? "" : $", in its workspace {workspace.Folder} with file and command tools")
            + (extraTools is { Count: > 0 } ? $", with {string.Join(", ", extraTools.Order())}" : "")
            + $", quality {context.Job.Options.Quality.ToString().ToLowerInvariant()}"
            + (request.Model is { } model ? $", model {model}" : "")
            + (request.Effort is { } effort ? $", effort {effort.ToString().ToLowerInvariant()}." : "."));
        var transcript = Transcript(context);
        var usage = new List<AgentUsage>();
        var seenModel = request.Model;
        try
        {
            var outcome = await AgentStepRun.RunAsync(
                adapter,
                installation,
                request,
                e =>
                {
                    if (e is AgentUsage u)
                    {
                        usage.Add(u);
                    }
                    else if (e is AgentSessionStarted { Model: { } started })
                    {
                        seenModel = started;
                    }
                    else if (e is AgentLimitStatus status)
                    {
                        // Shown while the run goes on (the progress card's limit box), so written at once.
                        PipelineProgressFile.Update(context.Files, p => p with { Limit = new AgentLimitInfo(status.Used, status.Window, status.ResetsAt) });
                    }

                    Record(context, logTag, transcript, e);
                },
                cancellationToken).ConfigureAwait(false);
            return new PipelineAgentResult(outcome.Stop, outcome.Done.Result);
        }
        finally
        {
            PipelineProgressFile.Update(context.Files, p => p with
            {
                Usage = p.Usage.Add(usage),
                Agent = new AgentRunInfo(installation.Version ?? p.Agent?.Version, seenModel ?? p.Agent?.Model),
            });
        }
    }

    /// <summary>
    /// The model of a run: the job's <see cref="GenerationOptions.Model"/> when the player named one (CLI <c>--model</c>),
    /// else <see cref="AgentModels"/> for the job's quality; the agent's own scripts (unknown engines) always run with the
    /// Balanced choice.
    /// </summary>
    public static AgentModelChoice ModelFor(GenerationJob job, string agentId, bool scripts)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Options.Model is { } model)
        {
            return new AgentModelChoice(model, null);
        }

        return scripts ? AgentModels.ForExtraction(agentId) : AgentModels.For(agentId, job.Options.Quality);
    }

    /// <summary>The game brief a run gets: a Thorough job's Glossary and Translate runs, once Study wrote it.</summary>
    private static string? BriefFor(GenerationStepContext context) =>
        context.Job.Options.Studies && context.Job.Stage is GenerationStage.Glossary or GenerationStage.Translate
            ? context.Files.ReadBrief()
            : null;

    private static HashSet<AgentBuiltinTool> Tools(AgentWorkspace? workspace, IReadOnlySet<AgentBuiltinTool>? extra)
    {
        var tools = workspace is null ? new HashSet<AgentBuiltinTool>() : new HashSet<AgentBuiltinTool>(AgentWorkspace.Tools);
        if (extra is not null)
        {
            tools.UnionWith(extra);
        }

        return tools;
    }

    private static PipelineAgentResult Stop(GenerationStepContext context, string? logTag, GenerationStepResult.Failed failed)
    {
        Log(context, logTag, failed.Message);
        return new PipelineAgentResult(failed, null);
    }

    private static string Transcript(GenerationStepContext context)
    {
        var logs = Path.Combine(context.Folder, "logs");
        Directory.CreateDirectory(logs);
        return Path.Combine(logs, $"agent-{context.Job.Stage.ToString().ToLowerInvariant()}.jsonl");
    }

    /// <summary>A job log line, after <c>logTag: </c> when runs go side by side (Translate chunks).</summary>
    private static void Log(GenerationStepContext context, string? logTag, string line) =>
        context.Log(logTag is null ? line : $"{logTag}: {line}");

    private static void Record(GenerationStepContext context, string? logTag, string transcript, AgentEvent e)
    {
        lock (TranscriptGate)
        {
            try
            {
                File.AppendAllText(transcript, AgentEventJson.Write(e) + "\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort, like the job log.
            }
        }

        var line = e switch
        {
            AgentSessionStarted s => $"Agent session {s.SessionId}" + (s.Model is { } m ? $" ({m})." : "."),
            AgentMessage m => $"Agent: {Short(m.Text)}",
            AgentToolCall c => $"Tool call: {c.Name}",
            AgentToolResult { IsError: true } r => $"Tool error: {Short(r.Output)}",
            AgentUsage u => string.Create(CultureInfo.InvariantCulture, $"Usage: {u.InputTokens} input, {u.OutputTokens} output, {u.CacheReadTokens} cache read, {u.CacheWriteTokens} cache write tokens")
                + (u.CostUsd is { } cost ? string.Create(CultureInfo.InvariantCulture, $", ${cost:0.####}.") : "."),
            AgentLimitReached l => $"Agent limit reached" + (l.ResetsAt is { } at ? $", resets at {at:u}." : "."),
            AgentLimitStatus st => string.Create(CultureInfo.InvariantCulture, $"Agent usage: {st.Used:P0} of the {st.Window ?? "current"} window used."),
            AgentError error => $"Agent error ({error.Kind}): {Short(error.Message)}",
            AgentDone d => $"Agent run ended: {d.Outcome}" + (d.Message is { } message ? $" ({Short(message)})." : "."),
            _ => null,
        };
        if (line is not null)
        {
            Log(context, logTag, line);
        }
    }

    private static string Short(string text)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= LogTextLength ? flat : flat[..LogTextLength] + "…";
    }
}
