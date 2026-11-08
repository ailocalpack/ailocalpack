using AiLocalPack.Agents.Processes;

namespace AiLocalPack.Agents;

/// <summary>
/// An agent whose own sign-in the app can start (task 43): its login opens the browser, the app only waits and asks
/// <see cref="IAgentAdapter.GetAuthStatusAsync"/>; it never sees a token.
/// </summary>
public interface IAgentAccount
{
    /// <summary>
    /// Runs the agent's own login (browser), polling the sign-in status until signed in, the login ends, time runs out
    /// or <paramref name="cancellationToken"/> fires. Never throws.
    /// </summary>
    /// <param name="output">Each line the login prints (its sign-in URL, in case the browser didn't open).</param>
    Task<AgentSignInResult> SignInAsync(AgentInstallation installation, Action<ProcessLine>? output = null, CancellationToken cancellationToken = default);

    /// <summary>Runs the agent's own logout, then asks the status again. Never throws but for cancellation.</summary>
    Task<AgentAuthStatus> SignOutAsync(AgentInstallation installation, CancellationToken cancellationToken = default);
}

public enum AgentSignInOutcome
{
    /// <summary>The login finished: the status says signed in.</summary>
    SignedIn,

    /// <summary>It was signed in before: no login started.</summary>
    AlreadySignedIn,

    /// <summary>The login ended without signing in (closed browser tab, refused, error).</summary>
    Exited,

    /// <summary>No sign-in within the time limit; the login was stopped.</summary>
    TimedOut,

    /// <summary>Cancelled; the login was stopped.</summary>
    Cancelled,

    /// <summary>The login couldn't be started.</summary>
    StartFailed,
}

/// <summary>How a sign-in ended, the last status seen, and the login's own words when it failed.</summary>
public sealed record AgentSignInResult(AgentSignInOutcome Outcome, AgentAuthStatus? Status, string? Detail = null)
{
    public bool IsSignedIn => Outcome is AgentSignInOutcome.SignedIn or AgentSignInOutcome.AlreadySignedIn;
}

/// <param name="PollInterval">How often the status is asked while the login runs.</param>
/// <param name="Timeout">How long the player has to finish in the browser.</param>
/// <param name="Grace">How long a login that reported success may take to exit before it is stopped.</param>
public sealed record AgentSignInOptions(TimeSpan PollInterval, TimeSpan Timeout, TimeSpan Grace)
{
    public static AgentSignInOptions Default { get; } = new(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5));
}

/// <summary>
/// The sign-in loop shared by the agents (task 43): asks the status first (signed in → nothing started), starts the
/// agent's login process, forwards its lines, and asks the status every <see cref="AgentSignInOptions.PollInterval"/>
/// and once more when the login exits. Signed in → done (a login still running after <see cref="AgentSignInOptions.Grace"/>
/// is stopped); the login exited first → <see cref="AgentSignInOutcome.Exited"/> with its exit code and last lines;
/// past the time limit or cancelled → the login's process tree is killed.
/// </summary>
public static class AgentSignIn
{
    private const int KeptLines = 10;

    public static async Task<AgentSignInResult> RunAsync(
        IProcessRunner runner,
        ProcessSpec login,
        Func<CancellationToken, Task<AgentAuthStatus>> status,
        Action<ProcessLine>? output = null,
        AgentSignInOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(login);
        ArgumentNullException.ThrowIfNull(status);
        options ??= AgentSignInOptions.Default;
        AgentAuthStatus? last = null;
        try
        {
            last = await status(cancellationToken).ConfigureAwait(false);
            if (last.State == AgentAuthState.SignedIn)
            {
                return new AgentSignInResult(AgentSignInOutcome.AlreadySignedIn, last);
            }

            IRunningProcess process;
            try
            {
                process = runner.Start(login, CancellationToken.None);
            }
            catch (ProcessStartException e)
            {
                return new AgentSignInResult(AgentSignInOutcome.StartFailed, last, e.Message);
            }

            await using (process.ConfigureAwait(false))
            {
                var lines = new Queue<string>();
                var pump = Task.Run(async () =>
                {
                    await foreach (var line in process.ReadLinesAsync(CancellationToken.None).ConfigureAwait(false))
                    {
                        lock (lines)
                        {
                            lines.Enqueue(line.Text);
                            if (lines.Count > KeptLines)
                            {
                                lines.Dequeue();
                            }
                        }

                        output?.Invoke(line);
                    }
                }, CancellationToken.None);

                var deadline = DateTimeOffset.UtcNow + options.Timeout;
                while (true)
                {
                    var tick = Task.Delay(options.PollInterval, cancellationToken);
                    await Task.WhenAny(tick, process.Exit).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    var exited = process.Exit.IsCompleted;
                    if (exited)
                    {
                        await pump.ConfigureAwait(false);
                    }

                    last = await status(cancellationToken).ConfigureAwait(false);
                    if (last.State == AgentAuthState.SignedIn)
                    {
                        if (!exited && await Task.WhenAny(process.Exit, Task.Delay(options.Grace, cancellationToken)).ConfigureAwait(false) != process.Exit)
                        {
                            process.Kill();
                        }

                        return new AgentSignInResult(AgentSignInOutcome.SignedIn, last);
                    }

                    if (exited)
                    {
                        var exit = await process.Exit.ConfigureAwait(false);
                        string said;
                        lock (lines)
                        {
                            said = string.Join(" | ", lines.Where(l => l.Trim().Length > 0));
                        }

                        return new AgentSignInResult(
                            AgentSignInOutcome.Exited,
                            last,
                            $"The login exited with code {exit.ExitCode} without signing in" + (said.Length > 0 ? $": {said}" : "."));
                    }

                    if (DateTimeOffset.UtcNow >= deadline)
                    {
                        process.Kill();
                        return new AgentSignInResult(AgentSignInOutcome.TimedOut, last, $"Not signed in within {options.Timeout.TotalMinutes:0.#} min; the login was stopped.");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disposing the process killed its tree.
            return new AgentSignInResult(AgentSignInOutcome.Cancelled, last);
        }
    }
}
