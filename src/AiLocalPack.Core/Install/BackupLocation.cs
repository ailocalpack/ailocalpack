using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Core.Install;

/// <summary>Why moving the backups was refused or failed (<see cref="BackupLocation.Move"/>).</summary>
public enum BackupMoveProblem
{
    /// <summary>An install, uninstall or cache clearing runs (the install lock is taken).</summary>
    Busy,

    /// <summary>An install transaction or an earlier move a crash left behind couldn't be finished.</summary>
    Interrupted,

    /// <summary>The backups are already in that folder.</summary>
    SameFolder,

    /// <summary>The new folder is inside the backups, or the backups inside it.</summary>
    Nested,

    /// <summary>The new folder already holds files.</summary>
    NotEmpty,

    /// <summary>Not enough free space on the new folder's drive.</summary>
    DiskFull,

    /// <summary>The new folder can't be written (permissions, read-only drive).</summary>
    Denied,

    /// <summary>A copied file didn't read back the same.</summary>
    VerifyFailed,

    /// <summary>The location file was written by a newer app: left as it is.</summary>
    NewerFormat,

    /// <summary>Another disk error (drive gone, file in use).</summary>
    IoError,
}

/// <summary>Bytes of the backups moved so far.</summary>
public sealed record BackupMoveProgress(long BytesDone, long BytesTotal);

/// <summary>
/// How a move ended. Succeeded: the backups are in <see cref="Root"/>; <see cref="LeftBehind"/> = the old folder that
/// couldn't be deleted yet (deleted on a later start or transaction). Failed: <see cref="Problem"/> with the OS message,
/// the backups stay where they were and nothing is left in the new folder.
/// </summary>
public sealed record BackupMoveResult(string Root, BackupMoveProblem? Problem = null, string? Detail = null, string? LeftBehind = null)
{
    public bool Succeeded => Problem is null;
}

/// <summary>
/// Where the backups of installed packs live (task 66) and moving them elsewhere as one transaction. The folder in use
/// is <c>&lt;data&gt;/backups-location.json</c>'s <c>path</c> (missing: <see cref="AppPaths.Backups"/>); install records
/// name their backup folder relative to it, so a move changes nothing else. A move takes the install lock, writes the
/// journal into the same file before anything is copied (<c>move: {from, to, phase: copy}</c>), renames the folder on the
/// same drive or copies and hash-checks every file to another, then switches <c>path</c> to the new folder in one atomic
/// write (<c>phase: cleanup</c>, the commit point) and deletes the old one. A failure before the commit deletes what was
/// copied and keeps the old folder; a crash is finished by <see cref="Recover"/> (start-up and before every install
/// transaction): a copy is rolled back (or rolled forward when the rename had happened), a clean-up finished. The new
/// folder is the picked one when it is named <see cref="FolderName"/>, else <see cref="FolderName"/> inside it, so the
/// backups never mix with the player's own files. Methods block: call them off the UI thread.
/// </summary>
public sealed class BackupLocation
{
    /// <summary>The name of the folder the backups go into inside the folder the player picks.</summary>
    public const string FolderName = "ailocalpack-backups";

    public const int CurrentSchemaVersion = 1;

    /// <summary>Kept free on the new drive beyond the backups' size.</summary>
    public const long Margin = 64L * 1024 * 1024;

    private const string PhaseCopy = "copy";
    private const string PhaseCleanup = "cleanup";

    private readonly AppPaths _paths;
    private readonly IDiskSpace _disk;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public BackupLocation(AppPaths paths, IDiskSpace? disk = null, TimeProvider? time = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _disk = disk ?? new SystemDiskSpace();
        _time = time ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Tests: called before each file is copied (relative path); an exception thrown here fails the move there.</summary>
    internal Action<string>? BeforeFile { get; set; }

    /// <summary>Tests: <c>false</c> lets a failure escape without clean-up, leaving the journal as a crash would.</summary>
    internal bool RollBackOnFailure { get; set; } = true;

    /// <summary>Tests: whether the two folders are on one drive (a rename); <c>null</c> = ask the OS.</summary>
    internal bool? SameDrive { get; set; }

    /// <summary>The folder the backups are in now.</summary>
    public string Root => RootOf(_paths);

    /// <summary>A move a crash left behind waits for <see cref="Recover"/>. Never throws.</summary>
    public bool HasInterruptedMove => TryRead(_paths, out var file, out _) && file?.Move is not null;

    /// <summary>
    /// The folder the backups are in now: the location file's <c>path</c> (during a move: the old folder until the
    /// commit, then the new one), else <see cref="AppPaths.Backups"/>. Never throws: an unreadable file gives the default.
    /// </summary>
    public static string RootOf(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return TryRead(paths, out var file, out _) && file?.Path is { } path && Path.IsPathFullyQualified(path) ? path : paths.Backups;
    }

    /// <summary>The folder the backups go into when the player picks <paramref name="picked"/>.</summary>
    public static string TargetFor(string picked)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(picked);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(picked));
        return string.Equals(Path.GetFileName(full), FolderName, StringComparison.OrdinalIgnoreCase) ? full : Path.Combine(full, FolderName);
    }

    /// <summary>
    /// Moves the backups into <see cref="TargetFor"/> <paramref name="picked"/>. Refused without changing anything while
    /// another transaction runs, when the folders are the same or nested, the new one isn't empty or its drive lacks the
    /// space; any later failure (or cancellation, rethrown) puts everything back.
    /// </summary>
    public BackupMoveResult Move(string picked, IProgress<BackupMoveProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(picked);
        using var gate = InstallLock.TryAcquire(_paths);
        if (gate is null)
        {
            return Fail(BackupMoveProblem.Busy);
        }

        if (!RecoverLocked() || File.Exists(_paths.InstallJournal))
        {
            return Fail(BackupMoveProblem.Interrupted);
        }

        if (TryRead(_paths, out var current, out _) && current?.SchemaVersion > CurrentSchemaVersion)
        {
            return Fail(BackupMoveProblem.NewerFormat);
        }

        var from = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root));
        var to = TargetFor(picked);
        if (PathNames.Same(from, to))
        {
            return Fail(BackupMoveProblem.SameFolder);
        }

        if (IsInside(to, from) || IsInside(from, to))
        {
            return Fail(BackupMoveProblem.Nested);
        }

        var targetExisted = Directory.Exists(to);
        try
        {
            if (targetExisted && Directory.EnumerateFileSystemEntries(to).Any())
            {
                return Fail(BackupMoveProblem.NotEmpty);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail(Classify(e), e.Message);
        }

        var size = FolderBytes.Of(from, cancellationToken);
        var rename = Directory.Exists(from) && (SameDrive ?? OnSameDrive(from, to));
        if (!rename && size > 0 && _disk.FreeBytes(to) is { } free && free < size + Margin)
        {
            return Fail(BackupMoveProblem.DiskFull, $"Needs {size + Margin:N0} bytes; free: {free:N0}.");
        }

        var move = new BackupMoveJson(from, to, PhaseCopy, CreatedTarget: !targetExisted);
        try
        {
            Write(new BackupLocationFile(CurrentSchemaVersion, from, move));
            if (!targetExisted)
            {
                Directory.CreateDirectory(to);
            }

            if (Directory.Exists(from))
            {
                if (!(rename && TryRename(from, to)))
                {
                    Copy(from, to, size, progress, cancellationToken);
                }
            }

            Write(new BackupLocationFile(CurrentSchemaVersion, to, move with { Phase = PhaseCleanup }));
        }
        catch (Exception e) when (IsMoveError(e) && RollBackOnFailure)
        {
            var problem = e is BackupVerifyException ? BackupMoveProblem.VerifyFailed : Classify(e);
            _logger.LogError(e, "Moving the backups from {From} to {To} failed: {Problem}", from, to, problem);
            if (!RollBack(move))
            {
                _logger.LogError("Rolling back the backup move failed; it is finished on the next start");
            }

            if (e is OperationCanceledException)
            {
                throw;
            }

            return Fail(problem, e.Message);
        }

        var leftBehind = CleanUp(move) ? null : from;
        _logger.LogInformation("Backups moved from {From} to {To} ({Bytes:N0} bytes{Left})", from, to, size, leftBehind is null ? string.Empty : "; the old folder is deleted later");
        return new BackupMoveResult(to, LeftBehind: leftBehind);
    }

    /// <summary>
    /// Finishes a move a crash left behind (start-up; installs call it before every transaction): a copy is rolled back,
    /// or completed when the folder had already been renamed; a clean-up is finished. <c>true</c> when nothing is left;
    /// <c>false</c> when it couldn't finish (busy, a file in use: tried again next time). Never throws for disk state.
    /// </summary>
    public bool Recover()
    {
        using var gate = InstallLock.TryAcquire(_paths);
        return gate is not null && RecoverLocked();
    }

    /// <summary><see cref="Recover"/> under the install lock (the caller holds it).</summary>
    internal bool RecoverLocked()
    {
        if (!TryRead(_paths, out var file, out var broken))
        {
            if (broken)
            {
                MoveAside();
            }

            return true;
        }

        if (file?.Move is not { From: { Length: > 0 } from, To: { Length: > 0 } to } move)
        {
            return true;
        }

        try
        {
            if (move.Phase == PhaseCleanup)
            {
                return CleanUp(move);
            }

            if (!Directory.Exists(from) && Directory.Exists(to))
            {
                // The rename had happened: roll forward.
                Write(new BackupLocationFile(CurrentSchemaVersion, to, null));
                _logger.LogInformation("An interrupted backup move to {To} was completed", to);
                return true;
            }

            var done = RollBack(move);
            _logger.LogInformation("An interrupted backup move to {To} was rolled back: {Done}", to, done);
            return done;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(e, "Finishing the interrupted backup move to {To} failed", to);
            return false;
        }
    }

    /// <summary>Deletes what the move put in the new folder and points back at the old one; <c>false</c> when something stayed.</summary>
    private bool RollBack(BackupMoveJson move)
    {
        try
        {
            if (move.CreatedTarget)
            {
                InstallPaths.DeleteTree(move.To!);
            }
            else if (Directory.Exists(move.To))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(move.To!).ToList())
                {
                    if (Directory.Exists(entry))
                    {
                        InstallPaths.DeleteTree(entry);
                    }
                    else
                    {
                        File.Delete(entry);
                    }
                }
            }

            Write(new BackupLocationFile(CurrentSchemaVersion, move.From, null));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(e, "Can't delete the copied backups in {To}", move.To);
            return false;
        }
    }

    /// <summary>After the commit: the old folder goes, then the journal; <c>false</c> when the old folder stays for now.</summary>
    private bool CleanUp(BackupMoveJson move)
    {
        try
        {
            InstallPaths.DeleteTree(move.From!);
            Write(new BackupLocationFile(CurrentSchemaVersion, move.To, null));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Can't delete the old backups folder {From} yet", move.From);
            return false;
        }
    }

    /// <summary>A damaged location file is kept as <c>&lt;file&gt;.&lt;utc&gt;.bak</c>; the default folder is used from now on.</summary>
    private void MoveAside()
    {
        var file = _paths.BackupsLocationFile;
        var aside = $"{file}.{_time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)}.bak";
        _logger.LogError("The backups location file {File} is damaged; moved to {Aside}, backups are looked for in {Default}", file, aside, _paths.Backups);
        try
        {
            File.Move(file, aside, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(e, "Can't move the damaged {File} aside", file);
        }
    }

    private bool TryRename(string from, string to)
    {
        try
        {
            Directory.Delete(to);
            Directory.Move(from, to);
            return true;
        }
        catch (IOException e)
        {
            // Another file system after all (a mount inside the drive): copy instead.
            _logger.LogInformation(e, "Renaming {From} to {To} failed; copying", from, to);
            Directory.CreateDirectory(to);
            return false;
        }
    }

    /// <summary>Copies every file and empty folder, each file hash-checked against its source.</summary>
    private void Copy(string from, string to, long total, IProgress<BackupMoveProgress>? progress, CancellationToken cancellationToken)
    {
        long done = 0;
        progress?.Report(new BackupMoveProgress(0, total));
        foreach (var folder in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, folder)));
        }

        foreach (var source in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(from, source);
            BeforeFile?.Invoke(relative.Replace(Path.DirectorySeparatorChar, '/'));
            var target = Path.Combine(to, relative);
            var written = InstallPaths.CopyAtomically(source, target, cancellationToken);
            if (!string.Equals(written, InstallPaths.HashOrNull(source, cancellationToken), StringComparison.Ordinal)
                || !string.Equals(written, InstallPaths.HashOrNull(target, cancellationToken), StringComparison.Ordinal))
            {
                throw new BackupVerifyException($"{target} doesn't read back as {source}.");
            }

            done += FolderBytes.OfFile(target);
            progress?.Report(new BackupMoveProgress(done, total));
        }
    }

    private static bool OnSameDrive(string from, string to)
    {
        var existing = to;
        while (!Directory.Exists(existing) && Path.GetDirectoryName(existing) is { } parent)
        {
            existing = parent;
        }

        var a = InstallPaths.DriveOf(from);
        var b = InstallPaths.DriveOf(existing);
        return a is not null && b is not null && PathNames.Same(a.RootDirectory.FullName, b.RootDirectory.FullName);
    }

    private static bool IsInside(string path, string folder) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static bool IsMoveError(Exception e) =>
        e is IOException or UnauthorizedAccessException or OperationCanceledException or BackupVerifyException;

    private static BackupMoveProblem Classify(Exception e) => InstallErrors.Classify(e, null) switch
    {
        InstallProblem.DiskFull => BackupMoveProblem.DiskFull,
        InstallProblem.PermissionDenied or InstallProblem.ReadOnly => BackupMoveProblem.Denied,
        _ => BackupMoveProblem.IoError,
    };

    private BackupMoveResult Fail(BackupMoveProblem problem, string? detail = null) => new(Root, problem, detail);

    private void Write(BackupLocationFile file) =>
        AtomicFile.WriteAllBytes(_paths.BackupsLocationFile, JsonSerializer.SerializeToUtf8Bytes(file, BackupLocationJsonContext.Default.BackupLocationFile));

    /// <summary>
    /// Reads the location file. <c>false</c> when there is none, or it can't be read (<paramref name="broken"/>: damaged;
    /// the default folder is used, and the next recovery moves the file aside).
    /// </summary>
    private static bool TryRead(AppPaths paths, out BackupLocationFile? file, out bool broken)
    {
        file = null;
        broken = false;
        try
        {
            if (!File.Exists(paths.BackupsLocationFile))
            {
                return false;
            }

            file = JsonSerializer.Deserialize(File.ReadAllBytes(paths.BackupsLocationFile), BackupLocationJsonContext.Default.BackupLocationFile);
            return file is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            broken = true;
            return false;
        }
    }
}

/// <summary>A copied backup file didn't read back the same.</summary>
internal sealed class BackupVerifyException(string message) : IOException(message);

/// <summary>On-disk shape of <c>backups-location.json</c>.</summary>
/// <param name="Path">The folder the backups are in (during a move: the old one until the commit).</param>
/// <param name="Move">The move in progress; <c>null</c> = none.</param>
internal sealed record BackupLocationFile(int SchemaVersion, string? Path, BackupMoveJson? Move);

/// <param name="Phase"><c>copy</c> (the old folder is the backups; roll back) or <c>cleanup</c> (the new one is; delete the old).</param>
/// <param name="CreatedTarget">The move created the new folder (rolled back: deleted; else only emptied).</param>
internal sealed record BackupMoveJson(string? From, string? To, string? Phase, bool CreatedTarget);

[JsonSourceGenerationOptions(WriteIndented = true, NewLine = "\n", PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BackupLocationFile))]
internal sealed partial class BackupLocationJsonContext : JsonSerializerContext;
