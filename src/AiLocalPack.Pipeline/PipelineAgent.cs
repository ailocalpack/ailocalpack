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
/// Runs the job's agent for a stage: finds the program and checks its sign-in first (a signed-out Codex retries for half
/// a minute before saying so), then runs it headless in the working copy with the toolkit server as its only tools
/// (no built-in file, edit or shell tools), the <see cref="PromptTemplate.System"/> prompt appended and the job's model.
/// Every event goes to <c>logs/agent-&lt;stage&gt;.jsonl</c> (<see cref="AgentEventJson"/>) and a readable line of each
/// message, tool call, tool error and the usage to the job log. A cancelled run throws <see cref="OperationCanceledException"/>.
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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(values);
        var task = prompt.Render(values);
        var system = PromptTemplate.System.Render(PromptValues.System(gameName, context.Job.Language));
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

        var request = new AgentRunRequest(task, context.CopyFolder)
        {
            SystemPrompt = system,
            McpServers = [server.For(context.CopyFolder, context.Folder, context.Job.Language)],
            Model = context.Job.Options.Model,
        };
        context.Log($"Agent {adapter.Id} {installation.Version ?? "(version unknown)"} ({installation.Source}), prompt {prompt.Label} + {PromptTemplate.System.Label}"
            + (request.Model is { } model ? $", model {model}." : "."));
        var transcript = Transcript(context);
        var outcome = await AgentStepRun.RunAsync(adapter, installation, request, e => Record(context, transcript, e), cancellationToken).ConfigureAwait(false);
        return new PipelineAgentResult(outcome.Stop, outcome.Done.Result);
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
