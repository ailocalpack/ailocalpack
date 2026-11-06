using AiLocalPack.Core.Games;
using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Launch;

public sealed class GameSessionTrackerTests
{
    private const string Folder = "/games/Lantern";
    private static readonly GameId Id = GameId.Steam(10);
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);
    private static readonly RunningProcess[] Running = [new(42, "/games/Lantern/Lantern.x86_64")];

    private static GameSessionTracker Launched()
    {
        var tracker = new GameSessionTracker(OsKind.Linux, TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(10));
        tracker.Launched(Id, Folder, T0);
        return tracker;
    }

    [Fact]
    public void Launched_game_is_starting()
    {
        // Act
        var tracker = Launched();

        // Assert
        tracker.Find(Id).Should().Be(new GameSessionState(Id, Folder, GameSessionPhase.Starting, T0, null));
    }

    [Fact]
    public void Game_launched_twice_is_followed_once()
    {
        // Arrange
        var tracker = Launched();

        // Act
        var again = tracker.Launched(Id, Folder, T0.AddSeconds(5));

        // Assert
        again.Should().BeFalse();
    }

    [Fact]
    public void Process_showing_up_starts_the_session()
    {
        // Arrange
        var tracker = Launched();

        // Act
        var events = tracker.Observe(Running, T0.AddSeconds(4));

        // Assert
        events.Should().Equal(new GameSessionStarted(Id, T0.AddSeconds(4)));
    }

    [Fact]
    public void Started_game_is_running()
    {
        // Arrange
        var tracker = Launched();

        // Act
        tracker.Observe(Running, T0.AddSeconds(4));

        // Assert
        tracker.Find(Id)!.Phase.Should().Be(GameSessionPhase.Running);
    }

    [Fact]
    public void Process_of_another_folder_does_not_start_the_session()
    {
        // Arrange
        var tracker = Launched();

        // Act
        var events = tracker.Observe([new RunningProcess(7, "/games/Other/Other")], T0.AddSeconds(4));

        // Assert
        events.Should().BeEmpty();
    }

    [Fact]
    public void Nothing_showing_up_in_time_times_the_launch_out()
    {
        // Arrange
        var tracker = Launched();

        // Act
        var events = tracker.Observe([], T0.AddMinutes(3));

        // Assert
        events.Should().Equal(new GameLaunchTimedOut(Id, T0));
    }

    [Fact]
    public void Timed_out_game_is_no_longer_followed()
    {
        // Arrange
        var tracker = Launched();

        // Act
        tracker.Observe([], T0.AddMinutes(3));

        // Assert
        tracker.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Short_gap_between_processes_does_not_end_the_session()
    {
        // Arrange: the launcher exits and the game takes over a few seconds later.
        var tracker = Launched();
        tracker.Observe(Running, T0.AddSeconds(2));
        tracker.Observe([], T0.AddSeconds(4));

        // Act
        var events = tracker.Observe([new RunningProcess(43, "/games/Lantern/bin/Game")], T0.AddSeconds(8));

        // Assert
        events.Should().BeEmpty();
    }

    [Fact]
    public void Session_ends_once_no_process_was_seen_for_the_grace_time()
    {
        // Arrange
        var tracker = Launched();
        tracker.Observe(Running, T0.AddSeconds(2));
        tracker.Observe(Running, T0.AddMinutes(30));

        // Act
        var events = tracker.Observe([], T0.AddMinutes(30).AddSeconds(10));

        // Assert
        events.Should().Equal(new GameSessionEnded(Id, T0.AddSeconds(2), T0.AddMinutes(30)));
    }

    [Fact]
    public void Session_duration_runs_from_start_to_the_last_sighting()
    {
        // Arrange
        var ended = new GameSessionEnded(Id, T0.AddSeconds(2), T0.AddMinutes(30).AddSeconds(2));

        // Act
        var duration = ended.Duration;

        // Assert
        duration.Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Ended_game_is_no_longer_followed()
    {
        // Arrange
        var tracker = Launched();
        tracker.Observe(Running, T0.AddSeconds(2));

        // Act
        tracker.Observe([], T0.AddSeconds(20));

        // Assert
        tracker.Find(Id).Should().BeNull();
    }
}
