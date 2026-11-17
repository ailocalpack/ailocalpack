using System.Globalization;
using System.Security.Cryptography;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Generation;

/// <summary>
/// Disk space for a job's own files (_Live game folder_: no copy of the game is made). <paramref name="NeededBytes"/> =
/// the reserve for its rebuilt files + <see cref="GenerationJobFolders.Margin"/>; <paramref name="FreeBytes"/>
/// <c>null</c> = unknown (not checked).
/// </summary>
public sealed record JobSpace(long NeededBytes, long? FreeBytes)
{
    public bool IsEnough => FreeBytes is not { } free || free >= NeededBytes;
}

/// <summary>Movies, audio and streamed Unity resources: files no recipe reads and no game rewrites as a setting or cache.</summary>
public static class MediaFiles
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Movies.
        ".mp4", ".m4v", ".webm", ".ogv", ".mkv", ".avi", ".mov", ".wmv", ".mpg", ".mpeg", ".bik", ".bk2", ".usm",

        // Audio, sound banks (Wwise, FMOD, XACT).
        ".ogg", ".wav", ".mp3", ".flac", ".opus", ".m4a", ".aac", ".wma", ".wem", ".bnk", ".bank", ".fsb", ".xwm", ".xwb",

        // Unity streamed textures and audio (assets and bundles name them, the recipe never reads them).
        ".ress", ".resource",
    };

    public static bool IsMedia(string path) => Extensions.Contains(Path.GetExtension(path));
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
/// The folders of generation jobs (_Live game folder_ in <c>docs/generation.md</c>): <c>&lt;data&gt;/work/&lt;game
/// folder&gt;/&lt;job id&gt;/</c> holds a job's <c>job.json</c> and its <see cref="GenerationFiles"/>; the game is read
/// where it is installed, never copied. A job made before that keeps its full copy in <c>game/</c>
/// (<see cref="LegacyCopyFolder"/>) until its Setup runs again. Methods block; call them off the UI thread.
/// </summary>
public sealed class GenerationJobFolders(AppPaths paths, IDiskSpace disk)
{
    /// <summary>Space kept free beyond the reserve: job files, logs.</summary>
    public const long Margin = 64L * 1024 * 1024;

    private const string LegacyCopyName = "game";
    private const string LegacySavedOriginalsName = "game-originals";

    /// <summary>The job folder of <paramref name="jobId"/> (not created).</summary>
    public string JobFolder(GameId game, string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        if (jobId.IndexOfAny(['/', '\\', ':']) >= 0 || jobId is "." or "..")
        {
            throw new ArgumentException($"'{jobId}' is not a job ID.", nameof(jobId));
        }

        return Path.Combine(paths.GenerationJobs, RegistryLayout.GameFolder(game), jobId);
    }

    /// <summary>The full copy of the game a job made before _Live game folder_ (<c>game/</c> of its folder).</summary>
    public static string LegacyCopyFolder(string jobFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobFolder);
        return Path.Combine(jobFolder, LegacyCopyName);
    }

    /// <summary>A new job ID: UTC time (sorts by age) + random suffix.</summary>
    public static string NewJobId(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return clock.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-"
            + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));
    }

    /// <summary>The originals of the pack installed in <paramref name="gameFolder"/> (<see cref="InstalledOriginals.Find"/>), or none.</summary>
    public InstalledOriginals Originals(string gameFolder) => InstalledOriginals.Find(paths, gameFolder);

    /// <summary>The space a job needs on the drive of the job folders: <paramref name="reserveBytes"/> (its rebuilt files) + <see cref="Margin"/>.</summary>
    public JobSpace CheckSpace(long reserveBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(reserveBytes);
        return new JobSpace(reserveBytes + Margin, disk.FreeBytes(paths.GenerationJobs));
    }

    /// <summary>
    /// Deletes a job folder (read-only files included, links never followed), then its game folder when empty. A test
    /// run of the job that a crash left over the game is undone first: its originals are in the job folder.
    /// </summary>
    /// <exception cref="IOException">A file can't be deleted (in use), or the game's files can't be put back (the job stays).</exception>
    public void Delete(string jobFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobFolder);
        var job = Path.GetFullPath(jobFolder);
        var work = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.GenerationJobs)) + Path.DirectorySeparatorChar;
        if (!job.StartsWith(work, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{job} is not inside the generation jobs folder.", nameof(jobFolder));
        }

        if (new GenerationFiles(job).ReadGameFolder() is { } game && Directory.Exists(game))
        {
            TestRunOverlay.Recover(job, game);
        }

        DeleteTree(job, throwOnFailure: true);
        var parent = Path.GetDirectoryName(job);
        if (parent is not null && !PathNames.Same(parent, paths.GenerationJobs))
        {
            InstallPaths.DeleteIfEmpty(parent);
        }
    }

    /// <summary>Deletes the full copy an older job made (<see cref="LegacyCopyFolder"/>) and its saved originals; the job's files stay. Nothing to delete is fine.</summary>
    /// <exception cref="IOException">A file can't be deleted (in use).</exception>
    public static void DeleteLegacyCopy(string jobFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobFolder);
        var job = Path.GetFullPath(jobFolder);
        DeleteTree(Path.Combine(job, LegacySavedOriginalsName), throwOnFailure: true);
        DeleteTree(LegacyCopyFolder(job), throwOnFailure: true);
    }

    private static void DeleteTree(string path, bool throwOnFailure)
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
            // Best effort.
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
