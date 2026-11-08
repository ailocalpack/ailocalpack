using System.Runtime.CompilerServices;
using AiLocalPack.Agents.Processes;

namespace AiLocalPack.Agents;

/// <summary>
/// Reads one run's output in an agent's own format (task 40). A new parser is made for every run, so it may keep state
/// (open tool calls, the session). Never throws: a line it can't read becomes <see cref="AgentErrorKind.Malformed"/>.
/// </summary>
public interface IAgentOutputParser
{
    /// <summary>Events of one standard output line. An <see cref="AgentDone"/> here is the agent's own verdict (its result).</summary>
    IEnumerable<AgentEvent> ParseLine(string line);

    /// <summary>Events of one standard error line; by default a <see cref="AgentDiagnostic"/>.</summary>
    IEnumerable<AgentEvent> ParseErrorLine(string line) => [new AgentDiagnostic(line)];
}

/// <summary>
/// An agent driven as a child process whose standard output is a stream of JSON lines (both Claude Code and Codex). The
/// adapter builds the command line and the parser; this class runs it and keeps the <see cref="IAgentAdapter.RunAsync"/>
/// contract: exactly one <see cref="AgentDone"/> last, decided as cancelled → limit reached → signed out → the agent's own
/// verdict (a failure when the process exit code isn't 0) → failed for want of a result.
/// </summary>
public abstract class ProcessAgentAdapter(IProcessRunner runner) : IAgentAdapter
{
    private const int StderrTailLines = 20;
    private const int StderrTailLength = 2000;

    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    protected IProcessRunner Runner { get; } = runner ?? throw new ArgumentNullException(nameof(runner));

    public abstract Task<AgentInstallation?> DetectAsync(CancellationToken cancellationToken = default);

    public abstract Task<AgentAuthStatus> GetAuthStatusAsync(AgentInstallation installation, CancellationToken cancellationToken = default);

    /// <summary>The command line (and standard input, environment) of a run.</summary>
    protected abstract ProcessSpec CreateRunProcess(AgentInstallation installation, AgentRunRequest request);

    /// <summary>A fresh parser of the agent's output for one run.</summary>
    protected abstract IAgentOutputParser CreateParser(AgentRunRequest request);

    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentInstallation installation,
        AgentRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
        {
            yield return new AgentDone(AgentOutcome.Cancelled, Message: "Cancelled before the agent started.");
            yield break;
        }

        var process = TryStart(CreateRunProcess(installation, request), cancellationToken, out var startError);
        if (process is null)
        {
            yield return new AgentError(AgentErrorKind.StartFailed, startError!);
            yield return new AgentDone(AgentOutcome.Failed, Message: startError);
            yield break;
        }

        await using (process)
        {
            var parser = CreateParser(request);
            var state = new RunState();
            await foreach (var line in process.ReadLinesAsync(CancellationToken.None))
            {
                foreach (var e in Parse(parser, line, state))
                {
                    yield return e;
                }
            }

            var exit = await process.Exit;
            yield return Finish(state, exit, cancellationToken.IsCancellationRequested);
        }
    }

    private IRunningProcess? TryStart(ProcessSpec spec, CancellationToken cancellationToken, out string? error)
    {
        try
        {
            error = null;
            return Runner.Start(spec, cancellationToken);
        }
        catch (ProcessStartException e)
        {
            error = e.Message;
            return null;
        }
    }

    private static IEnumerable<AgentEvent> Parse(IAgentOutputParser parser, ProcessLine line, RunState state)
    {
        if (line.Stream == ProcessStream.StandardError)
        {
            state.AddStderr(line.Text);
        }

        if (line.Truncated)
        {
            return [new AgentError(AgentErrorKind.Malformed, $"An output line longer than {line.Text.Length} characters was cut and skipped.")];
        }

        var events = line.Stream == ProcessStream.StandardOutput ? parser.ParseLine(line.Text) : parser.ParseErrorLine(line.Text);
        var passed = new List<AgentEvent>();
        foreach (var e in events)
        {
            switch (e)
            {
                case AgentDone done:
                    state.Verdict ??= done;
                    continue;
                case AgentLimitReached limit:
                    state.Limit ??= limit;
                    break;
                case AgentError { Kind: AgentErrorKind.SignedOut } signedOut:
                    state.SignedOut ??= signedOut;
                    break;
            }

            passed.Add(e);
        }

        return passed;
    }

    private static AgentDone Finish(RunState state, ProcessExit exit, bool cancelled)
    {
        var result = state.Verdict?.Result;
        if (cancelled || exit.Killed)
        {
            return new AgentDone(AgentOutcome.Cancelled, result, "The run was cancelled; the agent was stopped.", exit.ExitCode);
        }

        if (state.Limit is { } limit)
        {
            return new AgentDone(AgentOutcome.LimitReached, result, limit.Message, exit.ExitCode);
        }

        if (state.SignedOut is { } signedOut)
        {
            return new AgentDone(AgentOutcome.SignedOut, result, signedOut.Message, exit.ExitCode);
        }

        if (state.Verdict is { } verdict)
        {
            if (verdict.Outcome != AgentOutcome.Succeeded || exit.ExitCode == 0)
            {
                return verdict with { ExitCode = exit.ExitCode, Message = verdict.Message ?? state.StderrTail() };
            }

            return new AgentDone(AgentOutcome.Failed, result, Join($"The agent exited with code {exit.ExitCode}.", state.StderrTail()), exit.ExitCode);
        }

        var reason = exit.ExitCode == 0 ? "The agent ended without a result." : $"The agent exited with code {exit.ExitCode} without a result.";
        return new AgentDone(AgentOutcome.Failed, null, Join(reason, state.StderrTail()), exit.ExitCode);
    }

    private static string Join(string reason, string? tail) => tail is null ? reason : reason + "\n" + tail;

    private sealed class RunState
    {
        private readonly Queue<string> _stderr = new();

        public AgentDone? Verdict { get; set; }

        public AgentLimitReached? Limit { get; set; }

        public AgentError? SignedOut { get; set; }

        public void AddStderr(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            _stderr.Enqueue(line);
            if (_stderr.Count > StderrTailLines)
            {
                _stderr.Dequeue();
            }
        }

        public string? StderrTail()
        {
            if (_stderr.Count == 0)
            {
                return null;
            }

            var tail = string.Join('\n', _stderr);
            return tail.Length <= StderrTailLength ? tail : "…" + tail[^StderrTailLength..];
        }
    }
}
