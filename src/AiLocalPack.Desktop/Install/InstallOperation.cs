using AiLocalPack.Core.Fonts;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Desktop.Install;

/// <summary>What the install service does to a game.</summary>
public enum InstallOperationKind
{
    /// <summary>Installs a registry pack (uninstalling the game's other pack first).</summary>
    Install,

    /// <summary>Applies the installed pack again (files reverted by a game update or a file check).</summary>
    Reapply,

    /// <summary>Restores the original files.</summary>
    Uninstall,
}

/// <summary>A step of an operation, shown as "step N of M" in the header's installed-pack block (design §4a).</summary>
public enum InstallStep
{
    /// <summary>Downloading the pack into the cache (instant when cached).</summary>
    Download,

    /// <summary>Uninstalling the pack it replaces.</summary>
    RemovePrevious,

    /// <summary>Comparing the game's files with the pack's builds.</summary>
    Check,

    /// <summary>Rebuilding the game files from the strings, on a copy (the recipe).</summary>
    Rebuild,

    /// <summary>Copying the originals into the backup.</summary>
    BackUp,

    /// <summary>Writing the new files.</summary>
    Write,

    /// <summary>Hashing what was written.</summary>
    Verify,

    /// <summary>Putting the original files back (uninstall).</summary>
    Restore,
}

/// <summary>The operation running now, for progress.</summary>
/// <param name="Title">The pack's title («…», or its ID when the registry doesn't list it).</param>
/// <param name="StepIndex">Index in <see cref="Steps"/>.</param>
/// <param name="Fraction">How far the current step is, 0–1 (0 when it can't tell).</param>
public sealed record InstallOperation(
    GameId Game,
    InstallOperationKind Kind,
    string PackId,
    string Title,
    IReadOnlyList<InstallStep> Steps,
    int StepIndex = 0,
    double Fraction = 0)
{
    public InstallStep Step => Steps[StepIndex];

    /// <summary>1-based, for "step N of M".</summary>
    public int StepNumber => StepIndex + 1;

    /// <summary>The whole operation, 0–100: finished steps plus the current one's fraction.</summary>
    public int Percent => (int)Math.Clamp(Math.Floor((StepIndex + Math.Clamp(Fraction, 0, 1)) * 100 / Steps.Count), 0, 100);
}

/// <summary>Why an operation stopped.</summary>
public enum InstallErrorKind
{
    /// <summary>Another install, re-apply or uninstall is running.</summary>
    Busy,

    /// <summary>The game is blocked (anti-cheat, opt-out): nothing was touched.</summary>
    Blocked,

    /// <summary>No pack list, or the pack (or the installed one, for re-apply) isn't in it.</summary>
    NotInRegistry,

    /// <summary>The pack couldn't be downloaded or failed its checks (<see cref="InstallError.Fetch"/>).</summary>
    Fetch,

    /// <summary>The pack doesn't fit the player's files (<see cref="InstallError.Compatibility"/>).</summary>
    Incompatible,

    /// <summary>A binary patch pack: installed from task 34 on.</summary>
    PatchUnsupported,

    /// <summary>The pack needs whitelisted executables: installed from task 32 on.</summary>
    DependenciesUnsupported,

    /// <summary>The downloaded string table doesn't read.</summary>
    InvalidStrings,

    /// <summary>The recipe couldn't rebuild a game file (<see cref="InstallError.File"/>, <see cref="InstallError.Detail"/>).</summary>
    Recipe,

    /// <summary>
    /// The game's font lacks the translation's letters and the whitelisted font that has them couldn't be had
    /// (<see cref="InstallError.Font"/>, <see cref="InstallError.FontName"/>; e.g. Noto Sans JP offline). Nothing was touched.
    /// </summary>
    Font,

    /// <summary>The transaction failed (<see cref="InstallError.Transaction"/>); the game files were restored unless it says otherwise.</summary>
    Transaction,

    /// <summary>A bug or an unforeseen error (<see cref="InstallError.Detail"/>); logged.</summary>
    Unexpected,
}

/// <summary>Why an operation failed, with what the plain reason is worded from.</summary>
public sealed record InstallError(
    InstallErrorKind Kind,
    string? File = null,
    string? Detail = null,
    PackFetchProblem Fetch = PackFetchProblem.None,
    PackCompatibility? Compatibility = null,
    InstallFailure? Transaction = null,
    IReadOnlyList<PackDependency>? Dependencies = null,
    FontFetchProblem Font = FontFetchProblem.None,
    string? FontName = null)
{
    /// <summary>The game folder is as it was before (always unless restoring itself failed; the next start retries).</summary>
    public bool FilesRestored => Transaction?.FilesRestored ?? true;

    /// <summary>A retry may succeed later (offline, game running, file in use…), so the failure offers <c>Retry</c>.</summary>
    public bool CanRetry => Kind switch
    {
        InstallErrorKind.Busy or InstallErrorKind.Transaction or InstallErrorKind.Unexpected or InstallErrorKind.Recipe => true,
        InstallErrorKind.Fetch => Fetch is PackFetchProblem.Offline or PackFetchProblem.Timeout or PackFetchProblem.HttpError
            or PackFetchProblem.HashMismatch or PackFetchProblem.DiskError,
        InstallErrorKind.Font => Font is not FontFetchProblem.FileMissing,
        _ => false,
    };
}

/// <summary>How an operation ended.</summary>
/// <param name="Log">What happened, line by line, in the UI language (the failure details dialog shows it).</param>
/// <param name="Installed">The record written (install, re-apply).</param>
/// <param name="Uninstall">What uninstall did (restored, removed, kept files).</param>
public sealed record InstallOutcome(
    InstallOperationKind Kind,
    Game Game,
    string PackId,
    string Title,
    InstallError? Error,
    IReadOnlyList<string> Log,
    InstalledPack? Installed = null,
    UninstallResult? Uninstall = null)
{
    public bool Succeeded => Error is null;
}
