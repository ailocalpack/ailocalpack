using AiLocalPack.Agents.Processes;

namespace AiLocalPack.Agents.Fake;

/// <summary>
/// The fake agent (task 40): runs a <see cref="FakeAgentReplay"/> program on one recording, the prompt on its standard
/// input, in the run's working folder, and reads its output as <see cref="AgentEventJson"/> lines. Used by agent and
/// pipeline tests and by <c>agent run --fake</c>; the run's MCP servers, tools and model are ignored.
/// </summary>
/// <param name="runner">Starts the replayer.</param>
/// <param name="replayer">The replayer program and the arguments before the recording (<c>agent replay</c>, an assembly…).</param>
/// <param name="recording">The recording every run replays.</param>
/// <param name="authState">What the sign-in probe answers.</param>
public sealed class FakeAgentAdapter(
    IProcessRunner runner,
    FakeAgentReplayer replayer,
    string recording,
    AgentAuthState authState = AgentAuthState.SignedIn) : ProcessAgentAdapter(runner)
{
    public const string AgentId = "fake";

    public override string Id => AgentId;

    public override string DisplayName => "Fake agent";

    public override Task<AgentInstallation?> DetectAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<AgentInstallation?>(
            new AgentInstallation(AgentId, replayer.Executable, "replay", AgentSource.Configured) { Arguments = replayer.Arguments });

    public override Task<AgentAuthStatus> GetAuthStatusAsync(AgentInstallation installation, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AgentAuthStatus(authState, authState == AgentAuthState.SignedIn ? "fake@example.com" : null));

    protected override ProcessSpec CreateRunProcess(AgentInstallation installation, AgentRunRequest request) =>
        new(installation.Executable, [.. installation.Arguments, Path.GetFullPath(recording)])
        {
            WorkingDirectory = request.WorkingDirectory,
            StandardInput = request.Prompt,
        };

    protected override IAgentOutputParser CreateParser(AgentRunRequest request) => new FakeAgentParser();
}

/// <summary>A program that replays recordings (<see cref="FakeAgentReplay"/>): itself and the arguments before the recording.</summary>
public sealed record FakeAgentReplayer(string Executable, IReadOnlyList<string> Arguments)
{
    /// <summary>
    /// The running program with <paramref name="arguments"/> before the recording: a framework-dependent run
    /// (<c>dotnet x.dll</c>) by <c>dotnet</c> + the entry assembly, else the process's own executable.
    /// </summary>
    public static FakeAgentReplayer ForCurrentProcess(params IReadOnlyList<string> arguments)
    {
        var process = Environment.ProcessPath ?? throw new InvalidOperationException("The program's path is unknown.");
        if (string.Equals(Path.GetFileNameWithoutExtension(process), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assembly = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(assembly))
            {
                throw new InvalidOperationException("The entry assembly's path is unknown.");
            }

            return new FakeAgentReplayer(process, [assembly, .. arguments]);
        }

        return new FakeAgentReplayer(process, arguments);
    }

    /// <summary>Start info of this replayer on <paramref name="recording"/>, its outputs shared with the caller's.</summary>
    public System.Diagnostics.ProcessStartInfo StartInfo(string recording)
    {
        var info = new System.Diagnostics.ProcessStartInfo(Executable) { UseShellExecute = false };
        foreach (var argument in Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.ArgumentList.Add(recording);
        return info;
    }
}

/// <summary>Reads the fake agent's output: one <see cref="AgentEventJson"/> event per line.</summary>
public sealed class FakeAgentParser : IAgentOutputParser
{
    private int _line;

    public IEnumerable<AgentEvent> ParseLine(string line)
    {
        _line++;
        if (string.IsNullOrWhiteSpace(line))
        {
            return [];
        }

        return AgentEventJson.Read(line, out var error) is { } e
            ? [e]
            : [new AgentError(AgentErrorKind.Malformed, $"Output line {_line}: {error}.")];
    }
}
