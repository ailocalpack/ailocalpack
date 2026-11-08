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

    /// <summary>
    /// The command line (and standard input, environment) of a run, with the files made for it. May throw
    /// <see cref="ProcessStartException"/>, <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>: the run
    /// then ends as failed to start.
    /// </summary>
    protected abstract AgentRunProcess CreateRunProcess(AgentInstallation installation, AgentRunRequest request);

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

        AgentRunProcess? run = null;
        try
        {
            var process = TryStart(installation, request, cancellationToken, out run, out var startError);
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
        finally
        {
            DeleteScratch(run);
        }
    }

    private IRunningProcess? TryStart(
        AgentInstallation installation,
        AgentRunRequest request,
        CancellationToken cancellationToken,
        out AgentRunProcess? run,
        out string? error)
    {
        run = null;
        try
        {
            run = CreateRunProcess(installation, request);
            error = null;
            return Runner.Start(run.Spec, cancellationToken);
        }
        catch (ProcessStartException e)
        {
            error = e.Message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = $"The run's files couldn't be written: {e.Message}";
        }

        return null;
    }

    private static void DeleteScratch(AgentRunProcess? run)
    {
        if (run?.ScratchDirectory is not { } folder)
        {
            return;
        }

        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: a temp folder left behind is harmless.
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

/// <summary>The process of one agent run.</summary>
/// <param name="Spec">What to start.</param>
public sealed record AgentRunProcess(ProcessSpec Spec)
{
    /// <summary>A folder of files made for this run only (an MCP config, a system prompt), deleted once it ended.</summary>
    public string? ScratchDirectory { get; init; }
}
