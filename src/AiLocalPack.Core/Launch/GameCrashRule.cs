namespace AiLocalPack.Core.Launch;

/// <summary>
/// When a game session with an AI translation installed reads as a crash (task 75, design §6e): the game closed before
/// it could have been played: within <see cref="Threshold"/> of starting, or within the test run's watch of that game
/// (its untranslated game's loading time × 1.5 + 20 s) when that is longer, so a game that loads for a minute and dies
/// right after still counts. Nobody plays a game for less than that on purpose. Pure.
/// </summary>
public static class GameCrashRule
{
    /// <summary>30 s: the shortest window, for a game whose loading time was never measured.</summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(30);

    /// <summary>The window of a game: <paramref name="watch"/> (the test run's) when longer than <see cref="Threshold"/>.</summary>
    public static TimeSpan WindowFor(TimeSpan? watch) => watch is { } w && w > Threshold ? w : Threshold;

    /// <summary>The session ended within <see cref="WindowFor"/> of starting.</summary>
    public static bool IsCrash(GameSessionEnded ended, TimeSpan? watch = null)
    {
        ArgumentNullException.ThrowIfNull(ended);
        return ended.Duration < WindowFor(watch);
    }
}
