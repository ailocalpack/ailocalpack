namespace AiLocalPack.Agents.Processes;

/// <summary>Starts programs and streams their output (task 40); behind an interface so adapters are tested with scripted output.</summary>
public interface IProcessRunner
{
    /// <summary>
    /// Starts <paramref name="spec"/>. Cancelling <paramref name="cancellationToken"/> kills the process tree (the run's
    /// <see cref="ProcessExit.Killed"/> is then <c>true</c>). Throws <see cref="ProcessStartException"/> when it can't start.
    /// </summary>
    IRunningProcess Start(ProcessSpec spec, CancellationToken cancellationToken = default);
}

/// <summary>A started program. Disposing it kills the process tree when it still runs.</summary>
public interface IRunningProcess : IAsyncDisposable
{
    /// <summary>The OS process ID.</summary>
    int Id { get; }

    /// <summary>
    /// Standard output and error lines in the order they arrive (one reader only). Ends once both outputs closed, or
    /// <see cref="ProcessRunnerOptions.OutputGrace"/> after the process exited when something else still holds them.
    /// </summary>
    IAsyncEnumerable<ProcessLine> ReadLinesAsync(CancellationToken cancellationToken = default);

    /// <summary>Completes when the process exited and its output ended (never faults).</summary>
    Task<ProcessExit> Exit { get; }

    /// <summary>Kills the process and every process it started that still runs. Does nothing once it exited.</summary>
    void Kill();
}
