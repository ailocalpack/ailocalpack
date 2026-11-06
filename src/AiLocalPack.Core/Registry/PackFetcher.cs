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
    private const int BufferSize = 81920;

    private readonly PackManifestClient _manifests;
    private readonly HttpClient _http;
    private readonly PackCache _cache;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly PackFetchOptions _options;

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
        _http = http;
        _cache = cache;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _options = options ?? PackFetchOptions.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.Attempts, 1, nameof(options));
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
        var counter = new Counter(progress, (manifest.Strings?.Size ?? 0) + (manifest.Patch?.Size ?? 0));
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

            if (await DownloadAsync(source, entry.StringsFile!, strings.Size, strings.Sha256, counter, cancellationToken).ConfigureAwait(false) is { } failure)
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

            if (await DownloadAsync(patch.Url, entry.PatchFile!, patch.Size, patch.Sha256, counter, cancellationToken).ConfigureAwait(false) is { } failure)
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

    /// <summary>Gets <paramref name="source"/> into <paramref name="target"/> verified, or says why not.</summary>
    private async Task<Failure?> DownloadAsync(Uri source, string target, long size, string sha256, Counter counter, CancellationToken cancellationToken)
    {
        if (File.Exists(target) && new FileInfo(target).Length == size)
        {
            // Verified by an earlier call that stopped before the manifest was written.
            counter.Set(target, size);
            return null;
        }

        var part = target + PackCache.PartSuffix;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new Failure(PackFetchProblem.DiskError, e.Message, MayPass: false);
        }

        Failure? failure = null;
        if (source.IsFile)
        {
            failure = CopyLocal(source.LocalPath, part, size);
            if (failure is null)
            {
                counter.Add(target, size);
            }
        }
        else
        {
            for (var attempt = 1; attempt <= _options.Attempts; attempt++)
            {
                if (attempt > 1)
                {
                    var wait = _options.RetryDelay * Math.Pow(2, attempt - 2);
                    _logger.LogInformation("Download of {Uri}: {Problem} ({Detail}); try {Attempt} of {Attempts} in {Wait}", source, failure!.Value.Problem, failure.Value.Detail, attempt, _options.Attempts, wait);
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
                    }
                }

                failure = await TryDownloadAsync(source, target, size, counter, cancellationToken).ConfigureAwait(false);
                if (failure is not { MayPass: true })
                {
                    break;
                }
            }
        }

        if (failure is not null)
        {
            return failure;
        }

        return Verify(part, target, size, sha256);
    }

    private Failure? Verify(string part, string target, long size, string sha256)
    {
        try
        {
            string actual;
            long length;
            using (var stream = File.OpenRead(part))
            {
                length = stream.Length;
                actual = Convert.ToHexStringLower(SHA256.HashData(stream));
            }

            if (length != size || actual != sha256)
            {
                File.Delete(part);
                return new Failure(PackFetchProblem.HashMismatch, $"{Path.GetFileName(target)}: expected {size} bytes with SHA-256 {sha256}, got {length} bytes with {actual}; deleted.", MayPass: false);
            }

            File.Move(part, target, overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new Failure(PackFetchProblem.DiskError, e.Message, MayPass: false);
        }
    }

    private static Failure? CopyLocal(string path, string part, long size)
    {
        FileStream input;
        try
        {
            input = File.OpenRead(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new Failure(PackFetchProblem.FileMissing, $"There is no file at {path}.", MayPass: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new Failure(PackFetchProblem.FileMissing, $"Can't read {path}: {e.Message}", MayPass: false);
        }

        using (input)
        {
            if (input.Length != size)
            {
                return new Failure(PackFetchProblem.HashMismatch, $"{path}: expected {size} bytes, the file has {input.Length}.", MayPass: false);
            }

            try
            {
                using var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new Failure(PackFetchProblem.DiskError, e.Message, MayPass: false);
            }
        }
    }

    /// <summary>One request: from the start, or the rest of the target's <c>.part</c> file with a range request.</summary>
    private async Task<Failure?> TryDownloadAsync(Uri source, string target, long size, Counter counter, CancellationToken cancellationToken)
    {
        var part = target + PackCache.PartSuffix;
        long existing;
        try
        {
            existing = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (existing > size)
            {
                File.Delete(part);
                existing = 0;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new Failure(PackFetchProblem.DiskError, e.Message, MayPass: false);
        }

        counter.Set(target, existing);
        if (existing == size)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            if (existing > 0)
            {
                request.Headers.Range = new RangeHeaderValue(existing, null);
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // The part doesn't fit the file the server has: start over.
                File.Delete(part);
                return new Failure(PackFetchProblem.HttpError, "HTTP 416: the partial download doesn't fit; starting over", MayPass: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                {
                    return new Failure(PackFetchProblem.FileMissing, $"HTTP {code} {response.ReasonPhrase}".TrimEnd(), MayPass: false);
                }

                var mayPass = code >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
                return new Failure(PackFetchProblem.HttpError, $"HTTP {code} {response.ReasonPhrase}".TrimEnd(), mayPass);
            }

            var resumes = existing > 0
                && response.StatusCode == HttpStatusCode.PartialContent
                && response.Content.Headers.ContentRange is { From: { } from } && from == existing;
            if (!resumes && response.StatusCode == HttpStatusCode.PartialContent)
            {
                // Another range than asked: start over.
                TryDelete(part);
                return new Failure(PackFetchProblem.HttpError, "HTTP 206 with another range than asked; starting over", MayPass: true);
            }

            if (!resumes && existing > 0)
            {
                // A full answer (the server ignored the range): replace the part.
                existing = 0;
                counter.Set(target, 0);
            }

            if (existing + response.Content.Headers.ContentLength > size)
            {
                TryDelete(part);
                return new Failure(PackFetchProblem.HashMismatch, $"The server sends {existing + response.Content.Headers.ContentLength} bytes, the manifest gives {size}.", MayPass: false);
            }

            FileStream output;
            try
            {
                output = new FileStream(part, resumes ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new Failure(PackFetchProblem.DiskError, e.Message, MayPass: false);
            }

            await using (output.ConfigureAwait(false))
            {
                var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    var buffer = new byte[BufferSize];
                    var written = existing;
                    while (true)
                    {
                        timeout.CancelAfter(_options.Timeout);
                        var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        if (written + read > size)
                        {
                            await output.DisposeAsync().ConfigureAwait(false);
                            TryDelete(part);
                            return new Failure(PackFetchProblem.HashMismatch, $"The server sends more than the {size} bytes the manifest gives.", MayPass: false);
                        }

                        try
                        {
                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        }
                        catch (IOException e)
                        {
                            return new Failure(PackFetchProblem.DiskError, e.Message, MayPass: false);
                        }

                        written += read;
                        counter.Add(target, read);
                    }

                    return written == size
                        ? null
                        : new Failure(PackFetchProblem.Offline, $"The connection closed after {written} of {size} bytes.", MayPass: true);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Failure(PackFetchProblem.Timeout, $"No data within {_options.Timeout.TotalSeconds:0.#} s", MayPass: true);
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            return new Failure(PackFetchProblem.Offline, e.Message, MayPass: true);
        }
        catch (UnauthorizedAccessException e)
        {
            return new Failure(PackFetchProblem.DiskError, e.Message, MayPass: false);
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

    private PackFetchResult Fail(RegistryPack pack, PackFetchProblem problem, string? detail, Counter counter)
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
            // Left as a part: the next try checks its length and hash again.
        }
    }

    private readonly record struct Failure(PackFetchProblem Problem, string Detail, bool MayPass);

    /// <summary>Progress over every file of the pack: bytes in place per file, so a resumed part counts once.</summary>
    private sealed class Counter(IProgress<PackFetchProgress>? progress, long total)
    {
        private readonly Dictionary<string, long> _files = new(StringComparer.Ordinal);

        public long Downloaded { get; private set; }

        /// <summary><paramref name="file"/> now holds <paramref name="bytes"/> (a request starting, a file already there).</summary>
        public void Set(string file, long bytes)
        {
            _files[file] = bytes;
            Report();
        }

        /// <summary><paramref name="bytes"/> more received into <paramref name="file"/>.</summary>
        public void Add(string file, long bytes)
        {
            _files[file] = _files.GetValueOrDefault(file) + bytes;
            Downloaded += bytes;
            Report();
        }

        private void Report() => progress?.Report(new PackFetchProgress(_files.Values.Sum(), total));
    }
}
