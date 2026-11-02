namespace AiLocalPack.Toolkit.Engines;

/// <summary>One engine layout found in a game folder.</summary>
/// <param name="Kind">Engine and variant.</param>
/// <param name="Confidence">How much of the layout was found.</param>
/// <param name="Marker">The file or folder that identified the engine, relative to the game folder with '/' separators.</param>
/// <param name="MainExecutable">
/// The game's main executable (Windows <c>.exe</c>, Linux binary, or macOS <c>.app</c> bundle), relative to the game folder
/// with '/' separators; <c>null</c> when none was found. With builds for several OSes the choice is the same on every
/// machine: Windows, then Linux, then macOS.
/// </param>
public sealed record EngineMatch(EngineKind Kind, EngineConfidence Confidence, string Marker, string? MainExecutable);

/// <summary>Result of <see cref="EngineDetector.Detect"/>.</summary>
/// <param name="Match">The engine, or <c>null</c> when nothing matched or the folder is ambiguous.</param>
/// <param name="Candidates">Every engine found, best first, one per engine.</param>
public sealed record EngineDetection(EngineMatch? Match, IReadOnlyList<EngineMatch> Candidates)
{
    public static EngineDetection None { get; } = new(null, []);

    /// <summary>The detected engine, <see cref="EngineKind.Unknown"/> when there is no single answer.</summary>
    public EngineKind Kind => Match?.Kind ?? EngineKind.Unknown;

    /// <summary>Several engines matched equally well (e.g. two games in one folder); <see cref="Match"/> is <c>null</c>.</summary>
    public bool IsAmbiguous => Match is null && Candidates.Count > 1;
}
