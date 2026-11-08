namespace AiLocalPack.Core.Activity;

/// <summary>Which heading a day of the feed gets.</summary>
public enum ActivityDayKind
{
    Today,
    Yesterday,

    /// <summary>Older: behind <i>Show N more</i>, headed by its date.</summary>
    Earlier,
}

/// <summary>One day of the feed, events newest first.</summary>
public sealed record ActivityDay(DateOnly Date, ActivityDayKind Kind, IReadOnlyList<ActivityEvent> Events);

/// <summary>The feed by day (design §5: <i>Today</i>, <i>Yesterday</i>, then older days behind <i>Show N more</i>).</summary>
public static class ActivityFeed
{
    /// <summary>
    /// Groups the events by the local calendar day of <paramref name="zone"/> (an event at 23:59 and one at 00:01 are on
    /// two days), newest day first. An event dated after <paramref name="now"/> (a clock set back) counts as today.
    /// </summary>
    public static IReadOnlyList<ActivityDay> ByDay(IEnumerable<ActivityEvent> events, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(zone);
        var today = LocalDay(now, zone);
        var yesterday = today.AddDays(-1);
        return [.. events
            .Select(e => (Event: e, Day: Min(LocalDay(e.Time, zone), today)))
            .GroupBy(x => x.Day)
            .OrderByDescending(g => g.Key)
            .Select(g => new ActivityDay(
                g.Key,
                g.Key == today ? ActivityDayKind.Today : g.Key == yesterday ? ActivityDayKind.Yesterday : ActivityDayKind.Earlier,
                [.. g.Select(x => x.Event).OrderByDescending(e => e.Time).ThenBy(e => e.Id, StringComparer.Ordinal)]))];
    }

    private static DateOnly LocalDay(DateTimeOffset time, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time, zone).DateTime);

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}
