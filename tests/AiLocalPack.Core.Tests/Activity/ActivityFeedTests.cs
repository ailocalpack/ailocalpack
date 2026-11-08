using AiLocalPack.Core.Activity;

namespace AiLocalPack.Core.Tests.Activity;

/// <summary>The feed by day and the 🔔 badge (task 38).</summary>
public sealed class ActivityFeedTests
{
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.CreateCustomTimeZone("UTC+3", TimeSpan.FromHours(3), "UTC+3", "UTC+3");

    private static ActivityEvent At(string id, DateTimeOffset time) => ActivityEvent.Create(ActivityEventKind.PackInstalled, time) with { Id = id };

    private static IReadOnlyList<(ActivityDayKind Kind, DateOnly Date, string[] Ids)> Days(IReadOnlyList<ActivityDay> days) =>
        [.. days.Select(d => (d.Kind, d.Date, d.Events.Select(e => e.Id).ToArray()))];

    [Fact]
    public void Events_are_grouped_into_today_yesterday_and_earlier()
    {
        // Arrange
        var now = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        var events = new[]
        {
            At("today", now.AddHours(-1)),
            At("yesterday", now.AddDays(-1)),
            At("week", now.AddDays(-5)),
        };

        // Act
        var days = ActivityFeed.ByDay(events, now, TimeZoneInfo.Utc);

        // Assert
        Days(days).Should().BeEquivalentTo(new[]
        {
            (ActivityDayKind.Today, new DateOnly(2026, 11, 8), new[] { "today" }),
            (ActivityDayKind.Yesterday, new DateOnly(2026, 11, 7), new[] { "yesterday" }),
            (ActivityDayKind.Earlier, new DateOnly(2026, 11, 3), new[] { "week" }),
        }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Midnight_separates_two_days()
    {
        // Arrange
        var now = new DateTimeOffset(2026, 11, 8, 0, 0, 30, TimeSpan.Zero);
        var events = new[]
        {
            At("before", new DateTimeOffset(2026, 11, 7, 23, 59, 59, TimeSpan.Zero)),
            At("after", new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero)),
        };

        // Act
        var days = ActivityFeed.ByDay(events, now, TimeZoneInfo.Utc);

        // Assert
        Days(days).Select(d => (d.Kind, d.Ids.Single())).Should().Equal(
            (ActivityDayKind.Today, "after"),
            (ActivityDayKind.Yesterday, "before"));
    }

    [Fact]
    public void Days_follow_the_local_time_zone()
    {
        // Arrange: 22:30 UTC on the 7th is 01:30 on the 8th in UTC+3.
        var now = new DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);
        var events = new[] { At("night", new DateTimeOffset(2026, 11, 7, 22, 30, 0, TimeSpan.Zero)) };

        // Act
        var days = ActivityFeed.ByDay(events, now, Moscow);

        // Assert
        days.Single().Should().BeEquivalentTo(new { Kind = ActivityDayKind.Today, Date = new DateOnly(2026, 11, 8) }, "the event is after local midnight");
    }

    [Fact]
    public void Same_moment_in_utc_is_yesterday_there()
    {
        // Arrange
        var now = new DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);
        var events = new[] { At("night", new DateTimeOffset(2026, 11, 7, 22, 30, 0, TimeSpan.Zero)) };

        // Act
        var days = ActivityFeed.ByDay(events, now, TimeZoneInfo.Utc);

        // Assert
        days.Single().Kind.Should().Be(ActivityDayKind.Yesterday);
    }

    [Fact]
    public void Events_of_a_day_are_newest_first()
    {
        // Arrange
        var now = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        var events = new[] { At("morning", now.AddHours(-3)), At("noon", now), At("night", now.AddHours(-11)) };

        // Act
        var days = ActivityFeed.ByDay(events, now, TimeZoneInfo.Utc);

        // Assert
        days.Single().Events.Select(e => e.Id).Should().Equal("noon", "morning", "night");
    }

    [Fact]
    public void Event_after_now_counts_as_today()
    {
        // Arrange: the clock was set back since.
        var now = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        var events = new[] { At("future", now.AddDays(2)) };

        // Act
        var days = ActivityFeed.ByDay(events, now, TimeZoneInfo.Utc);

        // Assert
        days.Single().Kind.Should().Be(ActivityDayKind.Today);
    }

    [Theory]
    [InlineData(2, 1, 5, 2, ActivityBadgeTone.Attention)]
    [InlineData(0, 1, 2, 3, ActivityBadgeTone.Normal)]
    [InlineData(0, 2, 0, 2, ActivityBadgeTone.Normal)]
    [InlineData(0, 0, 0, 0, ActivityBadgeTone.None)]
    public void Badge_shows_decisions_first_else_running_and_unread(int decisions, int running, int unread, int count, ActivityBadgeTone tone)
    {
        // Act
        var badge = ActivityBadge.Of(decisions, running, unread);

        // Assert
        badge.Should().Be(new ActivityBadge(count, tone));
    }
}
