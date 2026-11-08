namespace AiLocalPack.Agents.Processes;

/// <summary>Output of a short run (a <c>--version</c> or sign-in probe).</summary>
/// <param name="ExitCode">Exit code; <c>null</c> when the run was stopped for taking too long.</param>
/// <param name="StandardOutput">Standard output lines, joined with <c>\n</c>.</param>
/// <param name="StandardError">Standard error lines, joined with <c>\n</c>.</param>
public sealed record ProcessResult(int? ExitCode, string StandardOutput, string StandardError)
{
    public bool TimedOut => ExitCode is null;
}

public static class ProcessRunnerExtensions
{
    /// <summary>
    /// Runs <paramref name="spec"/> to the end and collects its output; past <paramref name="timeout"/> the tree is killed and
    /// <see cref="ProcessResult.ExitCode"/> is <c>null</c>. Throws <see cref="ProcessStartException"/>, and
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public static async Task<ProcessResult> RunToEndAsync(
        this IProcessRunner runner,
        ProcessSpec spec,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        await using var process = runner.Start(spec, limit.Token);
        var stdout = new List<string>();
        var stderr = new List<string>();
        await foreach (var line in process.ReadLinesAsync(CancellationToken.None))
        {
            (line.Stream == ProcessStream.StandardOutput ? stdout : stderr).Add(line.Text);
        }

        var exit = await process.Exit;
        cancellationToken.ThrowIfCancellationRequested();
        return new ProcessResult(exit.Killed ? null : exit.ExitCode, string.Join('\n', stdout), string.Join('\n', stderr));
    }
}
