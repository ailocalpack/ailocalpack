using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Library;
using AiLocalPack.Desktop.Registry;
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

/// <summary>The stage handlers of one job: the real pipeline (<see cref="PipelineStagesFactory"/>), or fakes in tests.</summary>
public interface IGenerationStagesFactory
{
    /// <exception cref="InvalidOperationException">The stages can't be set up (unknown agent, the app's own path unknown).</exception>
    GenerationStages Create(Game game, GenerationJobEntry entry);
}

/// <summary>
/// The app's generation jobs (task 52): starts a job for a game through <see cref="GenerationJobs"/> with the pipeline's
/// stages, keeps the open jobs (read once from disk, then followed through the runs it starts) so the game page, the
/// library and the tab know a game's job, and tells them on every change. Disposing it (the app quits) stops the runs,
/// which leaves their jobs interrupted, resumable on the next start.
/// </summary>
public sealed class GenerationService : IDisposable
{
    private readonly GenerationJobs _jobs;
    private readonly AntiCheatPolicy _policy;
    private readonly PackIndex _packIndex;
    private readonly IGenerationStagesFactory _stages;
    private readonly ILogger<GenerationService> _logger;
    private readonly CancellationTokenSource _quit = new();
    private readonly Lock _lock = new();
    private readonly Dictionary<GameId, GenerationJobEntry> _open = [];
    private readonly Dictionary<GameId, GenerationRun> _runs = [];
    private bool _loaded;
    private int _disposed;

    public GenerationService(
        GenerationJobStore store,
        AntiCheatPolicy policy,
        PackIndex packIndex,
        IGenerationStagesFactory stages,
        TimeProvider time,
        ILogger<GenerationService> logger,
        ILogger<GenerationRunner> runnerLogger)
    {
        ArgumentNullException.ThrowIfNull(store);
        _jobs = new GenerationJobs(store, new GenerationRunner(time, runnerLogger), time);
        _policy = policy;
        _packIndex = packIndex;
        _stages = stages;
        _logger = logger;
    }

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
