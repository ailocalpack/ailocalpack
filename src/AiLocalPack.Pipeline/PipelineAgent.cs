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

    public string Id => adapter.Id;

    public string DisplayName => adapter.DisplayName;

    public async Task<PipelineAgentResult> RunAsync(
        GenerationStepContext context,
        PromptTemplate prompt,
        IReadOnlyDictionary<string, string> values,
        string gameName,
        CancellationToken cancellationToken,
        AgentWorkspace? workspace = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(values);
        var task = prompt.Render(values);
        var systemTemplate = workspace is null ? PromptTemplate.System : PromptTemplate.SystemScripts;
        var system = workspace is null
            ? PromptTemplate.System.Render(PromptValues.System(gameName, context.Job.Language))
            : PromptTemplate.SystemScripts.Render(PromptValues.SystemScripts(gameName, context.Job.Language, context.CopyFolder));
        if (await adapter.DetectAsync(cancellationToken).ConfigureAwait(false) is not { } installation)
        {
            return Stop(context, new GenerationStepResult.Failed(
                GenerationFailureKind.AgentMissing, $"{adapter.DisplayName} isn't installed: install it in Settings → AI agents."));
        }

        var auth = await adapter.GetAuthStatusAsync(installation, cancellationToken).ConfigureAwait(false);
        if (auth.State == AgentAuthState.SignedOut)
        {
            return Stop(context, new GenerationStepResult.Failed(
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
                return Stop(context, new GenerationStepResult.Failed(GenerationFailureKind.Disk, $"The agent's workspace {workspace.Folder} can't be made: {e.Message}"));
            }
        }

        var request = new AgentRunRequest(task, workspace?.Folder ?? context.CopyFolder)
        {
            SystemPrompt = system,
            BuiltinTools = workspace is null ? new HashSet<AgentBuiltinTool>() : AgentWorkspace.Tools,
            McpServers = [server.For(context.CopyFolder, context.Folder, context.Job.Language)],
            Model = context.Job.Options.Model,
        };
        context.Log($"Agent {adapter.Id} {installation.Version ?? "(version unknown)"} ({installation.Source}), prompt {prompt.Label} + {systemTemplate.Label}"
            + (workspace is null ? "" : $", in its workspace {workspace.Folder} with file and command tools")
            + (request.Model is { } model ? $", model {model}." : "."));
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

                    Record(context, transcript, e);
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

    private static PipelineAgentResult Stop(GenerationStepContext context, GenerationStepResult.Failed failed)
    {
        context.Log(failed.Message);
        return new PipelineAgentResult(failed, null);
    }

    private static string Transcript(GenerationStepContext context)
    {
        var logs = Path.Combine(context.Folder, "logs");
        Directory.CreateDirectory(logs);
        return Path.Combine(logs, $"agent-{context.Job.Stage.ToString().ToLowerInvariant()}.jsonl");
    }

    private static void Record(GenerationStepContext context, string transcript, AgentEvent e)
    {
        try
        {
            File.AppendAllText(transcript, AgentEventJson.Write(e) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort, like the job log.
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
            context.Log(line);
        }
    }

    private static string Short(string text)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= LogTextLength ? flat : flat[..LogTextLength] + "…";
    }
}
