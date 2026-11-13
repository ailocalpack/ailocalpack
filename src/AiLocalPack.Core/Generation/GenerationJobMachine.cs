namespace AiLocalPack.Core.Generation;

/// <summary>What happens to a job: the inputs of <see cref="GenerationJobMachine"/>.</summary>
public abstract record GenerationTrigger
{
    /// <summary>The running stage finished; <paramref name="Counters"/> replaces the job's when given (Extract sets the totals).</summary>
    public sealed record StageCompleted(GenerationCounters? Counters = null) : GenerationTrigger;

    /// <summary>
    /// <paramref name="Chunks"/> Translate chunks finished (the next ones in order), <paramref name="Translated"/> more
    /// strings translated, in <paramref name="Took"/> (wall time: chunks run side by side share it).
    /// </summary>
    public sealed record ChunkCompleted(int Translated, TimeSpan Took, int Chunks = 1) : GenerationTrigger;

    /// <summary>The agent hit the subscription limit; <paramref name="ResumeAt"/> = its reset time.</summary>
    public sealed record LimitReached(DateTimeOffset ResumeAt) : GenerationTrigger;

    public sealed record PausedByPlayer : GenerationTrigger;

    /// <summary>A paused or interrupted job goes on (by the player, or by itself at the limit's reset time).</summary>
    public sealed record Resumed : GenerationTrigger;

    /// <summary>Found running with no runner (the app died): <see cref="GenerationJobStore.Recover"/>.</summary>
    public sealed record Interrupted : GenerationTrigger;

    /// <summary>The running stage failed.</summary>
    public sealed record Failed(GenerationFailureKind Kind, string Message) : GenerationTrigger;

    /// <summary>A failed stage is tried again.</summary>
    public sealed record Retried : GenerationTrigger;

    /// <summary>The player approved the glossary (task 54): the translation starts.</summary>
    public sealed record GlossaryApproved : GenerationTrigger;

    /// <summary>The player asked the agent to draft the glossary again (task 54): the Glossary stage runs again.</summary>
    public sealed record GlossaryRegenerated : GenerationTrigger;

    public sealed record Cancelled : GenerationTrigger;
}

/// <summary>A trigger the job's state doesn't allow (a bug, or a stale screen).</summary>
public sealed class GenerationTransitionException : InvalidOperationException
{
    public GenerationTransitionException(GenerationJob job, GenerationTrigger trigger)
        : base($"Job {job.Id} ({job.Status} at {job.Stage}) can't take {trigger.GetType().Name}.")
    {
        Status = job.Status;
        Stage = job.Stage;
        Trigger = trigger;
    }

    public GenerationTransitionException()
    {
        Trigger = new GenerationTrigger.Cancelled();
    }

    public GenerationTransitionException(string message)
        : base(message)
    {
        Trigger = new GenerationTrigger.Cancelled();
    }

    public GenerationTransitionException(string message, Exception inner)
        : base(message, inner)
    {
        Trigger = new GenerationTrigger.Cancelled();
    }

    public GenerationJobStatus Status { get; }

    public GenerationStage Stage { get; }

    public GenerationTrigger Trigger { get; }
}

/// <summary>
/// The job's state machine (task 47). Pure: the time is passed in. Allowed:
/// <list type="bullet">
/// <item>Running: stage completed (next stage; after Glossary → waiting for review when asked; after Build → Done +
/// Completed), chunk completed (Translate only), limit reached → Paused (limit), paused by player, interrupted, failed,
/// cancelled.</item>
/// <item>Paused, Interrupted: resumed → Running at the same stage and chunk; cancelled.</item>
/// <item>Failed: retried → Running at the failed stage; cancelled.</item>
/// <item>WaitingForReview: glossary approved → Running at Translate; glossary regenerated → Running at Glossary; cancelled.</item>
/// <item>Cancelled, Completed: nothing.</item>
/// </list>
/// Time running is added to <see cref="GenerationJob.ActiveTime"/> when leaving Running (an interrupted run up to the
/// last saved step, <see cref="GenerationJob.Updated"/>).
/// </summary>
public static class GenerationJobMachine
{
    public static bool CanApply(GenerationJob job, GenerationTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(trigger);
        return (job.Status, trigger) switch
        {
            (GenerationJobStatus.Running, GenerationTrigger.StageCompleted) => job.Stage != GenerationStage.Done,
            (GenerationJobStatus.Running, GenerationTrigger.ChunkCompleted chunk) =>
                job.Stage == GenerationStage.Translate && chunk.Chunks > 0 && job.Counters.ChunksDone + chunk.Chunks <= job.Counters.Chunks,
            (GenerationJobStatus.Running, GenerationTrigger.LimitReached or GenerationTrigger.PausedByPlayer
                or GenerationTrigger.Interrupted or GenerationTrigger.Failed or GenerationTrigger.Cancelled) => true,
            (GenerationJobStatus.Paused or GenerationJobStatus.Interrupted, GenerationTrigger.Resumed or GenerationTrigger.Cancelled) => true,
            (GenerationJobStatus.Failed, GenerationTrigger.Retried or GenerationTrigger.Cancelled) => true,
            (GenerationJobStatus.WaitingForReview, GenerationTrigger.GlossaryApproved or GenerationTrigger.GlossaryRegenerated
                or GenerationTrigger.Cancelled) => true,
            _ => false,
        };
    }

    /// <exception cref="GenerationTransitionException">The job's state doesn't allow <paramref name="trigger"/>.</exception>
    public static GenerationJob Apply(GenerationJob job, GenerationTrigger trigger, DateTimeOffset now)
    {
        if (!CanApply(job, trigger))
        {
            throw new GenerationTransitionException(job, trigger);
        }

        var next = trigger switch
        {
            GenerationTrigger.StageCompleted done => CompleteStage(job, done, now),
            GenerationTrigger.ChunkCompleted chunk => job with
            {
                Counters = job.Counters with
                {
                    ChunksDone = job.Counters.ChunksDone + chunk.Chunks,
                    Translated = Math.Min(job.Counters.Strings, job.Counters.Translated + Math.Max(0, chunk.Translated)),
                },
                ChunkTime = job.ChunkTime + (chunk.Took > TimeSpan.Zero ? chunk.Took : TimeSpan.Zero),
            },
            GenerationTrigger.LimitReached limit => Stop(job, now, GenerationJobStatus.Paused) with
            {
                PauseReason = GenerationPauseReason.Limit,
                ResumeAt = limit.ResumeAt,
            },
            GenerationTrigger.PausedByPlayer => Stop(job, now, GenerationJobStatus.Paused) with { PauseReason = GenerationPauseReason.Player },
            GenerationTrigger.Interrupted => Stop(job, job.Updated, GenerationJobStatus.Interrupted),
            GenerationTrigger.Failed failed => Stop(job, now, GenerationJobStatus.Failed) with
            {
                Failure = new GenerationFailure(job.Stage, failed.Kind, failed.Message),
            },
            GenerationTrigger.Cancelled => Stop(job, now, GenerationJobStatus.Cancelled),
            GenerationTrigger.Resumed or GenerationTrigger.Retried or GenerationTrigger.GlossaryRegenerated => Start(job, now),
            GenerationTrigger.GlossaryApproved => Start(job, now) with { Stage = GenerationStage.Translate },
            _ => throw new GenerationTransitionException(job, trigger),
        };
        return next with { Updated = now };
    }

    /// <summary>A new job, running at Setup.</summary>
    public static GenerationJob Create(
        string id,
        Games.GameId game,
        string gameName,
        string language,
        string agent,
        GenerationOptions options,
        DateTimeOffset now,
        TimeSpan? estimatedTime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(game);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        ArgumentNullException.ThrowIfNull(options);
        return new GenerationJob
        {
            Id = id,
            Game = game,
            GameName = gameName,
            Language = language,
            Agent = agent,
            Options = options,
            Created = now,
            Updated = now,
            RunningSince = now,
            EstimatedTime = estimatedTime,
        };
    }

    private static GenerationJob CompleteStage(GenerationJob job, GenerationTrigger.StageCompleted done, DateTimeOffset now)
    {
        var counters = done.Counters ?? job.Counters;
        if (job.Stage == GenerationStage.Translate)
        {
            counters = counters with { ChunksDone = counters.Chunks };
        }

        var next = job with { Counters = counters, Stage = GenerationStages.After(job.Stage, job.Options) };
        if (next.Stage == GenerationStage.Done)
        {
            return Stop(next, now, GenerationJobStatus.Completed);
        }

        return job.Stage == GenerationStage.Glossary && job.Options.ReviewGlossary
            ? Stop(job with { Counters = counters }, now, GenerationJobStatus.WaitingForReview)
            : next;
    }

    private static GenerationJob Start(GenerationJob job, DateTimeOffset now) => job with
    {
        Status = GenerationJobStatus.Running,
        PauseReason = null,
        ResumeAt = null,
        Failure = null,
        RunningSince = now,
    };

    private static GenerationJob Stop(GenerationJob job, DateTimeOffset end, GenerationJobStatus status) => job with
    {
        Status = status,
        ActiveTime = job.ActiveTimeAt(end),
        RunningSince = null,
        PauseReason = null,
        ResumeAt = null,
        Failure = null,
    };
}
