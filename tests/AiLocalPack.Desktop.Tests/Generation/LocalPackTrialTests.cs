using AiLocalPack.Core.Games;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Launch;
using AiLocalPack.Desktop.Generation;

namespace AiLocalPack.Desktop.Tests.Generation;

/// <summary>When a game session counts as the player trying the installed local pack (task 55).</summary>
public sealed class LocalPackTrialTests
{
    private static readonly DateTimeOffset Installed = new(2026, 11, 9, 20, 0, 0, TimeSpan.Zero);
    private static readonly GameId Game = GameId.Steam(10);

    private static InstalledPack Record(bool local = true) =>
        new(Game, "ru", "claude-code", 1, Installed, "/games/Alpha", "20261109-200000-abcdefgh", [], []) { Local = local };

    private static GameSessionEnded Session(TimeSpan startedAfterInstall, GameId? game = null) =>
        new(game ?? Game, Installed + startedAfterInstall, Installed + startedAfterInstall + TimeSpan.FromMinutes(20));

    [Fact]
    public void Session_started_after_the_local_install_counts()
    {
        // Act
        var counts = LocalPackTrial.Counts(Record(), Session(TimeSpan.FromSeconds(30)));

        // Assert
        counts.Should().BeTrue();
    }

    [Fact]
    public void Session_started_before_the_install_does_not_count()
    {
        // Act
        var counts = LocalPackTrial.Counts(Record(), Session(TimeSpan.FromMinutes(-5)));

        // Assert
        counts.Should().BeFalse();
    }

    [Fact]
    public void Registry_pack_session_does_not_count()
    {
        // Act
        var counts = LocalPackTrial.Counts(Record(local: false), Session(TimeSpan.FromSeconds(30)));

        // Assert
        counts.Should().BeFalse();
    }

    [Fact]
    public void Nothing_installed_does_not_count()
    {
        // Act
        var counts = LocalPackTrial.Counts(null, Session(TimeSpan.FromSeconds(30)));

        // Assert
        counts.Should().BeFalse();
    }

    [Fact]
    public void Another_game_s_session_does_not_count()
    {
        // Act
        var counts = LocalPackTrial.Counts(Record(), Session(TimeSpan.FromSeconds(30), GameId.Steam(20)));

        // Assert
        counts.Should().BeFalse();
    }
}
