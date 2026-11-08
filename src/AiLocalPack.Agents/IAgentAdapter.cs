namespace AiLocalPack.Agents;

/// <summary>
/// One external AI agent (Claude Code, Codex; task 40): finds it, tells whether it is signed in and runs it headless with a
/// stream of normalized <see cref="AgentEvent"/>s. Every adapter keeps the same contract (see <see cref="RunAsync"/>), so
/// the generation pipeline never looks at an agent's own format.
/// </summary>
public interface IAgentAdapter
{
    /// <summary>Stable ID (<c>claude</c>, <c>codex</c>, <c>fake</c>), stored in jobs and manifests.</summary>
    string Id { get; }

    /// <summary>The agent's product name, as the player knows it.</summary>
    string DisplayName { get; }

    /// <summary>The agent's program and version, or <c>null</c> when it isn't installed. Never throws but for cancellation.</summary>
    Task<AgentInstallation?> DetectAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the agent is signed in, asked without the app ever reading a token. Never throws but for cancellation.</summary>
    Task<AgentAuthStatus> GetAuthStatusAsync(AgentInstallation installation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="request"/>. The stream never throws (not even for cancellation) and ends with exactly one
    /// <see cref="AgentDone"/>: cancelling kills the agent's process tree and ends with <see cref="AgentOutcome.Cancelled"/>;
    /// a reported limit → <see cref="AgentLimitReached"/> then <see cref="AgentOutcome.LimitReached"/>; not signed in →
    /// <see cref="AgentError"/> (<see cref="AgentErrorKind.SignedOut"/>) then <see cref="AgentOutcome.SignedOut"/>; a line
    /// that can't be read → <see cref="AgentErrorKind.Malformed"/> and the run goes on; a process that ends without a
    /// result → <see cref="AgentOutcome.Failed"/> with its exit code and the end of its standard error. Stopping the
    /// enumeration early kills the process tree too.
    /// </summary>
    IAsyncEnumerable<AgentEvent> RunAsync(AgentInstallation installation, AgentRunRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Where an agent's program comes from.</summary>
public enum AgentSource
{
    /// <summary>Found on <c>PATH</c> (installed by the player).</summary>
    Path,

    /// <summary>Installed by the app into its own folder (task 43).</summary>
    Managed,

    /// <summary>Named explicitly (the fake agent, a developer override).</summary>
    Configured,
}

/// <summary>An agent's program.</summary>
/// <param name="AgentId">The adapter's <see cref="IAgentAdapter.Id"/>.</param>
/// <param name="Executable">The program to start.</param>
/// <param name="Version">Its version as it reports it, when known.</param>
/// <param name="Source">Where it was found.</param>
public sealed record AgentInstallation(string AgentId, string Executable, string? Version, AgentSource Source)
{
    /// <summary>Arguments always passed before the run's own (e.g. the assembly of a <c>dotnet</c>-hosted program).</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];
}

public enum AgentAuthState
{
    SignedIn,
    SignedOut,

    /// <summary>The probe failed or its answer wasn't understood.</summary>
    Unknown,
}

/// <summary>Whether an agent is signed in.</summary>
/// <param name="State">Signed in, out, or unknown.</param>
/// <param name="Account">The account (e-mail or plan) when the agent shows it.</param>
/// <param name="Detail">The probe's own words when the state is unknown.</param>
public sealed record AgentAuthStatus(AgentAuthState State, string? Account = null, string? Detail = null);

/// <summary>Built-in agent tools a run may use, always inside its working folder (the generation working copy, task 46).</summary>
public enum AgentBuiltinTool
{
    ReadFiles,
    EditFiles,
    RunCommands,
}

/// <summary>An MCP server the agent starts for the run (the toolkit server, task 45); all its tools are allowed.</summary>
/// <param name="Name">The server's name, which the agent prefixes its tools with.</param>
/// <param name="Command">The program to start.</param>
/// <param name="Arguments">Its arguments.</param>
public sealed record AgentMcpServer(string Name, string Command, IReadOnlyList<string> Arguments)
{
    /// <summary>Extra environment variables of the server.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
}

/// <summary>One headless run of an agent.</summary>
/// <param name="Prompt">The task, passed on standard input when the agent allows it (no command-line length limit).</param>
/// <param name="WorkingDirectory">The folder the agent works in, and the only one its file tools may touch.</param>
public sealed record AgentRunRequest(string Prompt, string WorkingDirectory)
{
    /// <summary>Instructions appended to the agent's own system prompt.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>MCP servers of the run.</summary>
    public IReadOnlyList<AgentMcpServer> McpServers { get; init; } = [];

    /// <summary>Built-in tools allowed besides the MCP servers' (none by default).</summary>
    public IReadOnlySet<AgentBuiltinTool> BuiltinTools { get; init; } = new HashSet<AgentBuiltinTool>();

    /// <summary>The model, when the player picked one; <c>null</c> = the agent's default.</summary>
    public string? Model { get; init; }

    /// <summary>A session to continue (from <see cref="AgentSessionStarted.SessionId"/>), else a new one.</summary>
    public string? ResumeSessionId { get; init; }
}
