namespace AiLocalPack.Core.Launch;

/// <summary>
/// When a game session with an AI translation installed reads as a crash (task 75, design §6e): the game closed within
/// <see cref="Threshold"/> of starting. Nobody plays a game for less than that on purpose, and a broken translation
/// usually stops it at the first loading screen. Pure.
/// </summary>
public static class GameCrashRule
{
    /// <summary>30 s: twice the Build stage's test run, so a game that passes it and then dies at its first load still counts.</summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(30);

    /// <summary>The session ended within <see cref="Threshold"/> of starting.</summary>
    public static bool IsCrash(GameSessionEnded ended)
    {
        ArgumentNullException.ThrowIfNull(ended);
        return ended.Duration < Threshold;
    }
}
