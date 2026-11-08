using System.Globalization;
using System.Security.Cryptography;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Generation;

/// <summary>Which game files a working copy holds.</summary>
public enum WorkingCopyScope
{
    /// <summary>Everything (an unknown engine: the agent may need any file).</summary>
    AllFiles,

    /// <summary>
    /// Everything but movies, audio and streamed Unity resources (<see cref="WorkingCopies.IsMedia"/>), which no recipe
    /// tool reads: the default when the toolkit handles the engine.
    /// </summary>
    WithoutMedia,
}

/// <summary>A file or link of the game that goes into the copy.</summary>
/// <param name="RelativePath">Relative to the game folder, <c>/</c>-separated.</param>
/// <param name="LinkTarget">The link's target as stored (a link is recreated, never followed); <c>null</c> for a file.</param>
/// <param name="IsFolderLink">The link points at a folder.</param>
public sealed record WorkingCopyEntry(string RelativePath, long Size, string? LinkTarget = null, bool IsFolderLink = false);

/// <summary>What <see cref="WorkingCopies.Plan"/> found: the files to copy and what the scope leaves out.</summary>
/// <param name="Folders">Every folder of the copy (relative, <c>/</c>-separated, parents first), empty ones included.</param>
public sealed record WorkingCopyPlan(
    string GameFolder,
    WorkingCopyScope Scope,
    IReadOnlyList<WorkingCopyEntry> Entries,
    IReadOnlyList<string> Folders,
    long TotalBytes,
    int SkippedFiles,
    long SkippedBytes)
{
    public int FileCount => Entries.Count;
}

/// <summary>
/// Disk space for a working copy. <paramref name="NeededBytes"/> = the copy (nothing when it can be cloned) + the
/// reserve asked for + <see cref="WorkingCopies.Margin"/>; <paramref name="FreeBytes"/> <c>null</c> = unknown (not
/// checked).
/// </summary>
public sealed record WorkingCopySpace(long NeededBytes, long? FreeBytes, bool CanClone)
{
    public bool IsEnough => FreeBytes is not { } free || free >= NeededBytes;
}

/// <param name="BytesDone">Bytes of the plan in place (a clone counts its size at once).</param>
public sealed record WorkingCopyProgress(long BytesDone, long BytesTotal, int FilesDone, int FilesTotal);

/// <summary>A made working copy.</summary>
/// <param name="Folder">The copy (<see cref="WorkingCopies.CopyFolder"/> of the job folder).</param>
/// <param name="SkippedLinks">Links the OS refused to create (Windows without the symlink privilege).</param>
public sealed record WorkingCopyResult(string Folder, int Files, long ClonedBytes, long CopiedBytes, IReadOnlyList<string> SkippedLinks);

public enum WorkingCopyProblem
{
    /// <summary>The game folder is missing.</summary>
    GameMissing,

    /// <summary>Not enough free space, found before anything was written.</summary>
    DiskFull,

    /// <summary>The job already has a working copy.</summary>
    AlreadyExists,

    /// <summary>A game file can't be read.</summary>
    SourceUnreadable,

    /// <summary>Writing the copy failed (disk full midway, permissions).</summary>
    DiskError,
}

/// <summary>Making a working copy failed; whatever was written was removed again.</summary>
public sealed class WorkingCopyException : Exception
{
    public WorkingCopyException(WorkingCopyProblem problem, string message, string? file = null, Exception? inner = null)
        : base(message, inner)
    {
        Problem = problem;
        File = file;
    }

    public WorkingCopyException()
    {
    }

    public WorkingCopyException(string message)
        : base(message)
    {
    }

    public WorkingCopyException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public WorkingCopyProblem Problem { get; }

    /// <summary>The game file or copy path concerned, when there is one.</summary>
    public string? File { get; }

    /// <summary>Set for <see cref="WorkingCopyProblem.DiskFull"/>.</summary>
    public WorkingCopySpace? Space { get; init; }
}

/// <summary>Free space of the drive holding a path; behind an interface so tests can run out of space.</summary>
public interface IDiskSpace
{
    /// <summary>Free bytes for the current user on the drive of <paramref name="path"/> (which may not exist yet); <c>null</c> = unknown.</summary>
    long? FreeBytes(string path);
}

public sealed class SystemDiskSpace : IDiskSpace
{
    public long? FreeBytes(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            var existing = Path.GetFullPath(path);
            while (!Directory.Exists(existing) && Path.GetDirectoryName(existing) is { } parent)
            {
                existing = parent;
            }

            return InstallPaths.DriveOf(existing)?.AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// Copies of game files that generation works on (the agent never touches the game folder):
/// <c>&lt;data&gt;/work/&lt;game folder&gt;/&lt;job id&gt;/</c> is the job folder (task 47's <c>job.json</c>, the
/// <see cref="GenerationFiles"/>), its <c>game/</c> subfolder the copy. Files are cloned where the file system can
/// (<see cref="IFileCloner"/>), else copied; links are recreated, never followed. Methods block; call them off the UI
/// thread.
/// </summary>
public sealed class WorkingCopies
{
    /// <summary>Space kept free beyond the copy and the reserve: job files, logs, clone metadata.</summary>
    public const long Margin = 64L * 1024 * 1024;

    private const string CopyFolderName = "game";
    private const int BufferSize = 1 << 20;

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Movies.
        ".mp4", ".m4v", ".webm", ".ogv", ".mkv", ".avi", ".mov", ".wmv", ".mpg", ".mpeg", ".bik", ".bk2", ".usm",

        // Audio, sound banks (Wwise, FMOD, XACT).
        ".ogg", ".wav", ".mp3", ".flac", ".opus", ".m4a", ".aac", ".wma", ".wem", ".bnk", ".bank", ".fsb", ".xwm", ".xwb",

        // Unity streamed textures and audio (assets and bundles name them, the recipe never reads them).
        ".ress", ".resource",
    };

    private readonly AppPaths _paths;
    private readonly IFileCloner _cloner;
    private readonly IDiskSpace _disk;

    public WorkingCopies(AppPaths paths, IFileCloner cloner, IDiskSpace disk)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(cloner);
        ArgumentNullException.ThrowIfNull(disk);
        _paths = paths;
        _cloner = cloner;
        _disk = disk;
    }

    /// <summary>The job folder of <paramref name="jobId"/> (not created).</summary>
    public string JobFolder(GameId game, string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        if (jobId.IndexOfAny(['/', '\\', ':']) >= 0 || jobId is "." or "..")
        {
            throw new ArgumentException($"'{jobId}' is not a job ID.", nameof(jobId));
        }

        return Path.Combine(_paths.WorkingCopies, RegistryLayout.GameFolder(game), jobId);
    }

    /// <summary>The working copy inside a job folder (what the agent and the toolkit MCP server get as their root).</summary>
    public static string CopyFolder(string jobFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobFolder);
        return Path.Combine(jobFolder, CopyFolderName);
    }

    /// <summary>A new job ID: UTC time (sorts by age) + random suffix.</summary>
    public static string NewJobId(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return clock.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-"
            + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));
    }

    /// <summary>A file no recipe tool reads (movie, audio, streamed Unity resource), left out by <see cref="WorkingCopyScope.WithoutMedia"/>.</summary>
    public static bool IsMedia(string path) => MediaExtensions.Contains(Path.GetExtension(path));

    /// <summary>The files a copy of <paramref name="gameFolder"/> would hold, without following links.</summary>
    /// <exception cref="WorkingCopyException">The game folder is missing or can't be listed.</exception>
    public WorkingCopyPlan Plan(string gameFolder, WorkingCopyScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);
        var root = Path.GetFullPath(gameFolder);
        if (!Directory.Exists(root))
        {
            throw new WorkingCopyException(WorkingCopyProblem.GameMissing, $"The game folder {root} doesn't exist.", root);
        }

        var entries = new List<WorkingCopyEntry>();
        var folders = new List<string>();
        long total = 0, skippedBytes = 0;
        var skipped = 0;
        var pending = new Stack<(DirectoryInfo Folder, string Relative)>();
        pending.Push((new DirectoryInfo(root), ""));
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileSystemInfo[] children;
            try
            {
                children = current.Folder.GetFileSystemInfos();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new WorkingCopyException(WorkingCopyProblem.SourceUnreadable, $"Can't list {current.Folder.FullName}: {e.Message}", current.Folder.FullName, e);
            }

            var subfolders = new List<(DirectoryInfo, string)>();
            foreach (var child in children.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                var relative = current.Relative.Length == 0 ? child.Name : current.Relative + "/" + child.Name;
                if (child.LinkTarget is { } target)
                {
                    entries.Add(new WorkingCopyEntry(relative, 0, target, child is DirectoryInfo));
                }
                else if (child is DirectoryInfo folder)
                {
                    folders.Add(relative);
                    subfolders.Add((folder, relative));
                }
                else if (child is FileInfo file)
                {
                    if (scope == WorkingCopyScope.WithoutMedia && IsMedia(file.Name))
                    {
                        skipped++;
                        skippedBytes += file.Length;
                        continue;
                    }

                    entries.Add(new WorkingCopyEntry(relative, file.Length));
                    total += file.Length;
                }
            }

            for (var i = subfolders.Count - 1; i >= 0; i--)
            {
                pending.Push(subfolders[i]);
            }
        }

        return new WorkingCopyPlan(root, scope, entries, folders, total, skipped, skippedBytes);
    }

    /// <summary>
    /// The space a copy needs on the drive of the working copies and whether it can be cloned there (found by cloning
    /// one game file next to the working copies and removing it again).
    /// </summary>
    /// <param name="reserveBytes">Room needed beyond the copy (the rebuilt files of the job).</param>
    public WorkingCopySpace CheckSpace(WorkingCopyPlan plan, long reserveBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfNegative(reserveBytes);
        var canClone = ProbeClone(plan);
        return new WorkingCopySpace((canClone ? 0 : plan.TotalBytes) + reserveBytes + Margin, _disk.FreeBytes(_paths.WorkingCopies), canClone);
    }

    /// <summary>
    /// Makes the copy of <paramref name="plan"/> in <see cref="CopyFolder"/> of <paramref name="jobFolder"/>. The space
    /// is checked first (<see cref="WorkingCopyProblem.DiskFull"/> before anything is written); a failure or a cancel
    /// removes the copy again (and the job folder when this call created it).
    /// </summary>
    /// <exception cref="WorkingCopyException">See <see cref="WorkingCopyProblem"/>.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; nothing is left.</exception>
    public WorkingCopyResult Create(
        WorkingCopyPlan plan,
        string jobFolder,
        long reserveBytes = 0,
        IProgress<WorkingCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobFolder);
        if (!Directory.Exists(plan.GameFolder))
        {
            throw new WorkingCopyException(WorkingCopyProblem.GameMissing, $"The game folder {plan.GameFolder} doesn't exist.", plan.GameFolder);
        }

        var job = Path.GetFullPath(jobFolder);
        var copy = CopyFolder(job);
        if (Directory.Exists(copy))
        {
            throw new WorkingCopyException(WorkingCopyProblem.AlreadyExists, $"The job already has a working copy in {copy}.", copy);
        }

        var space = CheckSpace(plan, reserveBytes);
        if (!space.IsEnough)
        {
            throw new WorkingCopyException(
                WorkingCopyProblem.DiskFull,
                $"The working copy needs {space.NeededBytes:N0} bytes; {space.FreeBytes:N0} are free.",
                _paths.WorkingCopies)
            {
                Space = space,
            };
        }

        var createdJob = !Directory.Exists(job);
        try
        {
            return Copy(plan, copy, space.CanClone, progress, cancellationToken);
        }
        catch
        {
            DeleteTree(copy);
            if (createdJob)
            {
                DeleteTree(job);
                RemoveEmptyParent(job);
            }

            throw;
        }
    }

    /// <summary>Deletes a job folder with its working copy (read-only files included), then its game folder when empty.</summary>
    /// <exception cref="IOException">A file can't be deleted (in use).</exception>
    public void Delete(string jobFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobFolder);
        var job = Path.GetFullPath(jobFolder);
        var work = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.WorkingCopies)) + Path.DirectorySeparatorChar;
        if (!job.StartsWith(work, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{job} is not inside the working copies folder.", nameof(jobFolder));
        }

        DeleteTree(job, throwOnFailure: true);
        RemoveEmptyParent(job);
    }

    /// <summary>
    /// Deletes only the working copy of a job folder (a copy half made when the app died: the Setup stage makes it
    /// again); the job's files stay. Nothing to delete is fine.
    /// </summary>
    /// <exception cref="IOException">A file can't be deleted (in use).</exception>
    public void DeleteCopy(string jobFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobFolder);
        DeleteTree(CopyFolder(Path.GetFullPath(jobFolder)), throwOnFailure: true);
    }

    private WorkingCopyResult Copy(WorkingCopyPlan plan, string copy, bool clone, IProgress<WorkingCopyProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(copy);
        foreach (var folder in plan.Folders)
        {
            Directory.CreateDirectory(Path.Combine(copy, folder));
        }

        long done = 0, cloned = 0, copied = 0;
        var files = 0;
        var skippedLinks = new List<string>();
        foreach (var entry in plan.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.Combine(plan.GameFolder, entry.RelativePath);
            var target = Path.Combine(copy, entry.RelativePath);
            if (entry.LinkTarget is { } link)
            {
                if (!TryLink(target, link, entry.IsFolderLink))
                {
                    skippedLinks.Add(entry.RelativePath);
                }
            }
            else if (clone && _cloner.TryClone(source, target))
            {
                cloned += entry.Size;
                done += entry.Size;
            }
            else
            {
                CopyFile(source, target, entry.RelativePath, bytes =>
                {
                    done += bytes;
                    progress?.Report(new WorkingCopyProgress(done, plan.TotalBytes, files, plan.FileCount));
                }, cancellationToken);
                copied += entry.Size;
            }

            files++;
            progress?.Report(new WorkingCopyProgress(done, plan.TotalBytes, files, plan.FileCount));
        }

        return new WorkingCopyResult(copy, files, cloned, copied, skippedLinks);
    }

    private static void CopyFile(string source, string target, string relative, Action<long> copiedBytes, CancellationToken cancellationToken)
    {
        FileStream input;
        try
        {
            input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new WorkingCopyException(WorkingCopyProblem.SourceUnreadable, $"Can't read {relative}: {e.Message}", source, e);
        }

        try
        {
            using (input)
            {
                if (OperatingSystem.IsWindows())
                {
                    // CopyFileEx block-clones on ReFS / Dev Drive (Windows 11 24H2+); progress is per file there.
                    File.Copy(source, target);
                    File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                    copiedBytes(input.Length);
                    return;
                }

                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize);
                var buffer = new byte[BufferSize];
                int read;
                while ((read = ReadSource(input, buffer, relative, source)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    copiedBytes(read);
                }
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(target, File.GetUnixFileMode(source) | UnixFileMode.UserWrite | UnixFileMode.UserRead);
            }

            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new WorkingCopyException(WorkingCopyProblem.DiskError, $"Can't write {relative} into the working copy: {e.Message}", target, e);
        }
    }

    private static int ReadSource(FileStream input, byte[] buffer, string relative, string source)
    {
        try
        {
            return input.Read(buffer, 0, buffer.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new WorkingCopyException(WorkingCopyProblem.SourceUnreadable, $"Can't read {relative}: {e.Message}", source, e);
        }
    }

    private static bool TryLink(string target, string link, bool folder)
    {
        try
        {
            if (folder)
            {
                Directory.CreateSymbolicLink(target, link);
            }
            else
            {
                File.CreateSymbolicLink(target, link);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool ProbeClone(WorkingCopyPlan plan)
    {
        if (plan.Entries.FirstOrDefault(e => e.LinkTarget is null) is not { } sample)
        {
            return false;
        }

        var probe = Path.Combine(_paths.WorkingCopies, $".clone-probe-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}");
        try
        {
            Directory.CreateDirectory(_paths.WorkingCopies);
            return _cloner.TryClone(Path.Combine(plan.GameFolder, sample.RelativePath), probe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            DeleteTree(probe);
        }
    }

    private void RemoveEmptyParent(string job)
    {
        var parent = Path.GetDirectoryName(job);
        if (parent is not null && !PathNames.Same(parent, _paths.WorkingCopies))
        {
            InstallPaths.DeleteIfEmpty(parent);
        }
    }

    private static void DeleteTree(string path, bool throwOnFailure = false)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null || File.Exists(path))
            {
                info.Attributes = FileAttributes.Normal;
                info.Delete();
                return;
            }

            if (!Directory.Exists(path))
            {
                return;
            }

            ClearReadOnly(new DirectoryInfo(path));
            Directory.Delete(path, recursive: true);
        }
        catch (Exception e) when (!throwOnFailure && e is IOException or UnauthorizedAccessException)
        {
            // Best effort: what is left is removed with the job later.
        }
    }

    /// <summary>Clears the read-only attribute of files under <paramref name="folder"/>, never following a link out of it.</summary>
    private static void ClearReadOnly(DirectoryInfo folder)
    {
        foreach (var child in folder.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
        {
            if (child.LinkTarget is not null)
            {
                continue;
            }

            if (child is DirectoryInfo subfolder)
            {
                ClearReadOnly(subfolder);
            }
            else if (child.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                child.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
    }
}
