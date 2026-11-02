namespace AiLocalPack.TestKit.Tests;

public sealed class FakeClockTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Time_stands_still_until_advanced()
    {
        // Arrange
        var clock = new FakeClock(Start);
        var before = clock.GetUtcNow();

        // Act
        clock.Advance(TimeSpan.FromMinutes(5));

        // Assert
        before.Should().Be(Start);
        clock.GetUtcNow().Should().Be(Start.AddMinutes(5));
    }

    [Fact]
    public void Elapsed_time_follows_the_fake_clock()
    {
        // Arrange
        var clock = new FakeClock(Start);
        var before = clock.GetTimestamp();

        // Act
        clock.Advance(TimeSpan.FromSeconds(90));

        // Assert
        clock.GetElapsedTime(before).Should().Be(TimeSpan.FromSeconds(90));
    }

    [Fact]
    public void Advance_refuses_to_go_back()
    {
        // Arrange
        var clock = new FakeClock(Start);

        // Act
        var act = () => clock.Advance(TimeSpan.FromSeconds(-1));

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
        clock.GetUtcNow().Should().Be(Start);
    }

    [Fact]
    public void Set_can_go_back()
    {
        // Arrange
        var clock = new FakeClock(Start);

        // Act
        clock.Set(Start.AddDays(-1));

        // Assert
        clock.GetUtcNow().Should().Be(Start.AddDays(-1));
    }

    [Fact]
    public void Local_time_is_utc()
    {
        // Arrange
        var clock = new FakeClock(Start);

        // Act
        var local = clock.GetLocalNow();

        // Assert
        local.Offset.Should().Be(TimeSpan.Zero);
    }
}
