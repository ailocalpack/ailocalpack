using AiLocalPack.Core.Games;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Launch;

/// <summary>Where a launched game is: waiting for its process to show up, or running.</summary>
public enum GameSessionPhase
{
    Starting,
    Running,
}

/// <summary>A launched game the tracker follows.</summary>
/// <param name="StartedAt">When its process was first seen; <c>null</c> while starting.</param>
public sealed record GameSessionState(GameId Game, string Folder, GameSessionPhase Phase, DateTimeOffset LaunchedAt, DateTimeOffset? StartedAt);

/// <summary>Something that happened to a launched game.</summary>
public abstract record GameSessionEvent(GameId Game);

/// <summary>The game was launched; its process hasn't shown up yet.</summary>
public sealed record GameLaunched(GameId Game, DateTimeOffset LaunchedAt) : GameSessionEvent(Game);

/// <summary>A process of the game showed up.</summary>
public sealed record GameSessionStarted(GameId Game, DateTimeOffset StartedAt) : GameSessionEvent(Game);

/// <summary>The game's last process ended (seen last at <paramref name="EndedAt"/>).</summary>
public sealed record GameSessionEnded(GameId Game, DateTimeOffset StartedAt, DateTimeOffset EndedAt) : GameSessionEvent(Game)
{
    public TimeSpan Duration => EndedAt - StartedAt;
}

/// <summary>No process of the game showed up in time after the launch (Steam closed a dialog, the game failed at once).</summary>
public sealed record GameLaunchTimedOut(GameId Game, DateTimeOffset LaunchedAt) : GameSessionEvent(Game);

/// <summary>
/// Follows launched games through process snapshots: starting → running (a process inside the game folder seen) →
/// ended (none seen for <see cref="EndGrace"/>, so a launcher handing over to the game isn't the end), or timed out
/// when nothing showed up within <see cref="StartTimeout"/>. Pure: the caller polls the process list and passes the
/// time. Not thread-safe.
/// </summary>
public sealed class GameSessionTracker(OsKind os, TimeSpan? startTimeout = null, TimeSpan? endGrace = null)
{
    private readonly Dictionary<GameId, Entry> _games = [];

    /// <summary>Steam may update the game or show a dialog first: 3 minutes by default.</summary>
    public TimeSpan StartTimeout { get; } = startTimeout ?? TimeSpan.FromMinutes(3);

    public TimeSpan EndGrace { get; } = endGrace ?? TimeSpan.FromSeconds(10);

    /// <summary>Games launched and not ended yet.</summary>
    public IReadOnlyList<GameSessionState> Sessions => [.. _games.Values.Select(e => e.State)];

    public bool IsEmpty => _games.Count == 0;

    public GameSessionState? Find(GameId game) => _games.TryGetValue(game, out var entry) ? entry.State : null;

    /// <summary>
    /// Starts following <paramref name="game"/> launched at <paramref name="at"/> from <paramref name="folder"/>
    /// (absolute, symlinks resolved). False when it is followed already (starting or running).
    /// </summary>
    public bool Launched(GameId game, string folder, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (_games.ContainsKey(game))
        {
            return false;
        }

        _games[game] = new Entry(new GameSessionState(game, folder, GameSessionPhase.Starting, at, null), at);
        return true;
    }

    /// <summary>Moves every followed game on by the processes running at <paramref name="now"/>.</summary>
    public IReadOnlyList<GameSessionEvent> Observe(IReadOnlyList<RunningProcess> processes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(processes);
        var events = new List<GameSessionEvent>();
        foreach (var (id, entry) in _games.ToList())
        {
            var state = entry.State;
            var seen = processes.Any(p => GameProcessMatcher.IsInside(p.Path, state.Folder, os));
            if (state.Phase == GameSessionPhase.Starting)
            {
                if (seen)
                {
                    _games[id] = new Entry(state with { Phase = GameSessionPhase.Running, StartedAt = now }, now);
                    events.Add(new GameSessionStarted(id, now));
                }
                else if (now - state.LaunchedAt >= StartTimeout)
                {
                    _games.Remove(id);
                    events.Add(new GameLaunchTimedOut(id, state.LaunchedAt));
                }
            }
            else if (seen)
            {
                _games[id] = entry with { LastSeen = now };
            }
            else if (now - entry.LastSeen >= EndGrace)
            {
                _games.Remove(id);
                events.Add(new GameSessionEnded(id, state.StartedAt!.Value, entry.LastSeen));
            }
        }

        return events;
    }

    private sealed record Entry(GameSessionState State, DateTimeOffset LastSeen);
}
