using System.ComponentModel;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Folders;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Library;

/// <summary>What <see cref="GameSessions.LaunchAsync"/> did.</summary>
/// <param name="Problem">Why the game can't start on this machine; <c>null</c> when it was started or the OS refused.</param>
/// <param name="Executable">The executable <paramref name="Problem"/> is about (relative).</param>
/// <param name="Error">The OS's message when it refused to start the program.</param>
public sealed record GameLaunchResult(LaunchProblem? Problem = null, string? Executable = null, string? Error = null)
{
    public static GameLaunchResult Started { get; } = new();

    public bool Succeeded => Problem is null && Error is null;
}

/// <summary>
/// Launches games and follows them while they run (task 25): Steam games through <c>steam://rungameid</c>, manual
/// games by their main executable (<see cref="GameLaunchPlanner"/>), then a process poll every
/// <see cref="PollInterval"/> while a launched game is starting or running (<see cref="GameSessionTracker"/>).
/// Session start and end are logged with the duration; the start sets the game's last played time. Install (task 27)
/// asks <see cref="IsRunningAsync"/> before touching files; the rating prompt (task 63) listens to session ends.
/// </summary>
public sealed class GameSessions : IDisposable
{
    private readonly GameCatalog _catalog;
    private readonly IProcessList _processes;
    private readonly IProcessStarter _starter;
    private readonly OsEnvironment _env;
    private readonly TimeProvider _time;
    private readonly ILogger<GameSessions> _logger;
    private readonly GameSessionTracker _tracker;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _disposed = new();
    private bool _polling;

    public GameSessions(
        GameCatalog catalog,
        IProcessList processes,
        IProcessStarter starter,
        OsEnvironment env,
        TimeProvider time,
        ILogger<GameSessions> logger)
    {
        _catalog = catalog;
        _processes = processes;
        _starter = starter;
        _env = env;
        _time = time;
        _logger = logger;
        _tracker = new GameSessionTracker(env.Os);
    }

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>A game was launched, started, ended or didn't start in time. Raised on a pool thread.</summary>
    public event EventHandler<GameSessionEvent>? Changed;

    /// <summary>The launched game's session: starting or running; <c>null</c> when not launched from the app or ended.</summary>
    public GameSessionState? Find(GameId game)
    {
        lock (_lock)
        {
            return _tracker.Find(game);
        }
    }

    /// <summary>Starts <paramref name="game"/> (off the UI thread) and follows it. Never throws for disk or OS state.</summary>
    public async Task<GameLaunchResult> LaunchAsync(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (Find(game.Id) is not null)
        {
            return GameLaunchResult.Started;
        }

        var (plan, folder) = await Task.Run(() => (GameLaunchPlanner.Plan(game, _env.Os, Candidates(game)), PathNames.RealPath(game.InstallPath)));
        if (plan is CannotLaunch cannot)
        {
            _logger.LogWarning("Can't launch {Game} ({Name}) on {Os}: {Problem} {Executable}", game.Id, game.Name, _env.Os, cannot.Problem, cannot.Executable);
            return new GameLaunchResult(cannot.Problem, cannot.Executable);
        }

        try
        {
            switch (plan)
            {
                case OpenUrlLaunch url:
                    _starter.OpenUrl(url.Url);
                    break;
                case ProcessLaunch process:
                    _starter.Start(process.FileName, process.Arguments, process.WorkingDirectory);
                    break;
            }
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(e, "Can't launch {Game} ({Name}): {Plan}", game.Id, game.Name, plan.ToString());
            return new GameLaunchResult(Error: e.Message);
        }

        var at = _time.GetUtcNow();
        bool startPolling;
        lock (_lock)
        {
            if (!_tracker.Launched(game.Id, folder, at))
            {
                return GameLaunchResult.Started;
            }

            startPolling = !_polling;
            _polling = true;
        }

        _logger.LogInformation("Launched {Game} ({Name}): {Plan}", game.Id, game.Name, plan.ToString());
        Raise(new GameLaunched(game.Id, at));
        if (startPolling)
        {
            _ = Task.Run(PollAsync);
        }

        return GameLaunchResult.Started;
    }

    /// <summary>A process runs from inside the game folder right now (whether or not the app launched it).</summary>
    public Task<bool> IsRunningAsync(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return Task.Run(() => GameProcessMatcher.Find(_processes.Snapshot(), PathNames.RealPath(game.InstallPath), _env.Os).Count > 0);
    }

    public void Dispose()
    {
        _disposed.Cancel();
        _disposed.Dispose();
    }

    /// <summary>The main executable first, then the folder's other programs, each with its format read from disk.</summary>
    private static List<LaunchCandidate> Candidates(Game game) =>
        game.Source != GameSource.Manual || !Directory.Exists(game.InstallPath) ? []
        : [.. FolderRecognizer.ListExecutables(game.InstallPath, game.MainExecutable)
            .Select(exe => new LaunchCandidate(exe, ExecutableFormats.Detect(Path.Combine(game.InstallPath, exe.Replace('/', Path.DirectorySeparatorChar)))))];

    private async Task PollAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(PollInterval, _time);
            while (await timer.WaitForNextTickAsync(_disposed.Token))
            {
                var snapshot = _processes.Snapshot();
                IReadOnlyList<GameSessionEvent> events;
                bool done;
                lock (_lock)
                {
                    events = _tracker.Observe(snapshot, _time.GetUtcNow());
                    done = _tracker.IsEmpty;
                    _polling = !done;
                }

                foreach (var change in events)
                {
                    OnEvent(change);
                }

                if (done)
                {
                    return;
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

    private void OnEvent(GameSessionEvent change)
    {
        var name = _catalog.Find(change.Game)?.Name;
        switch (change)
        {
            case GameSessionStarted started:
                _logger.LogInformation("Session of {Game} ({Name}) started at {Start:O}", change.Game, name, started.StartedAt);
                _catalog.Update(change.Game, g => g with { LastPlayed = started.StartedAt });
                break;
            case GameSessionEnded ended:
                _logger.LogInformation("Session of {Game} ({Name}) ended at {End:O} after {Duration}", change.Game, name, ended.EndedAt,
                    TimeSpan.FromSeconds(Math.Round(ended.Duration.TotalSeconds)).ToString("c", System.Globalization.CultureInfo.InvariantCulture));
                break;
            case GameLaunchTimedOut:
                _logger.LogWarning("{Game} ({Name}) was launched but no process of it showed up", change.Game, name);
                break;
        }

        Raise(change);
    }

    private void Raise(GameSessionEvent change) => Changed?.Invoke(this, change);
}
