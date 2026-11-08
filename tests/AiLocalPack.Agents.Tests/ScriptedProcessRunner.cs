using System.Runtime.CompilerServices;
using AiLocalPack.Agents.Processes;

namespace AiLocalPack.Agents.Tests;

/// <summary>What a scripted process writes: its lines in order, then its exit code, or it hangs until killed.</summary>
public sealed record ScriptedOutput(IReadOnlyList<ProcessLine> Lines, int ExitCode = 0, bool Hangs = false)
{
    public static ScriptedOutput Of(params string[] stdout) =>
        new(stdout.Select(line => new ProcessLine(ProcessStream.StandardOutput, line)).ToList());
}

/// <summary>An <see cref="IProcessRunner"/> whose processes replay a <see cref="ScriptedOutput"/>, recording what was started.</summary>
public sealed class ScriptedProcessRunner(ScriptedOutput script) : IProcessRunner
{
    public ProcessStartException? StartError { get; init; }

    public ProcessSpec? Started { get; private set; }

    public ScriptedProcess? Process { get; private set; }

    public IRunningProcess Start(ProcessSpec spec, CancellationToken cancellationToken = default)
    {
        if (StartError is not null)
        {
            throw StartError;
        }

        Started = spec;
        Process = new ScriptedProcess(script, cancellationToken);
        return Process;
    }
}

public sealed class ScriptedProcess : IRunningProcess
{
    private const int KilledExitCode = 137;

    private readonly ScriptedOutput _script;
    private readonly TaskCompletionSource _killed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ProcessExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration _registration;

    public ScriptedProcess(ScriptedOutput script, CancellationToken cancellationToken)
    {
        _script = script;
        _registration = cancellationToken.Register(Kill);
    }

    public int Id => 4242;

    public bool Killed => _killed.Task.IsCompleted;

    public bool Disposed { get; private set; }

    public Task<ProcessExit> Exit => _exit.Task;

    public async IAsyncEnumerable<ProcessLine> ReadLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var line in _script.Lines)
        {
            if (Killed)
            {
                break;
            }

            await Task.Yield();
            yield return line;
        }

        if (_script.Hangs)
        {
            await _killed.Task.WaitAsync(cancellationToken);
        }

        _exit.TrySetResult(Killed ? new ProcessExit(KilledExitCode, true) : new ProcessExit(_script.ExitCode, false));
    }

    public void Kill()
    {
        if (!_exit.Task.IsCompleted)
        {
            _killed.TrySetResult();
            _exit.TrySetResult(new ProcessExit(KilledExitCode, true));
        }
    }

    public async ValueTask DisposeAsync()
    {
        Disposed = true;
        await _registration.DisposeAsync();
        Kill();
    }
}
