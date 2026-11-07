using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Core.Dependencies;

/// <summary>A dependency's release archive in the cache, or why it isn't there.</summary>
/// <param name="Path">The verified archive.</param>
/// <param name="Problem">Offline / Timeout / HttpError (may pass), FileMissing, HashMismatch, DiskError.</param>
/// <param name="Downloaded">Bytes received in this call.</param>
/// <param name="FromCache">It was in the cache already: nothing downloaded.</param>
public sealed record DependencyFetchResult(string? Path, PackFetchProblem Problem, string? Detail = null, long Downloaded = 0, bool FromCache = false)
{
    /// <summary>Worth trying again later (no connection, timeout, server error); a partial download resumes.</summary>
    public bool MayPass => Problem is PackFetchProblem.Offline or PackFetchProblem.Timeout or PackFetchProblem.HttpError;
}

/// <summary>
/// Downloads the official release archive of a whitelisted dependency into
/// <c>&lt;cache&gt;/dependencies/&lt;id&gt;/&lt;version&gt;/&lt;first 16 hex of its SHA-256&gt;-&lt;file&gt;</c>
/// (<see cref="AppPaths.Dependencies"/>): resumable, its size and SHA-256 checked against the whitelist before it
/// enters the cache (a mismatch deletes it). A cached file of the right size is used as is. Never throws except for
/// cancellation.
/// </summary>
public sealed class DependencyStore
{
    private readonly string _root;
    private readonly VerifiedDownloader _downloader;
    private readonly ILogger _logger;

    public DependencyStore(string cacheRoot, HttpClient http, TimeProvider? time = null, ILogger<DependencyStore>? logger = null, PackFetchOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheRoot);
        ArgumentNullException.ThrowIfNull(http);
        _root = System.IO.Path.GetFullPath(cacheRoot);
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _downloader = new VerifiedDownloader(http, time ?? TimeProvider.System, _logger, options ?? PackFetchOptions.Default);
    }

    public DependencyStore(AppPaths paths, HttpClient http, TimeProvider? time = null, ILogger<DependencyStore>? logger = null)
        : this(paths?.Dependencies ?? throw new ArgumentNullException(nameof(paths)), http, time, logger)
    {
    }

    /// <summary>Where the archive is kept.</summary>
    public string CachedPath(DependencyChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        return System.IO.Path.Combine(_root, choice.Dependency.Id, Folder(choice.Version.Version), $"{choice.Asset.Sha256[..16]}-{choice.Asset.FileName}");
    }

    /// <summary>Whether the archive is in the cache (no download needed).</summary>
    public bool IsCached(DependencyChoice choice)
    {
        var path = CachedPath(choice);
        return File.Exists(path) && new FileInfo(path).Length == choice.Asset.Size;
    }

    public async Task<DependencyFetchResult> GetAsync(DependencyChoice choice, IProgress<PackFetchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(choice);
        var target = CachedPath(choice);
        if (IsCached(choice))
        {
            progress?.Report(new PackFetchProgress(choice.Asset.Size, choice.Asset.Size));
            return new DependencyFetchResult(target, PackFetchProblem.None, FromCache: true);
        }

        var asset = choice.Asset;
        var counter = new DownloadCounter(asset.Size, progress is null ? null : (done, total) => progress.Report(new PackFetchProgress(done, total)));
        if (await _downloader.DownloadAsync(asset.Url, target, asset.Size, asset.Sha256, counter, cancellationToken).ConfigureAwait(false) is { } failure)
        {
            _logger.LogWarning("Dependency {Id} {Version} ({Url}): {Problem} ({Detail})", choice.Dependency.Id, choice.Version.Version, asset.Url, failure.Problem, failure.Detail);
            return new DependencyFetchResult(null, failure.Problem, failure.Detail, counter.Downloaded);
        }

        _logger.LogInformation("Dependency {Id} {Version} downloaded from {Url} into {Path}, hash checked", choice.Dependency.Id, choice.Version.Version, asset.Url, target);
        return new DependencyFetchResult(target, PackFetchProblem.None, Downloaded: counter.Downloaded);
    }

    /// <summary>A version as a folder name (whitelist versions are letters, digits, '.', '-', '+').</summary>
    private static string Folder(string version) => version.Replace('+', '_');
}
