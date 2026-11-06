using AiLocalPack.Core.Games;

namespace AiLocalPack.Core.Install;

/// <summary>One file the install writes into the game folder.</summary>
/// <param name="Path">Relative to the game folder, <c>/</c>-separated, no <c>.</c> / <c>..</c> parts.</param>
/// <param name="Source">The new content: a file on disk (a rebuilt copy, task 28), read while installing.</param>
public sealed record InstallFile(string Path, string Source);

/// <summary>What to install: a pack's files for a game.</summary>
/// <param name="Game">The game, as the catalog has it (its folder, availability).</param>
/// <param name="Language">The pack's language tag.</param>
/// <param name="PackId">The pack's ID.</param>
/// <param name="Revision">The pack revision installed.</param>
/// <param name="Files">The files to write (each path once).</param>
public sealed record InstallRequest(Game Game, string Language, string PackId, int Revision, IReadOnlyList<InstallFile> Files)
{
    /// <summary>
    /// Files the game makes from the written ones when it runs (Ren'Py's <c>.rpyc</c> of a translation file); those not
    /// in the game folder before the install are recorded and deleted on uninstall.
    /// </summary>
    public IReadOnlyList<string> GeneratedFiles { get; init; } = [];
}

/// <summary>A game file the installed pack touched.</summary>
/// <param name="Path">Relative to the game folder, <c>/</c>-separated.</param>
/// <param name="OriginalSha256">The file before the pack (its copy is in the backup); <c>null</c> = the pack added it.</param>
/// <param name="InstalledSha256">The file the pack wrote.</param>
public sealed record InstalledFile(string Path, string? OriginalSha256, string InstalledSha256);

/// <summary>A pack installed into a game (one per game): what makes uninstall and the update re-check (task 33) possible.</summary>
/// <param name="Backup">The backup folder name under <c>&lt;data&gt;/backups/&lt;game folder&gt;/</c>.</param>
/// <param name="InstallPath">The game folder it was installed into.</param>
/// <param name="AddedFolders">Folders the pack created (relative, <c>/</c>-separated), removed on uninstall when empty.</param>
public sealed record InstalledPack(
    GameId Game,
    string Language,
    string PackId,
    int Revision,
    DateTimeOffset InstalledAt,
    string InstallPath,
    string Backup,
    IReadOnlyList<InstalledFile> Files,
    IReadOnlyList<string> AddedFolders)
{
    /// <summary>Files the game makes from the pack's files (relative, <c>/</c>-separated), deleted on uninstall when present.</summary>
    public IReadOnlyList<string> GeneratedFiles { get; init; } = [];
}

/// <summary>Why an install, re-apply or uninstall failed, as the player is told (the UI and CLI word it).</summary>
public enum InstallProblem
{
    /// <summary>The game is running (a process from its folder): close it and retry.</summary>
    GameRunning,

    /// <summary>Another program has the file open (Windows locks, a running executable on Linux).</summary>
    FileInUse,

    /// <summary>Not enough free space for the backup or the new files.</summary>
    DiskFull,

    /// <summary>The OS refused access (owner, permissions, protected folder).</summary>
    PermissionDenied,

    /// <summary>The file or the drive is read-only.</summary>
    ReadOnly,

    /// <summary>The game folder can't be reached (unplugged drive, removed folder).</summary>
    GameUnavailable,

    /// <summary>A pack is already installed for the game (uninstall it first, or re-apply it).</summary>
    AlreadyInstalled,

    /// <summary>Nothing is installed for the game.</summary>
    NotInstalled,

    /// <summary>Another install or uninstall is running (in the app or the CLI).</summary>
    Busy,

    /// <summary>An earlier install was interrupted and couldn't be finished yet; it is retried on every start.</summary>
    InterruptedInstall,

    /// <summary>A written file doesn't have the expected content.</summary>
    VerifyFailed,

    /// <summary>The backup of an original file is missing or damaged, so it can't be restored.</summary>
    BackupDamaged,

    /// <summary>Any other read or write error (its message is in <see cref="InstallFailure.Detail"/>).</summary>
    IoError,
}

/// <summary>A failed transaction.</summary>
/// <param name="File">The game file it failed on (relative), when it was one.</param>
/// <param name="Detail">The OS message, for logs and the details dialog.</param>
/// <param name="FilesRestored">
/// The game folder is as it was before (always for install; an uninstall that stopped midway restored some files and
/// can be retried). <c>false</c> also when restoring failed: the journal stays and the next start retries.
/// </param>
public sealed record InstallFailure(InstallProblem Problem, string? File = null, string? Detail = null, bool FilesRestored = true);

/// <summary>An install or re-apply: the record written, or why nothing was installed.</summary>
public sealed record InstallResult(InstalledPack? Installed, InstallFailure? Failure)
{
    public bool Succeeded => Installed is not null;

    public static InstallResult Fail(InstallFailure failure) => new(null, failure);
}

/// <summary>An uninstall.</summary>
/// <param name="Restored">Original files put back.</param>
/// <param name="Removed">Files the pack added, deleted.</param>
/// <param name="Kept">
/// Files changed since the install (a game update rewrote them): left as they are, since the backup is of an older
/// version.
/// </param>
public sealed record UninstallResult(
    InstalledPack? Uninstalled,
    IReadOnlyList<string> Restored,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Kept,
    InstallFailure? Failure)
{
    public bool Succeeded => Uninstalled is not null;

    public static UninstallResult Fail(InstallFailure failure) => new(null, [], [], [], failure);
}

/// <summary>What a transaction is doing, for a progress line.</summary>
public enum InstallStage
{
    /// <summary>Hashing the game's files and the new ones.</summary>
    Checking,

    /// <summary>Copying originals into the backup.</summary>
    BackingUp,

    /// <summary>Writing the new files.</summary>
    Writing,

    /// <summary>Hashing what was written.</summary>
    Verifying,

    /// <summary>Putting originals back (uninstall, rollback).</summary>
    Restoring,
}

/// <param name="Done">Files done in this stage.</param>
/// <param name="Total">Files in this stage.</param>
public sealed record InstallProgress(InstallStage Stage, int Done, int Total);

/// <summary>What the start-up recovery found (<see cref="PackInstaller.Recover"/>).</summary>
public enum RecoveryOutcome
{
    /// <summary>No interrupted transaction.</summary>
    Nothing,

    /// <summary>An install or re-apply stopped before it finished: the game files were put back as they were.</summary>
    RolledBack,

    /// <summary>An install had written and checked everything when it stopped: it was completed.</summary>
    Completed,

    /// <summary>An uninstall stopped midway: it was finished.</summary>
    UninstallFinished,

    /// <summary>Another install is running right now (in another process): left alone.</summary>
    Busy,

    /// <summary>Finishing failed (file in use…): the journal stays and the next start retries.</summary>
    Failed,
}

/// <param name="Game">The game of the interrupted transaction.</param>
/// <param name="Failure">Why finishing failed (<see cref="RecoveryOutcome.Failed"/>).</param>
public sealed record RecoveryResult(RecoveryOutcome Outcome, GameId? Game = null, string? PackId = null, InstallFailure? Failure = null);
