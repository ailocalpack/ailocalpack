using AiLocalPack.Core.Games;

namespace AiLocalPack.Core.Generation;

/// <summary>The stages of a generation, in order (task 47). Their handlers come with tasks 49–51.</summary>
public enum GenerationStage
{
    /// <summary>The working copy of the game (task 46).</summary>
    Setup,

    /// <summary>The strings, through the toolkit or the agent (task 49).</summary>
    Extract,

    /// <summary>The glossary: names, terms, tone (task 49); may wait for the player's review.</summary>
    Glossary,

    /// <summary>The strings in chunks (task 50).</summary>
    Translate,

    /// <summary>Rebuild, test run, local pack (task 51).</summary>
    Build,

    /// <summary>Finished.</summary>
    Done,
}

/// <summary>Where a job stands.</summary>
public enum GenerationJobStatus
{
    /// <summary>A runner works on it (or worked on it until the app died: <see cref="GenerationJobStore.Recover"/> tells).</summary>
    Running,

    /// <summary>Stopped by the subscription limit (<see cref="GenerationJob.ResumeAt"/>) or by the player.</summary>
    Paused,

    /// <summary>The glossary is done and the player asked to review it before the translation.</summary>
    WaitingForReview,

    /// <summary>The app or the machine stopped while it ran; resumes from where it was.</summary>
    Interrupted,

    /// <summary>A stage failed (<see cref="GenerationJob.Failure"/>); the stage can be tried again.</summary>
    Failed,

    /// <summary>The player cancelled it. Final.</summary>
    Cancelled,

    /// <summary>All stages done. Final.</summary>
    Completed,
}

public enum GenerationPauseReason
{
    /// <summary>The agent's subscription limit; resumes by itself at <see cref="GenerationJob.ResumeAt"/>.</summary>
    Limit,

    /// <summary>The player paused it.</summary>
    Player,
}

/// <summary>What went wrong in a failed stage, for the UI's wording.</summary>
public enum GenerationFailureKind
{
    /// <summary>The agent failed or reported an error.</summary>
    Agent,

    /// <summary>The agent isn't signed in (any more).</summary>
    SignedOut,

    /// <summary>The agent isn't installed, or its program can't start.</summary>
    AgentMissing,

    /// <summary>The toolkit couldn't read or rebuild the game.</summary>
    Toolkit,

    /// <summary>The working copy couldn't be made (disk full, game missing).</summary>
    WorkingCopy,

    /// <summary>A job file couldn't be read or written.</summary>
    Disk,

    /// <summary>Anything else (a bug).</summary>
    Unexpected,
}

/// <param name="Stage">The stage that failed.</param>
/// <param name="Message">The reason in plain English (logs, CLI); the UI words it from <paramref name="Kind"/>.</param>
public sealed record GenerationFailure(GenerationStage Stage, GenerationFailureKind Kind, string Message);

/// <summary>A job's counters.</summary>
/// <param name="Strings">Strings to translate (known after Extract).</param>
/// <param name="Translated">Strings translated so far.</param>
/// <param name="Chunks">Chunks of the Translate stage (known after Extract or Glossary).</param>
/// <param name="ChunksDone">Chunks finished: the next one to run is number <paramref name="ChunksDone"/> (0-based).</param>
public sealed record GenerationCounters(int Strings = 0, int Translated = 0, int Chunks = 0, int ChunksDone = 0)
{
    public static readonly GenerationCounters Zero = new();
}

/// <summary>What the player chose on the start form.</summary>
/// <param name="ReviewGlossary">Wait for the player after the glossary (task 54).</param>
/// <param name="Scope">Which game files the working copy holds; <c>null</c> = the default for the engine.</param>
/// <param name="Model">The agent's model, when the player picked one.</param>
public sealed record GenerationOptions(bool ReviewGlossary = false, WorkingCopyScope? Scope = null, string? Model = null)
{
    public static readonly GenerationOptions Default = new();
}

/// <summary>
/// One generation of a pack (task 47), persisted as <c>job.json</c> in its job folder after every step, so it survives
/// app restarts, crashes and subscription limits. Changed only through <see cref="GenerationJobMachine"/>.
/// </summary>
public sealed record GenerationJob
{
    /// <summary><see cref="WorkingCopies.NewJobId"/>; the job folder's name.</summary>
    public required string Id { get; init; }

    public required GameId Game { get; init; }

    /// <summary>The game's name when the job started.</summary>
    public required string GameName { get; init; }

    /// <summary>The target language (a <see cref="Languages.GameLanguages.Tags"/> tag).</summary>
    public required string Language { get; init; }

    /// <summary>The agent's adapter ID (<c>claude</c>, <c>codex</c>, <c>fake</c>).</summary>
    public required string Agent { get; init; }

    public GenerationOptions Options { get; init; } = GenerationOptions.Default;

    public GenerationStage Stage { get; init; } = GenerationStage.Setup;

    public GenerationJobStatus Status { get; init; } = GenerationJobStatus.Running;

    /// <summary>Set while <see cref="GenerationJobStatus.Paused"/>.</summary>
    public GenerationPauseReason? PauseReason { get; init; }

    /// <summary>When a job paused by the limit resumes by itself (the limit's reset time).</summary>
    public DateTimeOffset? ResumeAt { get; init; }

    /// <summary>Set while <see cref="GenerationJobStatus.Failed"/>.</summary>
    public GenerationFailure? Failure { get; init; }

    public GenerationCounters Counters { get; init; } = GenerationCounters.Zero;

    public required DateTimeOffset Created { get; init; }

    /// <summary>The last change (written with it). A crash leaves it at the last step that was saved.</summary>
    public required DateTimeOffset Updated { get; init; }

    /// <summary>Time spent running before the current run (<see cref="RunningSince"/>).</summary>
    public TimeSpan ActiveTime { get; init; }

    /// <summary>The current run's start while <see cref="GenerationJobStatus.Running"/>.</summary>
    public DateTimeOffset? RunningSince { get; init; }

    /// <summary>Time the finished chunks took (the ETA's measure).</summary>
    public TimeSpan ChunkTime { get; init; }

    /// <summary>The size estimate's agent time (task 46) for the ETA until chunks are measured; <c>null</c> = none.</summary>
    public TimeSpan? EstimatedTime { get; init; }

    /// <summary>The job can still go on (not cancelled, not completed): one per game.</summary>
    public bool IsOpen => Status is not (GenerationJobStatus.Cancelled or GenerationJobStatus.Completed);

    /// <summary>Time spent running up to <paramref name="now"/>.</summary>
    public TimeSpan ActiveTimeAt(DateTimeOffset now) =>
        RunningSince is { } since && now > since ? ActiveTime + (now - since) : ActiveTime;
}
