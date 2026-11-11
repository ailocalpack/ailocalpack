using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Install;

namespace AiLocalPack.Core.Storage;

/// <summary>One generation job folder (<c>&lt;data&gt;/work/&lt;game folder&gt;/&lt;job id&gt;/</c>) and what it takes.</summary>
/// <param name="Folder">The job folder (job files and the working copy in <c>game/</c>).</param>
/// <param name="GameFolder">The game's folder name (<c>steam-620</c>), for a job that can't be read.</param>
/// <param name="Job">Its job; <c>null</c> when <c>job.json</c> is missing or damaged (a copy left by a crash).</param>
public sealed record WorkingCopyUsage(string Folder, string GameFolder, GenerationJob? Job, long Bytes)
{
    public string JobId => Path.GetFileName(Folder);
}

/// <summary>
/// The disk the app uses, as Settings → Storage shows it (task 66): backups (the folder in use, bytes, games with a
/// backup), generation working copies (per job folder), the cache.
/// </summary>
/// <param name="BackupGames">Game folders under the backups holding at least one backup.</param>
public sealed record StorageUsage(string BackupsFolder, long BackupBytes, int BackupGames, IReadOnlyList<WorkingCopyUsage> WorkingCopies, long CacheBytes)
{
    public long WorkingCopyBytes => WorkingCopies.Sum(w => w.Bytes);

    public long TotalBytes => BackupBytes + WorkingCopyBytes + CacheBytes;

    /// <summary>
    /// Measures everything (walks the backups, the work folder and the cache; links never followed). Never throws for
    /// disk state: what can't be read counts as nothing. Blocks: call it off the UI thread.
    /// </summary>
    public static StorageUsage Measure(AppPaths paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var backups = BackupLocation.RootOf(paths);
        var games = Subfolders(backups).Count(g => Subfolders(g).Count > 0);
        var copies = new List<WorkingCopyUsage>();
        foreach (var game in Subfolders(paths.WorkingCopies))
        {
            foreach (var folder in Subfolders(game))
            {
                cancellationToken.ThrowIfCancellationRequested();
                copies.Add(new WorkingCopyUsage(folder, Path.GetFileName(game), ReadJob(folder), FolderBytes.Of(folder, cancellationToken)));
            }
        }

        return new StorageUsage(
            backups,
            FolderBytes.Of(backups, cancellationToken),
            games,
            [.. copies.OrderByDescending(c => c.JobId, StringComparer.Ordinal)],
            FolderBytes.Of(paths.Cache, cancellationToken));
    }

    private static GenerationJob? ReadJob(string folder)
    {
        try
        {
            return GenerationJobStore.Read(folder);
        }
        catch (GenerationJobException)
        {
            return null;
        }
    }

    private static List<string> Subfolders(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? [.. new DirectoryInfo(folder).EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Select(d => d.FullName).Order(StringComparer.Ordinal)]
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
