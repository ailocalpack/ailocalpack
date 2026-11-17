namespace AiLocalPack.Core.Generation;

/// <summary>How the Build stage's test run of the translation went (task 51).</summary>
public enum TestRunOutcome
{
    /// <summary>The game still ran when the watch time was up (it was then closed).</summary>
    Passed,

    /// <summary>The translated game closed within the watch time while the untranslated game kept running: the stage fails.</summary>
    Crashed,

    /// <summary>The game couldn't be tried here (<see cref="TestRunSkip"/>); the pack is saved untested.</summary>
    Skipped,
}

/// <summary>Why a test run was skipped, for the UI's wording.</summary>
public enum TestRunSkip
{
    /// <summary>No program in the game folder this app knows how to start.</summary>
    NoExecutable,

    /// <summary>The game's program is for another OS (a Windows game on macOS).</summary>
    OtherPlatform,

    /// <summary>The program is wrapped in Steam's DRM, which only starts it from Steam (the real game folder).</summary>
    SteamDrm,

    /// <summary>The OS refused to start the program.</summary>
    StartFailed,

    /// <summary>The untranslated game closes by itself as well (it needs its store running).</summary>
    ClosesUntranslated,

    /// <summary>Test runs are turned off.</summary>
    TurnedOff,

    /// <summary>The game is running already (the player's own session): a test run changes the live game folder, so it waits for it to close.</summary>
    GameRunning,

    /// <summary>Another change of game files (an install, an uninstall, another test run) held the lock.</summary>
    Busy,
}

/// <summary>Which picture of a test run a screenshot is.</summary>
public enum TestRunShotKind
{
    /// <summary>The untranslated game once it finished loading (the comparison).</summary>
    Original,

    /// <summary>The translated game once it finished loading (the one to check the translation on).</summary>
    Translated,

    /// <summary>The translated game's last picture: at the end of the watch, or just before it closed.</summary>
    Last,
}

/// <summary>A screenshot of a test run (PNG on this machine).</summary>
public sealed record TestRunShot(TestRunShotKind Kind, string Path);

/// <param name="Skip">Why it was skipped (<see cref="TestRunOutcome.Skipped"/> only).</param>
/// <param name="Detail">Plain English detail for logs and the CLI: the program started, the OS error, the last lines of its error output.</param>
/// <param name="Ran">How long the translated game ran before it closed or the watch ended (not for a skip before starting).</param>
/// <param name="ExitCode">The exit code of a game that closed by itself.</param>
/// <param name="Ready">When the translated game finished loading (a picture on its window and its disk reads calmed down); <c>null</c> = not seen.</param>
/// <param name="Watch">How long the translated game had to keep running: the untranslated game's loading time x 1.5 + 20 s, 30 s to 3 min.</param>
/// <param name="Shots">Screenshots: the original and the translation once loaded, the translation's last picture.</param>
/// <param name="CrashReport">What the OS recorded about the crash (Windows Application Error, a macOS crash report), first lines.</param>
public sealed record TestRunResult(
    TestRunOutcome Outcome,
    TestRunSkip? Skip = null,
    string? Detail = null,
    TimeSpan? Ran = null,
    int? ExitCode = null,
    TimeSpan? Ready = null,
    TimeSpan? Watch = null,
    IReadOnlyList<TestRunShot>? Shots = null,
    string? CrashReport = null)
{
    public static TestRunResult Passed(TimeSpan ran, string? detail = null) => new(TestRunOutcome.Passed, Detail: detail, Ran: ran);

    public static TestRunResult Skipped(TestRunSkip skip, string detail, TimeSpan? ran = null, int? exitCode = null) =>
        new(TestRunOutcome.Skipped, skip, detail, ran, exitCode);

    public static TestRunResult Crashed(TimeSpan ran, int exitCode, string? detail) => new(TestRunOutcome.Crashed, Detail: detail, Ran: ran, ExitCode: exitCode);

    /// <summary>The screenshot of that kind, if one was taken.</summary>
    public TestRunShot? Shot(TestRunShotKind kind) => Shots?.FirstOrDefault(s => s.Kind == kind);
}
