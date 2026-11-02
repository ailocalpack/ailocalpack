namespace AiLocalPack.TestKit;

/// <summary>
/// A <see cref="TimeProvider"/> whose time only moves when the test says so. Production code takes a
/// <see cref="TimeProvider"/> (default <see cref="TimeProvider.System"/>) instead of reading the clock directly.
/// Local time zone is UTC so results do not depend on the machine. Timers are not faked.
/// </summary>
public sealed class FakeClock : TimeProvider
{
    private readonly Lock _lock = new();
    private DateTimeOffset _now;

    /// <summary>Starts at <paramref name="start"/>, or 2026-01-01T00:00:00Z.</summary>
    public FakeClock(DateTimeOffset? start = null) => _now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now.ToUniversalTime();
        }
    }

    // Timestamps (elapsed-time measurements) follow the fake time too.
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    /// <summary>Moves time forward. Negative steps are rejected: time never goes back.</summary>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        lock (_lock)
        {
            _now += by;
        }
    }

    /// <summary>Sets the current time (may go back, to simulate a clock change).</summary>
    public void Set(DateTimeOffset now)
    {
        lock (_lock)
        {
            _now = now;
        }
    }
}
