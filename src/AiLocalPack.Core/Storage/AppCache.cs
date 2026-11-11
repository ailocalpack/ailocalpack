using AiLocalPack.Core.Install;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Core.Storage;

/// <summary>How clearing the cache ended.</summary>
/// <param name="Busy">An install, uninstall or backup move ran: nothing was deleted.</param>
/// <param name="FreedBytes">Bytes deleted.</param>
/// <param name="LeftBytes">Bytes of downloads that couldn't be deleted (in use) and stay.</param>
public sealed record CacheClearResult(bool Busy, long FreedBytes, long LeftBytes);

/// <summary>
/// The app's cache folder as Settings → Storage shows it (task 66): <see cref="Size"/> = everything under
/// <see cref="AppPaths.Cache"/>; <see cref="Clear"/> deletes what was downloaded (saved registry index, packs, fonts,
/// dependency archives, Steam covers), all fetched again when needed. The file hash cache and the anti-cheat rules stamp
/// stay: small, and costly to make again (every game file hashed, every game checked). Methods block: call them off the
/// UI thread.
/// </summary>
public sealed class AppCache
{
    private readonly AppPaths _paths;
    private readonly ILogger _logger;

    public AppCache(AppPaths paths, ILogger<AppCache>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>The folders <see cref="Clear"/> deletes.</summary>
    public IReadOnlyList<string> Downloads => [_paths.RegistryCache, _paths.Packs, _paths.Fonts, _paths.Dependencies, _paths.Covers];

    /// <summary>Bytes under the cache folder. Never throws.</summary>
    public long Size(CancellationToken cancellationToken = default) => FolderBytes.Of(_paths.Cache, cancellationToken);

    /// <summary>
    /// Deletes the downloads, file by file, under the install lock (an install may be reading a cached pack); a file in
    /// use stays and is counted in <see cref="CacheClearResult.LeftBytes"/>. Never throws for disk state.
    /// </summary>
    public CacheClearResult Clear()
    {
        using var gate = InstallLock.TryAcquire(_paths);
        if (gate is null)
        {
            return new CacheClearResult(true, 0, 0);
        }

        long before = 0, left = 0;
        foreach (var folder in Downloads)
        {
            before += FolderBytes.Of(folder);
            Delete(folder);
            left += FolderBytes.Of(folder);
        }

        _logger.LogInformation("Cache cleared: {Freed:N0} bytes freed, {Left:N0} bytes in use left", before - left, left);
        return new CacheClearResult(false, before - left, left);
    }

    private void Delete(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }).ToList())
            {
                try
                {
                    if (OperatingSystem.IsWindows() && File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }

                    File.Delete(file);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _logger.LogInformation("{File} stays in the cache: {Reason}", file, e.Message);
                }
            }

            foreach (var sub in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).Append(folder).ToList())
            {
                InstallPaths.DeleteIfEmpty(sub);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Clearing {Folder} stopped", folder);
        }
    }
}
