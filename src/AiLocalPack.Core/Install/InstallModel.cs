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

    /// <summary>
    /// Whitelisted executables the pack needs (task 32), installed in the same transaction. One already installed for
    /// the game in the same version is shared (its files aren't written again, so they may be left out; the pack joins
    /// its users); re-apply writes the files given again.
    /// </summary>
    public IReadOnlyList<InstallDependency> Dependencies { get; init; } = [];

    /// <summary>A pack generated on this machine (task 55), not one from the registry: recorded with <see cref="Title"/>.</summary>
    public bool Local { get; init; }

    /// <summary>The pack's title, recorded for a local pack (the registry's list names the others).</summary>
    public string? Title { get; init; }
}

/// <summary>A whitelisted dependency to install with a pack: its unpacked files, laid out as in the game folder.</summary>
/// <param name="Id">The whitelist ID (<c>bepinex</c>).</param>
/// <param name="Files">Its files (paths unique, none also written by the pack); empty only for one installed already in the version.</param>
public sealed record InstallDependency(string Id, string Version, IReadOnlyList<InstallFile> Files)
{
    /// <summary>
    /// What it makes in the game folder when the game runs (a folder ends with <c>/</c>, <c>*</c> / <c>?</c> only in a
    /// file's last part); those not there before are deleted on uninstall.
    /// </summary>
    public IReadOnlyList<string> GeneratedFiles { get; init; } = [];
}

/// <summary>The files a pack or a dependency put into a game folder: what uninstall needs to undo it.</summary>
public interface IInstalledFiles
{
    /// <summary>The backup folder holding the originals (<c>&lt;data&gt;/backups/&lt;game folder&gt;/&lt;name&gt;</c>), shared by what one transaction installed.</summary>
    string Backup { get; }

    IReadOnlyList<InstalledFile> Files { get; }

    IReadOnlyList<string> AddedFolders { get; }

    IReadOnlyList<string> GeneratedFiles { get; }
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
    IReadOnlyList<string> AddedFolders) : IInstalledFiles
{
    /// <summary>Files the game makes from the pack's files (relative, <c>/</c>-separated), deleted on uninstall when present.</summary>
    public IReadOnlyList<string> GeneratedFiles { get; init; } = [];

    /// <summary>
    /// A pack generated on this machine (task 55: <c>LocalPackStore</c>), not one from the registry: re-apply reads it
    /// from the local packs, and a registry pack with the same ID is a different pack.
    /// </summary>
    public bool Local { get; init; }

    /// <summary>The title of a local pack (the registry's list names the others); <c>null</c> for a registry pack.</summary>
    public string? Title { get; init; }
}

/// <summary>
/// A whitelisted dependency installed into a game (task 32), shared by the packs in <see cref="UsedBy"/>: uninstalling
/// a pack removes it only when no other pack uses it. Stored next to the packs in <c>installed-packs.json</c> and
/// saved with them in one write.
/// </summary>
/// <param name="UsedBy">IDs of the game's packs that need it; empty only between the two halves of a pack replacement.</param>
public sealed record InstalledDependency(
    GameId Game,
    string Id,
    string Version,
    IReadOnlyList<string> UsedBy,
    DateTimeOffset InstalledAt,
    string InstallPath,
    string Backup,
    IReadOnlyList<InstalledFile> Files,
    IReadOnlyList<string> AddedFolders) : IInstalledFiles
{
    /// <summary>What it makes when the game runs (see <see cref="InstallDependency.GeneratedFiles"/>), not there before the install.</summary>
    public IReadOnlyList<string> GeneratedFiles { get; init; } = [];
}

/// <summary>A dependency (ID and version) a pack replacement keeps for the next pack.</summary>
public sealed record DependencyKey(string Id, string Version);

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

    /// <summary>
    /// The dependency is installed for the game in another version (used by another pack), or a dependency and the pack
    /// write the same file (<see cref="InstallFailure.File"/>).
    /// </summary>
    DependencyConflict,

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
    /// <summary>The game's dependency records the pack uses after the install.</summary>
    public IReadOnlyList<InstalledDependency> Dependencies { get; init; } = [];

    /// <summary>IDs of the dependencies that were installed already and are now shared (nothing written for them).</summary>
    public IReadOnlyList<string> SharedDependencies { get; init; } = [];

    public bool Succeeded => Installed is not null;

    public static InstallResult Fail(InstallFailure failure) => new(null, failure);
}

/// <summary>An uninstall (of a pack, or of dependencies no pack uses any more).</summary>
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
    public bool Succeeded => Failure is null;

    /// <summary>Dependencies removed with it: no other pack uses them.</summary>
    public IReadOnlyList<InstalledDependency> RemovedDependencies { get; init; } = [];

    /// <summary>Dependencies left installed: another pack uses them, or the next pack of a replacement will.</summary>
    public IReadOnlyList<InstalledDependency> KeptDependencies { get; init; } = [];

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
