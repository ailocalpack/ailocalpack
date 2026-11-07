using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Core.Registry;

/// <summary>Why a pack couldn't be fetched.</summary>
public enum PackFetchProblem
{
    None,

    /// <summary>No connection, and no cached copy of this revision.</summary>
    Offline,

    /// <summary>No data within the timeout, and no cached copy.</summary>
    Timeout,

    /// <summary>The server answered with an error status (other than 404 / 410).</summary>
    HttpError,

    /// <summary>A file of the pack is gone (local registry lacks it, HTTP 404 / 410).</summary>
    FileMissing,

    /// <summary>The manifest is not valid, or names files outside the registry.</summary>
    InvalidManifest,

    /// <summary>The manifest is in a newer format: the app needs an update.</summary>
    NewerFormat,

    /// <summary>The manifest names another pack, game or language than the index entry.</summary>
    Mismatch,

    /// <summary>A downloaded file doesn't have the size or SHA-256 the manifest gives; it was deleted.</summary>
    HashMismatch,

    /// <summary>The string table has the right hash but doesn't read (or is in another language); it was deleted.</summary>
    InvalidStrings,

    /// <summary>The cache folder can't be written.</summary>
    DiskError,
}

/// <summary>A fetched pack, or why it couldn't be fetched.</summary>
/// <param name="FromCache">Nothing was downloaded: the cache already had every file (offline: the cached revision).</param>
/// <param name="Downloaded">Bytes received in this call.</param>
public sealed record PackFetchResult(
    CachedPack? Pack,
    PackFetchProblem Problem,
    string? Detail = null,
    bool FromCache = false,
    long Downloaded = 0,
    IReadOnlyList<PackFormatError>? FormatErrors = null)
{
    public bool IsFetched => Pack is not null;

    public IReadOnlyList<PackFormatError> Errors => FormatErrors ?? [];

    /// <summary>Worth trying again later (no connection, timeout, server error); a partial download resumes.</summary>
    public bool MayPass => Problem is PackFetchProblem.Offline or PackFetchProblem.Timeout or PackFetchProblem.HttpError;
}

/// <summary>Bytes of the pack's files in the cache (resumed parts included) of all it needs.</summary>
public sealed record PackFetchProgress(long Done, long Total);

/// <param name="Timeout">Longest wait for the next bytes (not for the whole file: patches can be large).</param>
/// <param name="Attempts">Tries per file for errors that may pass; each resumes where the last stopped.</param>
/// <param name="RetryDelay">Wait before the second try; doubled before each next one.</param>
public sealed record PackFetchOptions(TimeSpan Timeout, int Attempts = 3, TimeSpan RetryDelay = default)
{
    public static PackFetchOptions Default { get; } = new(TimeSpan.FromSeconds(30), 3, TimeSpan.FromSeconds(1));
}

/// <summary>
/// Downloads one pack the index lists into the <see cref="PackCache"/>: its manifest (<see cref="PackManifestClient"/>),
/// then only the files it needs: the gzip string table (next to the manifest, local registry or https) and the binary
/// patch archive (its https Release asset URL). Each file is checked against the size and SHA-256 the manifest gives;
/// a mismatch deletes it. Downloads go to <c>*.part</c> files and resume with an HTTP range request after a dropped
/// connection, within this call (a few tries) and in the next one. A revision already cached is not downloaded again;
/// when the manifest can't be fetched (offline, timeout, server error) the cached copy of the index's revision is used.
/// Never throws except for cancellation by the caller. One fetch of a given pack at a time.
/// </summary>
public sealed class PackFetcher
{
    private readonly PackManifestClient _manifests;
    private readonly VerifiedDownloader _downloader;
    private readonly PackCache _cache;
    private readonly ILogger _logger;

    public PackFetcher(
        PackManifestClient manifests,
        HttpClient http,
        PackCache cache,
        TimeProvider? time = null,
        ILogger<PackFetcher>? logger = null,
        PackFetchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(cache);
        _manifests = manifests;
        _cache = cache;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        options ??= PackFetchOptions.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Attempts, 1, nameof(options));
        _downloader = new VerifiedDownloader(http, time ?? TimeProvider.System, _logger, options);
    }

    /// <summary>
    /// A client for pack files: no automatic decompression (the hashes are of the bytes as stored, and ranges count
    /// those bytes) and no overall timeout (large patches; <see cref="PackFetchOptions.Timeout"/> limits each wait).
    /// </summary>
    public static HttpClient CreateHttpClient(System.Net.Http.Headers.ProductInfoHeaderValue userAgent)
    {
        ArgumentNullException.ThrowIfNull(userAgent);
        var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.UserAgent.Add(userAgent);
        return http;
    }

    public async Task<PackFetchResult> FetchAsync(
        RegistryLocation location,
        RegistryPack pack,
        IProgress<PackFetchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(pack);

        var fetch = await _manifests.FetchAsync(location, pack, cancellationToken).ConfigureAwait(false);
        if (fetch is not { Manifest: { } manifest, Bytes: { } manifestBytes, Uri: { } manifestUri })
        {
            if (fetch.MayPass && _cache.Find(pack.Game, pack.Language, pack.Id, pack.Revision) is { } cached)
            {
                _logger.LogInformation("Pack {Pack}: registry unavailable ({Detail}); using the cached revision {Revision}", Key(pack), fetch.Detail, pack.Revision);
                Report(progress, cached.Manifest);
                return new PackFetchResult(cached, PackFetchProblem.None, fetch.Detail, FromCache: true);
            }

            return new PackFetchResult(null, Map(fetch.Problem), fetch.Detail, FormatErrors: fetch.FormatErrors);
        }

        if (_cache.Find(manifest, manifestBytes) is { } hit)
        {
            Report(progress, manifest);
            return new PackFetchResult(hit, PackFetchProblem.None, FromCache: true);
        }

        var entry = new CachedPack(manifest, _cache.EntryFolder(manifest, PackCache.Sha256Hex(manifestBytes)), PackCache.Sha256Hex(manifestBytes));
        var counter = new DownloadCounter((manifest.Strings?.Size ?? 0) + (manifest.Patch?.Size ?? 0), progress is null ? null : (done, total) => progress.Report(new PackFetchProgress(done, total)));
        try
        {
            Directory.CreateDirectory(entry.Folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return DiskFailure(pack, e);
        }

        if (manifest.Strings is { } strings)
        {
            Uri source;
            try
            {
                source = new Uri(manifestUri, strings.Path);
            }
            catch (UriFormatException e)
            {
                return Fail(pack, PackFetchProblem.InvalidManifest, $"The string table path {strings.Path} is not valid: {e.Message}", counter);
            }

            if (location.IsLocal ? !source.IsFile : source.Scheme != Uri.UriSchemeHttps)
            {
                return Fail(pack, PackFetchProblem.InvalidManifest, $"The string table address {source} is outside the registry.", counter);
            }

            if (await _downloader.DownloadAsync(source, entry.StringsFile!, strings.Size, strings.Sha256, counter, cancellationToken).ConfigureAwait(false) is { } failure)
            {
                return Fail(pack, failure.Problem, failure.Detail, counter);
            }

            if (CheckStrings(entry) is { } invalid)
            {
                TryDelete(entry.StringsFile!);
                return Fail(pack, PackFetchProblem.InvalidStrings, invalid, counter);
            }
        }

        if (manifest.Patch is { } patch)
        {
            if (patch.Url.Scheme != Uri.UriSchemeHttps)
            {
                return Fail(pack, PackFetchProblem.InvalidManifest, $"The patch address {patch.Url} is not https.", counter);
            }

            if (await _downloader.DownloadAsync(patch.Url, entry.PatchFile!, patch.Size, patch.Sha256, counter, cancellationToken).ConfigureAwait(false) is { } failure)
            {
                return Fail(pack, failure.Problem, failure.Detail, counter);
            }
        }

        try
        {
            // Last: an entry with its manifest is complete.
            AtomicFile.WriteAllBytes(entry.ManifestFile, manifestBytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return DiskFailure(pack, e);
        }

        _logger.LogInformation("Pack {Pack} revision {Revision} fetched into {Folder} ({Bytes} bytes downloaded)", Key(pack), manifest.Revision, entry.Folder, counter.Downloaded);
        return new PackFetchResult(entry, PackFetchProblem.None, Downloaded: counter.Downloaded);
    }

    private static string? CheckStrings(CachedPack entry)
    {
        try
        {
            var read = entry.ReadStrings();
            if (read.Value is not { } table)
            {
                return "The string table can't be read: " + string.Join("; ", read.Errors.Take(3));
            }

            return table.Language == entry.Manifest.Language
                ? null
                : $"The string table is in {table.Language}, the pack in {entry.Manifest.Language}.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "The string table can't be read: " + e.Message;
        }
    }

    private static PackFetchProblem Map(PackManifestProblem problem) => problem switch
    {
        PackManifestProblem.Offline => PackFetchProblem.Offline,
        PackManifestProblem.Timeout => PackFetchProblem.Timeout,
        PackManifestProblem.HttpError => PackFetchProblem.HttpError,
        PackManifestProblem.FileMissing => PackFetchProblem.FileMissing,
        PackManifestProblem.NewerFormat => PackFetchProblem.NewerFormat,
        PackManifestProblem.Mismatch => PackFetchProblem.Mismatch,
        _ => PackFetchProblem.InvalidManifest,
    };

    private static void Report(IProgress<PackFetchProgress>? progress, PackManifest manifest)
    {
        var total = (manifest.Strings?.Size ?? 0) + (manifest.Patch?.Size ?? 0);
        progress?.Report(new PackFetchProgress(total, total));
    }

    private PackFetchResult Fail(RegistryPack pack, PackFetchProblem problem, string? detail, DownloadCounter counter)
    {
        _logger.LogWarning("Pack {Pack}: {Problem} ({Detail})", Key(pack), problem, detail);
        return new PackFetchResult(null, problem, detail, Downloaded: counter.Downloaded);
    }

    private PackFetchResult DiskFailure(RegistryPack pack, Exception e)
    {
        _logger.LogWarning(e, "Pack {Pack}: writing to the cache {Folder} failed", Key(pack), _cache.Root);
        return new PackFetchResult(null, PackFetchProblem.DiskError, e.Message);
    }

    private static string Key(RegistryPack pack) => $"{pack.Game}/{pack.Language}/{pack.Id}";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left behind: the next fetch checks the file again.
        }
    }
}
