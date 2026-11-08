using AiLocalPack.Core.Generation;

namespace AiLocalPack.Agents;

/// <summary>How one agent run of a generation step ended (<see cref="AgentStepRun.RunAsync"/>).</summary>
/// <param name="Done">The run's last event.</param>
/// <param name="Stop">
/// What the step reports when the run didn't succeed: the limit (with its reset time), signed out, failed; <c>null</c>
/// when it succeeded and the stage decides what the result means.
/// </param>
/// <param name="Usage">The last usage the agent reported, if any.</param>
public sealed record AgentStepOutcome(AgentDone Done, GenerationStepResult? Stop, AgentUsage? Usage)
{
    public bool Succeeded => Stop is null;
}

/// <summary>
/// Runs an agent for a generation step (task 47; the stages of tasks 49–51 use it): passes every event on (the job log,
/// the progress card's activity), and maps the end to the step's result. A cancelled run throws
/// <see cref="OperationCanceledException"/>, so the runner pauses or cancels the job as the player asked.
/// </summary>
public static class AgentStepRun
{
    public static async Task<AgentStepOutcome> RunAsync(
        IAgentAdapter adapter,
        AgentInstallation installation,
        AgentRunRequest request,
        Action<AgentEvent>? onEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(request);
        AgentLimitReached? limit = null;
        AgentUsage? usage = null;
        AgentDone? done = null;
        var startFailed = false;
        await foreach (var e in adapter.RunAsync(installation, request, cancellationToken).ConfigureAwait(false))
        {
            onEvent?.Invoke(e);
            switch (e)
            {
                case AgentLimitReached l:
                    limit ??= l;
                    break;
                case AgentUsage u:
                    usage = u;
                    break;
                case AgentError { Kind: AgentErrorKind.StartFailed }:
                    startFailed = true;
                    break;
                case AgentDone d:
                    done = d;
                    break;
            }
        }

        done ??= new AgentDone(AgentOutcome.Failed, Message: "The agent's run ended without a result.");
        return new AgentStepOutcome(done, ToResult(done, limit, startFailed, cancellationToken), usage);
    }

    /// <summary>
    /// The step's result for a run that ended with <paramref name="done"/>; <c>null</c> = succeeded.
    /// <paramref name="startFailed"/>: the program couldn't be started (<see cref="AgentErrorKind.StartFailed"/>).
    /// </summary>
    public static GenerationStepResult? ToResult(AgentDone done, AgentLimitReached? limit, bool startFailed = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(done);
        return done.Outcome switch
        {
            AgentOutcome.Succeeded => null,
            AgentOutcome.LimitReached => new GenerationStepResult.LimitReached(limit?.ResetsAt, limit?.Message ?? done.Message),
            AgentOutcome.SignedOut => new GenerationStepResult.Failed(GenerationFailureKind.SignedOut, done.Message ?? "The agent isn't signed in."),
            AgentOutcome.Cancelled => throw new OperationCanceledException("The agent's run was cancelled.", cancellationToken),
            _ => new GenerationStepResult.Failed(
                startFailed ? GenerationFailureKind.AgentMissing : GenerationFailureKind.Agent,
                done.Message ?? done.Result ?? "The agent failed."),
        };
    }
}
