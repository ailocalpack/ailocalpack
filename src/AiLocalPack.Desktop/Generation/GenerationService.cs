using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Library;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Pipeline;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Generation;

/// <summary>Why <see cref="GenerationService.StartAsync"/> didn't start a job.</summary>
public enum GenerationStartProblem
{
    /// <summary>Anti-cheat or the studio's opt-out (<see cref="GameBlocks.EnsureAllowed"/>).</summary>
    Blocked,

    /// <summary>The game already has an open job (started elsewhere meanwhile, e.g. the CLI).</summary>
    AlreadyExists,

    /// <summary>The job folder couldn't be written.</summary>
    Disk,

    /// <summary>The stages couldn't be set up (the app's own path unknown, an unknown agent).</summary>
    Unexpected,
}

/// <summary>What <see cref="GenerationService.StartAsync"/> did: the job started, or why not (English detail for the log).</summary>
public sealed record GenerationStartResult(GenerationJob? Job, GenerationStartProblem? Problem = null, string? Detail = null)
{
    public bool Started => Job is not null;
}

/// <summary>Why a resume or a cancel didn't happen.</summary>
public enum GenerationActionProblem
{
    /// <summary>Anti-cheat or the studio's opt-out.</summary>
    Blocked,

    /// <summary>Another runner has the job (the CLI), or it is running here.</summary>
    Busy,

    /// <summary>The game has no open job (any more).</summary>
    NotFound,

    /// <summary>The game left the library or its folder is unavailable.</summary>
    GameUnavailable,

    /// <summary>The job folder couldn't be written or deleted.</summary>
    Disk,

    /// <summary>The stages couldn't be set up.</summary>
    Unexpected,
}

/// <summary>What a resume or a cancel did: the job as it is now, or why nothing happened (English detail for the log).</summary>
public sealed record GenerationActionResult(GenerationJob? Job, GenerationActionProblem? Problem = null, string? Detail = null)
{
    public bool Succeeded => Problem is null;
}

/// <summary>What the progress card shows besides the job's counters: <c>progress.json</c> and the end of the job's log.</summary>
public sealed record GenerationJobDetails(PipelineProgress Progress, IReadOnlyList<string> Log)
{
    public static GenerationJobDetails Empty { get; } = new(PipelineProgress.Empty, []);
}

/// <summary>The stage handlers of one job: the real pipeline (<see cref="PipelineStagesFactory"/>), or fakes in tests.</summary>
public interface IGenerationStagesFactory
{
    /// <exception cref="InvalidOperationException">The stages can't be set up (unknown agent, the app's own path unknown).</exception>
    GenerationStages Create(Game game, GenerationJobEntry entry);
}

/// <summary>
/// The app's generation jobs (tasks 52, 53): starts a job for a game through <see cref="GenerationJobs"/> with the
/// pipeline's stages, pauses, resumes (a paused, interrupted or failed job; the glossary approved) and cancels it (keeping
/// or deleting its folder), resumes jobs paused by the limit when it resets (<see cref="StartAutoResume"/>), keeps the
/// open jobs (read once from disk, then followed through the runs it starts) so the game page, the library and the tab
/// know a game's job, and tells them on every change. Disposing it (the app quits) stops the runs, which leaves their
/// jobs interrupted, resumable on the next start.
/// </summary>
public sealed class GenerationService : IDisposable
{
    private readonly GenerationJobs _jobs;
    private readonly AntiCheatPolicy _policy;
    private readonly PackIndex _packIndex;
    private readonly IGenerationStagesFactory _stages;
    private readonly GameCatalog _catalog;
    private readonly WorkingCopies _copies;
    private readonly TimeProvider _time;
    private readonly ILogger<GenerationService> _logger;
    private readonly CancellationTokenSource _quit = new();
    private readonly Lock _lock = new();
    private readonly Dictionary<GameId, GenerationJobEntry> _open = [];
    private readonly Dictionary<GameId, GenerationRun> _runs = [];
    private bool _loaded;
    private int _disposed;
    private int _autoResume;

    public GenerationService(
        GenerationJobStore store,
        AntiCheatPolicy policy,
        PackIndex packIndex,
        IGenerationStagesFactory stages,
        GameCatalog catalog,
        WorkingCopies copies,
        TimeProvider time,
        ILogger<GenerationService> logger,
        ILogger<GenerationRunner> runnerLogger)
    {
        ArgumentNullException.ThrowIfNull(store);
        _jobs = new GenerationJobs(store, new GenerationRunner(time, runnerLogger), time);
        _policy = policy;
        _packIndex = packIndex;
        _stages = stages;
        _catalog = catalog;
        _copies = copies;
        _time = time;
        _logger = logger;
    }

    /// <summary>How often <see cref="StartAutoResume"/> looks for jobs whose limit has reset.</summary>
    public static readonly TimeSpan AutoResumeInterval = TimeSpan.FromSeconds(30);

    /// <summary>Lines of the job's log the progress card's activity log shows.</summary>
    public const int LogLines = 200;

    /// <summary>Raised (on any thread) when a game's job started, moved on or ended.</summary>
    public event EventHandler<GenerationJob>? Changed;

    /// <summary>The game's open job (not cancelled or completed) as last saved, if any.</summary>
    public GenerationJob? FindOpen(GameId game)
    {
        ArgumentNullException.ThrowIfNull(game);
        EnsureLoaded();
        lock (_lock)
        {
            return _open.GetValueOrDefault(game)?.Job;
        }
    }

    /// <summary>The game's job state for the library and the page (<see cref="GenerationJobStates.Of"/>), if it has an open one.</summary>
    public GenerationJobState? StateOf(GameId game) => FindOpen(game) is { } job ? GenerationJobStates.Of(job) : null;

    /// <summary>
    /// Crash recovery at app start (<see cref="GenerationJobStore.Recover"/>): a job left running when the app died, with
    /// no runner here, becomes interrupted; the open jobs are read again and the pages told. Blocks; call it off the UI
    /// thread. Returns the interrupted jobs and the number of open ones.
    /// </summary>
    /// <exception cref="IOException">The work folder can't be read.</exception>
    public (IReadOnlyList<GenerationJobEntry> Interrupted, int Open) Recover()
    {
        HashSet<GameId> running;
        lock (_lock)
        {
            running = [.. _runs.Keys];
        }

        var interrupted = _jobs.Store.Recover().Where(e => !running.Contains(e.Job.Game)).ToList();
        Reload();
        foreach (var entry in interrupted)
        {
            Changed?.Invoke(this, FindOpen(entry.Job.Game) ?? entry.Job);
        }

        lock (_lock)
        {
            return (interrupted, _open.Count);
        }
    }

    /// <summary>The job folder of the game's open job, if any.</summary>
    public string? FolderOf(GameId game)
    {
        ArgumentNullException.ThrowIfNull(game);
        EnsureLoaded();
        lock (_lock)
        {
            return _open.GetValueOrDefault(game)?.Folder;
        }
    }

    /// <summary>The game's job runs in this app now (so it can be paused, not resumed or cancelled from disk).</summary>
    public bool IsRunning(GameId game)
    {
        lock (_lock)
        {
            return _runs.ContainsKey(game);
        }
    }

    /// <summary><c>progress.json</c> and the end of the log of the game's open job; empty when none. Reads the disk; never throws.</summary>
    public GenerationJobDetails ReadDetails(GameId game)
    {
        if (FolderOf(game) is not { } folder)
        {
            return GenerationJobDetails.Empty;
        }

        return new GenerationJobDetails(PipelineProgressFile.Read(new GenerationFiles(folder)), GenerationJobLog.Tail(folder, LogLines));
    }

    /// <summary>The player pauses the running job: its step stops and runs again on resume. Nothing when it doesn't run here.</summary>
    public void Pause(GameId game)
    {
        GenerationRun? run;
        lock (_lock)
        {
            run = _runs.GetValueOrDefault(game);
        }

        if (run is not null)
        {
            _logger.LogInformation("Generation job {Job} for {Game} paused by the player", run.Job.Id, game);
            run.Pause();
        }
    }

    /// <summary>
    /// Goes on with the game's job: a paused or interrupted one from its stage and chunk, a failed one from the failed
    /// stage, one waiting for review with the translation. Never throws.
    /// </summary>
    public async Task<GenerationActionResult> ResumeAsync(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (IsRunning(game.Id))
        {
            return new GenerationActionResult(FindOpen(game.Id), GenerationActionProblem.Busy, "The job is running.");
        }

        if (FolderOf(game.Id) is not { } folder)
        {
            return new GenerationActionResult(null, GenerationActionProblem.NotFound, "No open job.");
        }

        if (!game.IsAvailable)
        {
            return new GenerationActionResult(FindOpen(game.Id), GenerationActionProblem.GameUnavailable, "The game's folder is unavailable.");
        }

        GenerationRun run;
        try
        {
            run = await Task.Run(() => _jobs.Resume(folder, game, _policy, _packIndex.Current?.Index, entry => _stages.Create(game, entry), _quit.Token));
        }
        catch (GameBlockedException e)
        {
            _logger.LogWarning("Resuming the generation for {Game} refused: {Reason}", game.Id, e.Message);
            return new GenerationActionResult(FindOpen(game.Id), GenerationActionProblem.Blocked, e.Message);
        }
        catch (GenerationJobException e)
        {
            _logger.LogWarning("Resuming the generation for {Game} refused: {Reason}", game.Id, e.Message);
            Reload();
            var problem = e.Problem == GenerationJobProblem.Busy ? GenerationActionProblem.Busy : GenerationActionProblem.NotFound;
            return new GenerationActionResult(FindOpen(game.Id), problem, e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(e, "Couldn't resume the generation job for {Game}", game.Id);
            return new GenerationActionResult(FindOpen(game.Id), GenerationActionProblem.Disk, e.Message);
        }
        catch (InvalidOperationException e)
        {
            _logger.LogError(e, "Couldn't set up the generation stages for {Game}", game.Id);
            Reload();
            return new GenerationActionResult(FindOpen(game.Id), GenerationActionProblem.Unexpected, e.Message);
        }

        _logger.LogInformation("Generation job {Job} for {Game} resumed at {Stage}", run.Job.Id, game.Id, run.Job.Stage);
        Follow(run);
        return new GenerationActionResult(run.Job);
    }

    /// <summary>
    /// Cancels the game's job (a running one is stopped first) and, with <paramref name="deleteFolder"/>, deletes its
    /// folder with the working copy. Never throws.
    /// </summary>
    public async Task<GenerationActionResult> CancelAsync(GameId game, bool deleteFolder)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (FolderOf(game) is not { } folder)
        {
            return new GenerationActionResult(null, GenerationActionProblem.NotFound, "No open job.");
        }

        GenerationRun? run;
        lock (_lock)
        {
            run = _runs.GetValueOrDefault(game);
        }

        GenerationJob job;
        if (run is not null)
        {
            run.Cancel();
            job = await run.Completion.ConfigureAwait(false);
            if (job.IsOpen)
            {
                // It ended otherwise meanwhile (failed, the app quitting): cancel what was saved.
                return await CancelAsync(game, deleteFolder).ConfigureAwait(false);
            }
        }
        else
        {
            try
            {
                job = await Task.Run(() => _jobs.Cancel(folder)).ConfigureAwait(false);
            }
            catch (GenerationJobException e)
            {
                _logger.LogWarning("Cancelling the generation for {Game} refused: {Reason}", game, e.Message);
                Reload();
                return new GenerationActionResult(FindOpen(game), e.Problem == GenerationJobProblem.Busy ? GenerationActionProblem.Busy : GenerationActionProblem.NotFound, e.Message);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(e, "Couldn't cancel the generation job for {Game}", game);
                return new GenerationActionResult(FindOpen(game), GenerationActionProblem.Disk, e.Message);
            }

            Forget(job);
        }

        _logger.LogInformation("Generation job {Job} for {Game} cancelled", job.Id, game);
        if (!deleteFolder)
        {
            return new GenerationActionResult(job);
        }

        try
        {
            await Task.Run(() => _copies.Delete(folder)).ConfigureAwait(false);
            _logger.LogInformation("Generation job folder {Folder} deleted", folder);
            return new GenerationActionResult(job);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogError(e, "Couldn't delete the generation job folder {Folder}", folder);
            return new GenerationActionResult(job, GenerationActionProblem.Disk, e.Message);
        }
    }

    /// <summary>
    /// Resumes the jobs paused by the limit once it has reset, now and every <see cref="AutoResumeInterval"/> (time from
    /// the <see cref="TimeProvider"/>) until the service is disposed. Called once at app start.
    /// </summary>
    public void StartAutoResume()
    {
        if (Interlocked.Exchange(ref _autoResume, 1) == 1)
        {
            return;
        }

        _ = AutoResumeLoopAsync(_quit.Token);
    }

    /// <summary>One look for jobs whose limit has reset: resumes them (a game that left the library is skipped). Never throws.</summary>
    public Task<IReadOnlyList<GenerationJobEntry>> ResumeDueAsync()
    {
        var scheduler = new GenerationResumeScheduler(
            _time,
            () =>
            {
                EnsureLoaded();
                lock (_lock)
                {
                    return [.. _open.Values.Where(e => !_runs.ContainsKey(e.Job.Game))];
                }
            },
            async entry =>
            {
                var game = _catalog.Find(entry.Job.Game) ?? throw new InvalidOperationException($"{entry.Job.Game} isn't in the library.");
                var result = await ResumeAsync(game).ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException(result.Detail);
                }
            });
        return scheduler.TickAsync();
    }

    /// <summary>
    /// Creates the game's job and runs it in the background with the pipeline's stages. Never throws: a blocked game,
    /// an open job already, a folder that can't be written come back as the result's problem.
    /// </summary>
    public async Task<GenerationStartResult> StartAsync(Game game, string language, string agentId, GenerationOptions options, TimeSpan? estimatedTime)
    {
        ArgumentNullException.ThrowIfNull(game);
        GenerationRun run;
        try
        {
            run = await Task.Run(() => _jobs.Start(
                game,
                _policy,
                _packIndex.Current?.Index,
                language,
                agentId,
                options,
                entry => _stages.Create(game, entry),
                estimatedTime,
                _quit.Token));
        }
        catch (GameBlockedException e)
        {
            _logger.LogWarning("Generation for {Game} refused: {Reason}", game.Id, e.Message);
            return new GenerationStartResult(null, GenerationStartProblem.Blocked, e.Message);
        }
        catch (GenerationJobException e) when (e.Problem == GenerationJobProblem.AlreadyExists)
        {
            _logger.LogWarning("Generation for {Game} refused: {Reason}", game.Id, e.Message);
            Reload();
            return new GenerationStartResult(null, GenerationStartProblem.AlreadyExists, e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or GenerationJobException)
        {
            _logger.LogError(e, "Couldn't create the generation job for {Game}", game.Id);
            return new GenerationStartResult(null, GenerationStartProblem.Disk, e.Message);
        }
        catch (InvalidOperationException e)
        {
            _logger.LogError(e, "Couldn't set up the generation stages for {Game}", game.Id);
            Reload();
            return new GenerationStartResult(null, GenerationStartProblem.Unexpected, e.Message);
        }

        // Back on the caller's thread (the UI's in the app), so the first change reaches the pages from there.
        _logger.LogInformation("Generation job {Job} started for {Game} into {Language} with {Agent}", run.Job.Id, game.Id, language, agentId);
        Follow(run);
        return new GenerationStartResult(run.Job);
    }

    /// <summary>Stops the running jobs (they end interrupted) and waits a moment for them to save that.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _quit.Cancel();
        Task[] running;
        lock (_lock)
        {
            running = [.. _runs.Values.Select(r => (Task)r.Completion)];
        }

        try
        {
            Task.WaitAll(running, TimeSpan.FromSeconds(5));
        }
        catch (AggregateException e)
        {
            _logger.LogWarning(e, "A generation run failed while stopping");
        }

        _quit.Dispose();
    }

    private async Task AutoResumeLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Yield();
            await ResumeDueAsync().ConfigureAwait(false);
            using var timer = new PeriodicTimer(AutoResumeInterval, _time);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var resumed = await ResumeDueAsync().ConfigureAwait(false);
                foreach (var entry in resumed)
                {
                    _logger.LogInformation("Generation job {Job} for {Game} resumed after its limit reset", entry.Job.Id, entry.Job.Game);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>A job cancelled from disk (not by a run here) leaves the open jobs.</summary>
    private void Forget(GenerationJob job)
    {
        lock (_lock)
        {
            if (_open.GetValueOrDefault(job.Game)?.Job.Id == job.Id)
            {
                _open.Remove(job.Game);
            }
        }

        Changed?.Invoke(this, job);
    }

    private void Follow(GenerationRun run)
    {
        var game = run.Job.Game;
        lock (_lock)
        {
            _runs[game] = run;
            _open[game] = new GenerationJobEntry(run.Folder, run.Job);
        }

        run.Changed += (_, job) => Update(run, job);
        Changed?.Invoke(this, run.Job);
        _ = run.Completion.ContinueWith(
            t =>
            {
                lock (_lock)
                {
                    if (_runs.GetValueOrDefault(game) == run)
                    {
                        _runs.Remove(game);
                    }
                }

                if (t.IsCompletedSuccessfully && t.Result is { } last)
                {
                    Update(run, last);
                }
            },
            TaskScheduler.Default);
    }

    private void Update(GenerationRun run, GenerationJob job)
    {
        lock (_lock)
        {
            if (job.IsOpen)
            {
                _open[job.Game] = new GenerationJobEntry(run.Folder, job);
            }
            else if (_open.GetValueOrDefault(job.Game)?.Job.Id == job.Id)
            {
                _open.Remove(job.Game);
            }
        }

        Changed?.Invoke(this, job);
    }

    private void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_loaded)
            {
                return;
            }
        }

        Reload();
    }

    /// <summary>Reads the open jobs from disk again (a job the CLI made, a broken folder); a job this app runs keeps its live state.</summary>
    private void Reload()
    {
        var entries = _jobs.Store.List();
        lock (_lock)
        {
            _loaded = true;
            foreach (var game in _open.Keys.Where(g => !_runs.ContainsKey(g)).ToList())
            {
                _open.Remove(game);
            }

            foreach (var entry in entries.Where(e => e.Job.IsOpen && !_runs.ContainsKey(e.Job.Game)))
            {
                _open[entry.Job.Game] = entry;
            }
        }
    }
}
