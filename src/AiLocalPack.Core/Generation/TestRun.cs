namespace AiLocalPack.Core.Generation;

/// <summary>How the Build stage's test run of the translated working copy went (task 51).</summary>
public enum TestRunOutcome
{
    /// <summary>The game still ran when the watch time was up (it was then closed).</summary>
    Passed,

    /// <summary>The translated game closed within the watch time while the untranslated copy kept running: the stage fails.</summary>
    Crashed,

    /// <summary>The game couldn't be tried here (<see cref="TestRunSkip"/>); the pack is saved untested.</summary>
    Skipped,
}

/// <summary>Why a test run was skipped, for the UI's wording.</summary>
public enum TestRunSkip
{
    /// <summary>No program in the working copy this app knows how to start.</summary>
    NoExecutable,

    /// <summary>The game's program is for another OS (a Windows game on macOS).</summary>
    OtherPlatform,

    /// <summary>The program is wrapped in Steam's DRM, which only starts it from Steam (the real game folder).</summary>
    SteamDrm,

    /// <summary>The OS refused to start the program.</summary>
    StartFailed,

    /// <summary>The untranslated copy closes by itself as well (needs its store, or movies and audio left out of the copy).</summary>
    ClosesUntranslated,

    /// <summary>Test runs are turned off.</summary>
    TurnedOff,
}

/// <param name="Skip">Why it was skipped (<see cref="TestRunOutcome.Skipped"/> only).</param>
/// <param name="Detail">Plain English detail for logs and the CLI: the program started, the OS error, the last lines of its error output.</param>
/// <param name="Ran">How long the translated game ran before it closed or the watch ended (not for a skip before starting).</param>
/// <param name="ExitCode">The exit code of a game that closed by itself.</param>
public sealed record TestRunResult(TestRunOutcome Outcome, TestRunSkip? Skip = null, string? Detail = null, TimeSpan? Ran = null, int? ExitCode = null)
{
    public static TestRunResult Passed(TimeSpan ran, string? detail = null) => new(TestRunOutcome.Passed, Detail: detail, Ran: ran);

    public static TestRunResult Skipped(TestRunSkip skip, string detail, TimeSpan? ran = null, int? exitCode = null) =>
        new(TestRunOutcome.Skipped, skip, detail, ran, exitCode);

    public static TestRunResult Crashed(TimeSpan ran, int exitCode, string? detail) => new(TestRunOutcome.Crashed, Detail: detail, Ran: ran, ExitCode: exitCode);
}
