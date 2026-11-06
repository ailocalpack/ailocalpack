using AiLocalPack.Core.Games;
using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Desktop.Tests.Library;

/// <summary>Launching games and following their sessions (task 25) with a fake process list and a fake clock.</summary>
public sealed class GameSessionsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly FakeClock _clock = new(Now);
    private readonly ServiceProvider _services;

    public GameSessionsTests() => _services = TestServices.Build(
        services => services.AddSingleton(sp => new GameSessions(
            sp.GetRequiredService<GameLibrary>().Catalog,
            sp.GetRequiredService<IProcessList>(),
            sp.GetRequiredService<IProcessStarter>(),
            sp.GetRequiredService<OsEnvironment>(),
            _clock,
            NullLogger<GameSessions>.Instance) { PollInterval = TimeSpan.FromMilliseconds(10) }),
        steam: s => s.AddGame(10, "Alpha"));

    public static bool HostIsWindows => OperatingSystem.IsWindows();

    public void Dispose() => _services.Dispose();

    private GameSessions Sessions => _services.GetRequiredService<GameSessions>();

    private FakeProcesses Processes => _services.GetRequiredService<FakeProcesses>();

    private GameCatalog Catalog => _services.GetRequiredService<GameLibrary>().Catalog;

    private Game Alpha => Catalog.Find(GameId.Steam(10))!;

    /// <summary>A process of the game as the OS reports it: under the folder with symlinks resolved.</summary>
    private string ProcessOf(Game game) => Path.Combine(PathNames.RealPath(game.InstallPath), "Alpha.exe");

    private Task<T> Next<T>() where T : GameSessionEvent
    {
        var raised = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Sessions.Changed += (_, e) =>
        {
            if (e is T match)
            {
                raised.TrySetResult(match);
            }
        };
        return raised.Task.WaitAsync(Wait);
    }

    private async Task StartAlphaAsync()
    {
        var started = Next<GameSessionStarted>();
        await Sessions.LaunchAsync(Alpha);
        Processes.Run(ProcessOf(Alpha));
        await started;
    }

    [Fact]
    public async Task Steam_game_launches_through_steam()
    {
        // Act
        await Sessions.LaunchAsync(Alpha);

        // Assert
        Processes.OpenedUrls.Should().Equal(new Uri("steam://rungameid/10"));
    }

    [Fact]
    public async Task Launched_game_is_starting()
    {
        // Act
        await Sessions.LaunchAsync(Alpha);

        // Assert
        Sessions.Find(Alpha.Id)!.Phase.Should().Be(GameSessionPhase.Starting);
    }

    [Fact]
    public async Task Game_launched_while_starting_is_not_launched_again()
    {
        // Arrange
        await Sessions.LaunchAsync(Alpha);

        // Act
        await Sessions.LaunchAsync(Alpha);

        // Assert
        Processes.OpenedUrls.Should().HaveCount(1);
    }

    [Fact]
    public async Task Process_in_the_game_folder_starts_the_session()
    {
        // Arrange
        var started = Next<GameSessionStarted>();
        await Sessions.LaunchAsync(Alpha);

        // Act
        Processes.Run(ProcessOf(Alpha));

        // Assert
        (await started).Should().Be(new GameSessionStarted(Alpha.Id, Now));
    }

    [Fact]
    public async Task Session_start_sets_last_played()
    {
        // Act
        await StartAlphaAsync();

        // Assert
        Alpha.LastPlayed.Should().Be(Now);
    }

    [Fact]
    public async Task Session_ends_after_the_process_exits()
    {
        // Arrange
        await StartAlphaAsync();
        var ended = Next<GameSessionEnded>();

        // Act
        Processes.ExitAll();
        _clock.Advance(TimeSpan.FromSeconds(11));

        // Assert
        (await ended).Should().Be(new GameSessionEnded(Alpha.Id, Now, Now));
    }

    [Fact]
    public async Task Refused_start_names_the_os_error()
    {
        // Arrange
        Processes.Refuse = new System.ComponentModel.Win32Exception("No application is associated with steam://");

        // Act
        var result = await Sessions.LaunchAsync(Alpha);

        // Assert
        (result.Error, Sessions.Find(Alpha.Id)).Should().Be(("No application is associated with steam://", null));
    }

    [Fact]
    public async Task Unavailable_game_is_not_launched()
    {
        // Act
        var result = await Sessions.LaunchAsync(Alpha with { IsAvailable = false });

        // Assert
        (result.Problem, Processes.OpenedUrls.Count).Should().Be((LaunchProblem.GameUnavailable, 0));
    }

    [Fact(SkipWhen = nameof(HostIsWindows), Skip = "Shell scripts don't run on Windows")]
    public async Task Manual_game_starts_its_main_script()
    {
        // Arrange
        var folder = _services.GetRequiredService<TempDir>().File("games/Moss/start.sh", "#!/bin/sh\n").Combine("games/Moss");
        var game = new Game(GameId.Fingerprint(new string('b', 64)), "Moss", GameSource.Manual, folder, null, null, "unknown", "none", null, true, "start.sh");

        // Act
        await Sessions.LaunchAsync(game);

        // Assert
        Processes.Started.Should().ContainSingle().Which.Should().BeEquivalentTo((GameLaunchPlanner.Shell, new[] { Path.Combine(folder, "start.sh") }, folder));
    }

    [Fact]
    public async Task Running_game_is_found_whoever_started_it()
    {
        // Arrange
        Processes.Run(ProcessOf(Alpha));

        // Act
        var running = await Sessions.IsRunningAsync(Alpha);

        // Assert
        running.Should().BeTrue();
    }

    [Fact]
    public async Task Game_without_a_process_is_not_running()
    {
        // Arrange
        Processes.Run("/usr/bin/steam");

        // Act
        var running = await Sessions.IsRunningAsync(Alpha);

        // Assert
        running.Should().BeFalse();
    }
}
