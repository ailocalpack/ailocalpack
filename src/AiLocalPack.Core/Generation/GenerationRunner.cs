using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Registry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Core.Generation;

/// <summary>What one step of a stage handler ended with.</summary>
public abstract record GenerationStepResult
{
    /// <summary>The stage is done; <paramref name="Counters"/> replaces the job's when given.</summary>
    public sealed record StageDone(GenerationCounters? Counters = null) : GenerationStepResult;

    /// <summary>One Translate chunk (number <see cref="GenerationCounters.ChunksDone"/>) is done.</summary>
    public sealed record ChunkDone(int Translated) : GenerationStepResult;

    /// <summary>The agent hit its limit; <paramref name="ResetsAt"/> = when it resets, if it said.</summary>
    public sealed record LimitReached(DateTimeOffset? ResetsAt, string? Message = null) : GenerationStepResult;

    public sealed record Failed(GenerationFailureKind Kind, string Message) : GenerationStepResult;
}

/// <summary>What a stage handler gets for one step.</summary>
public sealed class GenerationStepContext(GenerationJobEntry entry, TimeProvider time)
{
    public GenerationJob Job => entry.Job;

    /// <summary>The job folder.</summary>
    public string Folder => entry.Folder;

    /// <summary>The working copy (the agent's and the MCP server's root).</summary>
    public string CopyFolder => WorkingCopies.CopyFolder(entry.Folder);

    public GenerationFiles Files { get; } = new(entry.Folder);

    public TimeProvider Time => time;

    /// <summary>Adds a line to the job's log.</summary>
    public void Log(string line) => GenerationJobLog.Append(entry.Folder, time, line);
}

/// <summary>
/// The handler of one stage (tasks 49–51). A step is the unit that is saved: a whole stage, or one chunk of Translate
/// (the next chunk is <see cref="GenerationCounters.ChunksDone"/>, so a resumed job goes on from it). A cancelled step
/// is run again from its start on resume, so a step must be safe to repeat.
/// </summary>
public interface IGenerationStage
{
    GenerationStage Stage { get; }

    /// <summary>Runs one step. Throws only <see cref="OperationCanceledException"/>; other exceptions fail the stage as unexpected.</summary>
    Task<GenerationStepResult> RunStepAsync(GenerationStepContext context, CancellationToken cancellationToken);
}

/// <summary>The handlers of a job, one per stage.</summary>
public sealed class GenerationStages
{
    private readonly Dictionary<GenerationStage, IGenerationStage> _stages;

    /// <param name="stages">One handler per stage.</param>
    /// <param name="pauseBefore">
    /// The job pauses (as if by the player) once it reaches this stage, before running it: <c>gen run --until glossary</c>
    /// pauses before Translate. <c>null</c> = runs on.
    /// </param>
    public GenerationStages(IEnumerable<IGenerationStage> stages, GenerationStage? pauseBefore = null)
    {
        PauseBefore = pauseBefore is GenerationStage.Setup or GenerationStage.Done
            ? throw new ArgumentOutOfRangeException(nameof(pauseBefore), pauseBefore, "A job can only pause before Extract, Glossary, Translate or Build.")
            : pauseBefore;
        ArgumentNullException.ThrowIfNull(stages);
        _stages = [];
        foreach (var stage in stages)
        {
            if (!_stages.TryAdd(stage.Stage, stage))
            {
                throw new ArgumentException($"Two handlers for {stage.Stage}.", nameof(stages));
            }
        }
    }

    /// <summary>The stage the job pauses before, if any.</summary>
    public GenerationStage? PauseBefore { get; }

    public IGenerationStage? For(GenerationStage stage) => _stages.GetValueOrDefault(stage);
}

/// <summary>
/// A job being run (<see cref="GenerationJobs"/>): its latest state, <see cref="Changed"/> after every saved change (on
/// the runner's thread), and the player's <see cref="Pause"/> / <see cref="Cancel"/>, which stop the running step.
/// </summary>
public sealed class GenerationRun : IProgress<GenerationJob>
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private GenerationJob _job;
    private int _request;

    internal GenerationRun(string folder, GenerationJob job)
    {
        Folder = folder;
        _job = job;
    }

    public event EventHandler<GenerationJob>? Changed;

    public string Folder { get; }

    public GenerationJob Job
    {
        get
        {
            lock (_lock)
            {
                return _job;
            }
        }
    }

    /// <summary>The job's state when the run ended (paused, waiting, failed, interrupted, cancelled, completed).</summary>
    public Task<GenerationJob> Completion { get; internal set; } = Task.FromResult<GenerationJob>(null!);

    internal CancellationToken StopToken => _stop.Token;

    internal GenerationTrigger? Requested => Volatile.Read(ref _request) switch
    {
        1 => new GenerationTrigger.PausedByPlayer(),
        2 => new GenerationTrigger.Cancelled(),
        _ => null,
    };

    /// <summary>The player pauses: the running step stops and is run again on resume.</summary>
    public void Pause() => Request(1);

    /// <summary>The player cancels (the job folder stays; deleting it is the caller's choice).</summary>
    public void Cancel() => Request(2);

    void IProgress<GenerationJob>.Report(GenerationJob value)
    {
        lock (_lock)
        {
            _job = value;
        }

        Changed?.Invoke(this, value);
    }

    private void Request(int request)
    {
        // A cancel overrides a pause; a pause doesn't undo a cancel.
        int current;
        do
        {
            current = Volatile.Read(ref _request);
            if (current >= request)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _request, request, current) != current);

        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run ended meanwhile.
        }
    }
}

/// <summary>
/// Runs a job's stages step by step (task 47): calls the handler of the job's stage (Translate once per chunk, and
/// completed by the runner once no chunk is left), applies its result through
/// <see cref="GenerationJobMachine"/>, writes <c>job.json</c> and reports the job after every step, until the job stops
/// running. A limit → paused until its reset time (<see cref="DefaultLimitWait"/> when the agent didn't say); the
/// player's pause / cancel → paused / cancelled; the caller's cancellation (the app quits, Ctrl+C) → interrupted; an
/// exception from a handler → failed (unexpected). Never throws.
/// </summary>
public sealed class GenerationRunner
{
    /// <summary>How long a job paused by a limit without a reset time waits before trying again.</summary>
    public static readonly TimeSpan DefaultLimitWait = TimeSpan.FromHours(1);

    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public GenerationRunner(TimeProvider? time = null, ILogger<GenerationRunner>? logger = null)
    {
        _time = time ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Runs the job of <paramref name="run"/> while it is <see cref="GenerationJobStatus.Running"/>.</summary>
    internal async Task<GenerationJob> RunAsync(GenerationRun run, GenerationStages stages, CancellationToken cancellationToken)
    {
        var job = run.Job;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(run.StopToken, cancellationToken);
        Log(run.Folder, $"Running at {job.Stage} ({job.Counters.ChunksDone} of {job.Counters.Chunks} chunks done).");
        while (job.Status == GenerationJobStatus.Running)
        {
            if (run.Requested is { } requested)
            {
                job = Save(run, job, requested);
                break;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                job = Save(run, job, new GenerationTrigger.Interrupted());
                break;
            }

            if (stages.PauseBefore is { } stop && job.Stage >= stop)
            {
                Log(run.Folder, $"Stopping before {stop}, as asked.");
                job = Save(run, job, new GenerationTrigger.PausedByPlayer());
                break;
            }

            var started = _time.GetTimestamp();
            GenerationTrigger trigger;
            try
            {
                var handler = stages.For(job.Stage);
                trigger = job.Stage == GenerationStage.Translate && job.Counters.ChunksDone >= job.Counters.Chunks
                    ? new GenerationTrigger.StageCompleted()
                    : handler is null
                    ? new GenerationTrigger.Failed(GenerationFailureKind.Unexpected, $"No handler for the {job.Stage} stage.")
                    : ToTrigger(job, await handler.RunStepAsync(new GenerationStepContext(new GenerationJobEntry(run.Folder, job), _time), linked.Token).ConfigureAwait(false), _time.GetElapsedTime(started));
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                trigger = run.Requested ?? new GenerationTrigger.Interrupted();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Generation stage {Stage} of job {Job} threw", job.Stage, job.Id);
                trigger = new GenerationTrigger.Failed(GenerationFailureKind.Unexpected, e.Message);
            }

            if (trigger is GenerationTrigger.Interrupted)
            {
                // Saved now rather than found after a restart: the time up to now counts.
                job = Save(run, job with { Updated = _time.GetUtcNow() }, trigger);
                break;
            }

            job = Save(run, job, GenerationJobMachine.CanApply(job, trigger)
                ? trigger
                : new GenerationTrigger.Failed(GenerationFailureKind.Unexpected, $"The {job.Stage} stage reported {trigger.GetType().Name}, which its state doesn't allow."));
        }

        return job;
    }

    private GenerationTrigger ToTrigger(GenerationJob job, GenerationStepResult result, TimeSpan took) => result switch
    {
        GenerationStepResult.StageDone done => new GenerationTrigger.StageCompleted(done.Counters),
        GenerationStepResult.ChunkDone chunk => new GenerationTrigger.ChunkCompleted(chunk.Translated, took),
        GenerationStepResult.LimitReached limit => new GenerationTrigger.LimitReached(ResumeTime(limit.ResetsAt)),
        GenerationStepResult.Failed failed => new GenerationTrigger.Failed(failed.Kind, failed.Message),
        _ => new GenerationTrigger.Failed(GenerationFailureKind.Unexpected, $"The {job.Stage} stage returned nothing known."),
    };

    private DateTimeOffset ResumeTime(DateTimeOffset? resetsAt)
    {
        var now = _time.GetUtcNow();
        return resetsAt is { } reset && reset > now ? reset : now + (resetsAt is null ? DefaultLimitWait : TimeSpan.FromMinutes(1));
    }

    /// <summary>Applies, writes and reports; a failed write ends the run as failed (disk), kept in memory only.</summary>
    private GenerationJob Save(GenerationRun run, GenerationJob job, GenerationTrigger trigger)
    {
        var next = GenerationJobMachine.Apply(job, trigger, _time.GetUtcNow());
        try
        {
            GenerationJobStore.Write(run.Folder, next);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(e, "Couldn't save generation job {Job}", job.Id);
            next = next.Status == GenerationJobStatus.Running
                ? GenerationJobMachine.Apply(next, new GenerationTrigger.Failed(GenerationFailureKind.Disk, $"job.json can't be written: {e.Message}"), _time.GetUtcNow())
                : next;
        }

        Log(run.Folder, Describe(next, trigger));
        ((IProgress<GenerationJob>)run).Report(next);
        return next;
    }

    private void Log(string folder, string line) => GenerationJobLog.Append(folder, _time, line);

    private static string Describe(GenerationJob job, GenerationTrigger trigger) => trigger switch
    {
        GenerationTrigger.ChunkCompleted => $"Chunk {job.Counters.ChunksDone} of {job.Counters.Chunks} done ({job.Counters.Translated} of {job.Counters.Strings} strings).",
        GenerationTrigger.StageCompleted when job.Status == GenerationJobStatus.Completed => "Completed.",
        GenerationTrigger.StageCompleted when job.Status == GenerationJobStatus.WaitingForReview => "Glossary done: waiting for the player's review.",
        GenerationTrigger.StageCompleted => $"Stage done; next: {job.Stage}.",
        GenerationTrigger.LimitReached => $"Paused by the agent's limit; resumes at {job.ResumeAt:u}.",
        GenerationTrigger.PausedByPlayer => "Paused by the player.",
        GenerationTrigger.Cancelled => "Cancelled by the player.",
        GenerationTrigger.Interrupted => "Interrupted (the app stopped).",
        _ when job.Failure is { } failure => $"{failure.Stage} failed ({failure.Kind}): {failure.Message}",
        _ => trigger.GetType().Name,
    };
}

/// <summary>
/// Starts, resumes and cancels generation jobs (task 47): one open job per game, a blocked game refused first
/// (<see cref="GameBlocks.EnsureAllowed"/>, whatever the UI showed), a job's lock held while it runs. The caller gives
/// the stage handlers (tasks 49–51 provide them). Methods that touch the disk block; call them off the UI thread.
/// </summary>
public sealed class GenerationJobs(GenerationJobStore store, GenerationRunner runner, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public GenerationJobStore Store => store;

    /// <summary>Creates the game's job and runs it in the background.</summary>
    /// <exception cref="GameBlockedException">The game is blocked (anti-cheat, opt-out).</exception>
    /// <exception cref="GenerationJobException">The game already has an open job.</exception>
    /// <exception cref="IOException">The job folder can't be written.</exception>
    public GenerationRun Start(
        Game game,
        AntiCheatPolicy policy,
        RegistryIndex? index,
        string language,
        string agent,
        GenerationOptions options,
        Func<GenerationJobEntry, GenerationStages> stages,
        TimeSpan? estimatedTime = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(stages);
        GameBlocks.EnsureAllowed(game, policy, index, BlockedOperation.Generate);
        var (entry, jobLock) = store.Create(game, language, agent, options, estimatedTime);
        GenerationJobLog.Append(entry.Folder, _time, $"Created for {game.Name} ({game.Id}) into {language} with agent {agent}.");
        return Launch(entry, jobLock, stages, cancellationToken);
    }

    /// <summary>
    /// Goes on with a paused or interrupted job from its stage and chunk, tries a failed stage again, or starts the
    /// translation once the glossary is approved (waiting for review). A job left running with no runner is taken as
    /// interrupted.
    /// </summary>
    /// <exception cref="GameBlockedException">The game is blocked by now.</exception>
    /// <exception cref="GenerationJobException">Not found, unreadable, busy (another runner has it), or cancelled / completed.</exception>
    public GenerationRun Resume(
        string folder,
        Game game,
        AntiCheatPolicy policy,
        RegistryIndex? index,
        Func<GenerationJobEntry, GenerationStages> stages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(stages);
        GameBlocks.EnsureAllowed(game, policy, index, BlockedOperation.Generate);
        var jobLock = Lock(folder);
        try
        {
            var job = GenerationJobStore.Read(folder);
            if (job.Game != game.Id)
            {
                throw new GenerationJobException(GenerationJobProblem.WrongState, $"The job in {folder} is for {job.Game}, not {game.Id}.");
            }

            var now = _time.GetUtcNow();
            if (job.Status == GenerationJobStatus.Running)
            {
                job = GenerationJobMachine.Apply(job, new GenerationTrigger.Interrupted(), now);
            }

            GenerationTrigger trigger = job.Status switch
            {
                GenerationJobStatus.Failed => new GenerationTrigger.Retried(),
                GenerationJobStatus.WaitingForReview => new GenerationTrigger.GlossaryApproved(),
                _ => new GenerationTrigger.Resumed(),
            };
            if (!GenerationJobMachine.CanApply(job, trigger))
            {
                throw new GenerationJobException(GenerationJobProblem.WrongState, $"The job {job.Id} is {job.Status.ToString().ToLowerInvariant()}: there is nothing to resume.");
            }

            job = GenerationJobMachine.Apply(job, trigger, now);
            GenerationJobStore.Write(folder, job);
            GenerationJobLog.Append(folder, _time, trigger is GenerationTrigger.GlossaryApproved ? "Glossary approved." : $"Resumed at {job.Stage} ({job.Counters.ChunksDone} of {job.Counters.Chunks} chunks done).");
            return Launch(new GenerationJobEntry(Path.GetFullPath(folder), job), jobLock, stages, cancellationToken);
        }
        catch
        {
            jobLock.Dispose();
            throw;
        }
    }

    /// <summary>Cancels a job that isn't running (paused, interrupted, failed, waiting); a running one is cancelled through its <see cref="GenerationRun"/>.</summary>
    /// <exception cref="GenerationJobException">Not found, unreadable, busy, or already cancelled / completed.</exception>
    /// <exception cref="IOException">job.json can't be written.</exception>
    public GenerationJob Cancel(string folder)
    {
        using var jobLock = Lock(folder);
        var job = GenerationJobStore.Read(folder);
        var now = _time.GetUtcNow();
        if (job.Status == GenerationJobStatus.Running)
        {
            job = GenerationJobMachine.Apply(job, new GenerationTrigger.Interrupted(), now);
        }

        if (!GenerationJobMachine.CanApply(job, new GenerationTrigger.Cancelled()))
        {
            throw new GenerationJobException(GenerationJobProblem.WrongState, $"The job {job.Id} is already {job.Status.ToString().ToLowerInvariant()}.");
        }

        job = GenerationJobMachine.Apply(job, new GenerationTrigger.Cancelled(), now);
        GenerationJobStore.Write(folder, job);
        GenerationJobLog.Append(folder, _time, "Cancelled by the player.");
        return job;
    }

    private static GenerationJobLock Lock(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!File.Exists(GenerationJobStore.JobFile(folder)))
        {
            throw new GenerationJobException(GenerationJobProblem.NotFound, $"No generation job in {folder}.");
        }

        return GenerationJobLock.TryAcquire(folder)
            ?? throw new GenerationJobException(GenerationJobProblem.Busy, $"The job in {folder} is running (in the app or another command).");
    }

    private GenerationRun Launch(GenerationJobEntry entry, GenerationJobLock jobLock, Func<GenerationJobEntry, GenerationStages> stages, CancellationToken cancellationToken)
    {
        var run = new GenerationRun(entry.Folder, entry.Job);
        GenerationStages handlers;
        try
        {
            handlers = stages(entry);
        }
        catch
        {
            jobLock.Dispose();
            throw;
        }

        run.Completion = Task.Run(
            async () =>
            {
                using (jobLock)
                {
                    return await runner.RunAsync(run, handlers, cancellationToken).ConfigureAwait(false);
                }
            },
            CancellationToken.None);
        return run;
    }
}
