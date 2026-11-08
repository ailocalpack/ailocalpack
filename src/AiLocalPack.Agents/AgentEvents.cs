namespace AiLocalPack.Agents;

/// <summary>
/// One normalized event of an agent run (task 40), the same for every agent. A run's stream (see
/// <see cref="IAgentAdapter.RunAsync"/>) always ends with exactly one <see cref="AgentDone"/>.
/// </summary>
public abstract record AgentEvent;

/// <summary>The agent started its session (Claude Code's <c>init</c>, Codex's thread start).</summary>
/// <param name="SessionId">The agent's session ID (resumable with <see cref="AgentRunRequest.ResumeSessionId"/>), when it reports one.</param>
/// <param name="Model">The model the agent runs, when it reports it.</param>
public sealed record AgentSessionStarted(string? SessionId, string? Model) : AgentEvent;

/// <summary>Text the agent wrote.</summary>
public sealed record AgentMessage(string Text) : AgentEvent;

/// <summary>The agent called a tool.</summary>
/// <param name="Id">The call's ID, named again by its <see cref="AgentToolResult"/>.</param>
/// <param name="Name">The tool (an MCP tool as the agent names it, or a built-in one).</param>
/// <param name="Input">Its arguments as JSON text (<c>{}</c> when none).</param>
public sealed record AgentToolCall(string Id, string Name, string Input) : AgentEvent;

/// <summary>What a tool call returned.</summary>
/// <param name="CallId">The <see cref="AgentToolCall.Id"/> it answers.</param>
/// <param name="Output">The result text, possibly shortened by the agent.</param>
/// <param name="IsError"><c>true</c> when the tool failed.</param>
public sealed record AgentToolResult(string CallId, string Output, bool IsError) : AgentEvent;

/// <summary>Tokens (and cost, when the agent reports it) used so far by a turn or by the whole run.</summary>
public sealed record AgentUsage(long InputTokens, long OutputTokens, long CacheReadTokens = 0, long CacheWriteTokens = 0, decimal? CostUsd = null)
    : AgentEvent;

/// <summary>The player's subscription or API limit ran out; the run ends with <see cref="AgentOutcome.LimitReached"/>.</summary>
/// <param name="ResetsAt">When the limit resets, when the agent says so (the job resumes then, task 47).</param>
/// <param name="Message">The agent's own words.</param>
public sealed record AgentLimitReached(DateTimeOffset? ResetsAt, string Message) : AgentEvent;

/// <summary>Something went wrong; whether the run goes on is up to <see cref="AgentDone"/>.</summary>
public sealed record AgentError(AgentErrorKind Kind, string Message) : AgentEvent;

/// <summary>A line the agent wrote to its standard error (logs, warnings), kept for the job log.</summary>
public sealed record AgentDiagnostic(string Text) : AgentEvent;

/// <summary>The run ended. Always the last event, exactly once.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Result">The agent's final answer, when it gave one.</param>
/// <param name="Message">Why it failed (an exit code, the end of its standard error…), else <c>null</c>.</param>
/// <param name="ExitCode">The process's exit code; <c>null</c> when it never started.</param>
public sealed record AgentDone(AgentOutcome Outcome, string? Result = null, string? Message = null, int? ExitCode = null) : AgentEvent;

public enum AgentErrorKind
{
    /// <summary>An output line that isn't the agent's format (or too long); the run goes on.</summary>
    Malformed,

    /// <summary>The agent isn't signed in (or its key was refused).</summary>
    SignedOut,

    /// <summary>The agent's program couldn't be started.</summary>
    StartFailed,

    /// <summary>An error the agent itself reported (API error, refused tool…).</summary>
    Reported,
}

public enum AgentOutcome
{
    Succeeded,
    Failed,

    /// <summary>The subscription / API limit ran out (see <see cref="AgentLimitReached"/>).</summary>
    LimitReached,

    /// <summary>The agent needs the player to sign in again.</summary>
    SignedOut,

    /// <summary>The run was cancelled and its process tree killed.</summary>
    Cancelled,
}
