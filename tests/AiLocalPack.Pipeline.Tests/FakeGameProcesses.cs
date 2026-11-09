using System.Runtime.CompilerServices;
using AiLocalPack.Agents.Processes;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>How a fake game process behaves: closes by itself with an exit code and error lines, or runs until killed.</summary>
internal sealed record FakeGame(int? ExitCode, IReadOnlyList<string> Errors)
{
    public static FakeGame KeepsRunning { get; } = new(null, []);

    public static FakeGame Closes(int exitCode, params string[] errors) => new(exitCode, errors);
}

/// <summary>A process runner for test runs: each start plays the next <see cref="FakeGame"/> and records the spec and what the copy held.</summary>
internal sealed class FakeGameProcesses(params FakeGame[] games) : IProcessRunner
{
    private readonly Queue<FakeGame> _games = new(games);

    public List<ProcessSpec> Started { get; } = [];

    /// <summary>Called with each spec as it starts (to look at the working copy during the run).</summary>
    public Action<ProcessSpec>? OnStart { get; init; }

    /// <summary>Throws <see cref="ProcessStartException"/> instead of starting.</summary>
    public bool FailToStart { get; init; }

    public IRunningProcess Start(ProcessSpec spec, CancellationToken cancellationToken = default)
    {
        if (FailToStart)
        {
            throw new ProcessStartException("Exec format error");
        }

        Started.Add(spec);
        OnStart?.Invoke(spec);
        return new Running(_games.Count > 0 ? _games.Dequeue() : FakeGame.KeepsRunning, cancellationToken);
    }

    private sealed class Running : IRunningProcess
    {
        private readonly FakeGame _game;
        private readonly TaskCompletionSource<ProcessExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;

        public Running(FakeGame game, CancellationToken cancellationToken)
        {
            _game = game;
            if (game.ExitCode is { } code)
            {
                _exit.TrySetResult(new ProcessExit(code, Killed: false));
            }

            _registration = cancellationToken.Register(Kill);
        }

        public int Id => 4242;

        public Task<ProcessExit> Exit => _exit.Task;

        public async IAsyncEnumerable<ProcessLine> ReadLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var line in _game.Errors)
            {
                yield return new ProcessLine(ProcessStream.StandardError, line);
            }

            await _exit.Task.ConfigureAwait(false);
        }

        public void Kill() => _exit.TrySetResult(new ProcessExit(137, Killed: true));

        public ValueTask DisposeAsync()
        {
            Kill();
            _registration.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
