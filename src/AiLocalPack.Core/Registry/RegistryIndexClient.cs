using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Core.Registry;

/// <param name="Timeout">For one attempt, headers and body.</param>
/// <param name="Attempts">Tries in all for errors that may pass (no connection, timeout, 5xx, 408, 429).</param>
/// <param name="RetryDelay">Wait before the second try; doubled before each next one.</param>
/// <param name="MaxIndexBytes">A bigger index is refused (protects memory against a broken or hostile server).</param>
public sealed record RegistryIndexClientOptions(
    TimeSpan Timeout,
    int Attempts = 3,
    TimeSpan RetryDelay = default,
    long MaxIndexBytes = 64L * 1024 * 1024)
{
    public static RegistryIndexClientOptions Default { get; } = new(TimeSpan.FromSeconds(15), 3, TimeSpan.FromSeconds(1));
}

/// <summary>
/// Downloads the registry <c>index.json</c> and keeps the last good copy in <see cref="AppPaths.RegistryCache"/>.
/// Conditional GET (<c>If-None-Match</c> / <c>If-Modified-Since</c>) so an unchanged index costs a 304; a timeout per
/// attempt and retries with doubling waits for errors that may pass. When the registry can't be read (offline, server
/// error, a malformed or newer-format file) the saved copy is returned as <see cref="RegistryIndexState.Stale"/> with
/// the date it was last confirmed, and the saved copy is never replaced by a bad one. A local registry folder is read
/// directly, without cache. Never throws except for cancellation by the caller.
/// </summary>
public sealed class RegistryIndexClient
{
    private const int MetaSchemaVersion = 1;

    private readonly HttpClient _http;
    private readonly AppPaths _paths;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly RegistryIndexClientOptions _options;

    public RegistryIndexClient(
        HttpClient http,
        AppPaths paths,
        TimeProvider? time = null,
        ILogger<RegistryIndexClient>? logger = null,
        RegistryIndexClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(paths);
        _http = http;
        _paths = paths;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _options = options ?? RegistryIndexClientOptions.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.Attempts, 1, nameof(options));
    }

    private string CacheIndexFile => Path.Combine(_paths.RegistryCache, RegistryLocation.IndexFileName);

    private string CacheMetaFile => Path.Combine(_paths.RegistryCache, "index.meta.json");

    /// <summary>The saved copy for <paramref name="location"/> as a stale snapshot, without network; <c>null</c> when there is none.</summary>
    public RegistryIndexSnapshot? LoadCached(RegistryLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (location.IsLocal || ReadCache(location) is not { } cached)
        {
            return null;
        }

        return new RegistryIndexSnapshot(location, RegistryIndexState.Stale, cached.Index, cached.Meta.FetchedAt, SkippedEntries: cached.Skipped);
    }

    public async Task<RegistryIndexSnapshot> FetchAsync(RegistryLocation location, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        return location.IsLocal
            ? await ReadLocalAsync(location, cancellationToken).ConfigureAwait(false)
            : await DownloadAsync(location, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RegistryIndexSnapshot> ReadLocalAsync(RegistryLocation location, CancellationToken cancellationToken)
    {
        var path = location.LocalIndexPath!;
        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return Unavailable(location, RegistryIndexProblem.LocalFileMissing, $"There is no {RegistryLocation.IndexFileName} at {path}.");
            }

            if (info.Length > _options.MaxIndexBytes)
            {
                return Unavailable(location, RegistryIndexProblem.Malformed, $"{path} is larger than {Megabytes(_options.MaxIndexBytes)}.");
            }

            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Reading the local registry index {Path} failed", path);
            return Unavailable(location, RegistryIndexProblem.LocalFileMissing, $"Can't read {path}: {e.Message}");
        }

        var read = Parse(bytes);
        if (read.Index is null)
        {
            return Unavailable(location, read.IsNewerFormat ? RegistryIndexProblem.NewerFormat : RegistryIndexProblem.Malformed, Describe(read));
        }

        LogSkipped(location, read);
        return new RegistryIndexSnapshot(location, RegistryIndexState.Fresh, read.Index, _time.GetUtcNow(), SkippedEntries: read.Errors);
    }

    private async Task<RegistryIndexSnapshot> DownloadAsync(RegistryLocation location, CancellationToken cancellationToken)
    {
        var cached = ReadCache(location);
        Failure failure = default;
        for (var attempt = 1; attempt <= _options.Attempts; attempt++)
        {
            if (attempt > 1)
            {
                var wait = _options.RetryDelay * Math.Pow(2, attempt - 2);
                _logger.LogInformation("Registry index: {Problem} ({Detail}); try {Attempt} of {Attempts} in {Wait}", failure.Problem, failure.Detail, attempt, _options.Attempts, wait);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
                }
            }

            var outcome = await TryOnceAsync(location, cached, cancellationToken).ConfigureAwait(false);
            if (outcome.Snapshot is { } snapshot)
            {
                return snapshot;
            }

            failure = outcome.Failure;
            if (!failure.MayPass)
            {
                break;
            }
        }

        _logger.LogWarning("Registry index {Location} unavailable: {Problem} ({Detail}); {Fallback}", location, failure.Problem, failure.Detail, cached is null ? "no saved copy" : "using the saved copy");
        return cached is null
            ? Unavailable(location, failure.Problem, failure.Detail)
            : new RegistryIndexSnapshot(location, RegistryIndexState.Stale, cached.Index, cached.Meta.FetchedAt, failure.Problem, failure.Detail, cached.Skipped);
    }

    private async Task<(RegistryIndexSnapshot? Snapshot, Failure Failure)> TryOnceAsync(
        RegistryLocation location, CachedIndex? cached, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, location.IndexUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (cached is not null)
            {
                if (cached.Meta.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag))
                {
                    request.Headers.IfNoneMatch.Add(tag);
                }

                if (cached.Meta.LastModified is { } modified
                    && DateTimeOffset.TryParse(modified, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var since))
                {
                    request.Headers.IfModifiedSince = since;
                }
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (cached is null)
                {
                    return (null, new Failure(RegistryIndexProblem.HttpError, "HTTP 304 without a saved copy", MayPass: false));
                }

                var now = _time.GetUtcNow();
                SaveMeta(cached.Meta with
                {
                    ETag = response.Headers.ETag?.ToString() ?? cached.Meta.ETag,
                    LastModified = response.Content.Headers.LastModified?.ToString("R") ?? cached.Meta.LastModified,
                    FetchedAt = now,
                });
                return (new RegistryIndexSnapshot(location, RegistryIndexState.Fresh, cached.Index, now, SkippedEntries: cached.Skipped), default);
            }

            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                var mayPass = code >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
                return (null, new Failure(RegistryIndexProblem.HttpError, $"HTTP {code} {response.ReasonPhrase}".TrimEnd(), mayPass));
            }

            var bytes = await ReadLimitedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            if (bytes is null)
            {
                return (null, new Failure(RegistryIndexProblem.Malformed, $"The index is larger than {Megabytes(_options.MaxIndexBytes)}.", MayPass: false));
            }

            var read = Parse(bytes);
            if (read.Index is null)
            {
                // The saved copy stays: a bad download never replaces a good one.
                var problem = read.IsNewerFormat ? RegistryIndexProblem.NewerFormat : RegistryIndexProblem.Malformed;
                return (null, new Failure(problem, Describe(read), MayPass: false));
            }

            LogSkipped(location, read);
            var fetchedAt = _time.GetUtcNow();
            SaveCache(bytes, new RegistryCacheMetaJson(
                MetaSchemaVersion,
                location.IndexUri.AbsoluteUri,
                response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified?.ToString("R"),
                fetchedAt,
                Sha256(bytes)));
            return (new RegistryIndexSnapshot(location, RegistryIndexState.Fresh, read.Index, fetchedAt, SkippedEntries: read.Errors), default);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, new Failure(RegistryIndexProblem.Timeout, $"No answer within {_options.Timeout.TotalSeconds:0.#} s", MayPass: true));
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            return (null, new Failure(RegistryIndexProblem.Offline, e.Message, MayPass: true));
        }
    }

    /// <summary>The body, or <c>null</c> when it is over the size limit.</summary>
    private async Task<byte[]?> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var max = _options.MaxIndexBytes;
        if (content.Headers.ContentLength > max)
        {
            return null;
        }

        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > max)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    private static RegistryIndexReadResult Parse(byte[] bytes)
    {
        try
        {
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return RegistryIndexSerializer.Read(text.TrimStart('﻿'));
        }
        catch (DecoderFallbackException)
        {
            return new RegistryIndexReadResult(null, [new Packs.PackFormatError("", "The file is not UTF-8 text.")]);
        }
    }

    private static string Describe(RegistryIndexReadResult read) =>
        read.Errors.Count == 0 ? "Not a valid index." : string.Join("; ", read.Errors.Take(3));

    private void LogSkipped(RegistryLocation location, RegistryIndexReadResult read)
    {
        if (read.Errors.Count > 0)
        {
            _logger.LogWarning("Registry index {Location}: {Count} damaged entries left out, first: {First}", location, read.Errors.Count, read.Errors[0]);
        }
    }

    private RegistryIndexSnapshot Unavailable(RegistryLocation location, RegistryIndexProblem problem, string detail) =>
        new(location, RegistryIndexState.Unavailable, null, null, problem, detail);

    /// <summary>The saved copy, when it is from <paramref name="location"/>, complete and still readable.</summary>
    private CachedIndex? ReadCache(RegistryLocation location)
    {
        try
        {
            if (!File.Exists(CacheMetaFile) || !File.Exists(CacheIndexFile))
            {
                return null;
            }

            var meta = JsonSerializer.Deserialize(File.ReadAllText(CacheMetaFile), RegistryJsonContext.Default.RegistryCacheMetaJson);
            if (meta is not { SchemaVersion: MetaSchemaVersion, FetchedAt: not null } || meta.Source != location.IndexUri.AbsoluteUri)
            {
                return null;
            }

            var bytes = File.ReadAllBytes(CacheIndexFile);
            if (meta.Sha256 != Sha256(bytes))
            {
                _logger.LogWarning("The saved registry index doesn't match its record; ignored");
                return null;
            }

            var read = Parse(bytes);
            if (read.Index is null)
            {
                _logger.LogWarning("The saved registry index can't be read ({Detail}); ignored", Describe(read));
                return null;
            }

            return new CachedIndex(read.Index, meta, read.Errors);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(e, "Reading the saved registry index failed; ignored");
            return null;
        }
    }

    /// <summary>Index first, then its record: a crash in between leaves a record that doesn't match, so the copy isn't used.</summary>
    private void SaveCache(byte[] bytes, RegistryCacheMetaJson meta)
    {
        try
        {
            AtomicFile.WriteAllBytes(CacheIndexFile, bytes);
            SaveMeta(meta);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Saving the registry index to {Folder} failed", _paths.RegistryCache);
        }
    }

    private void SaveMeta(RegistryCacheMetaJson meta)
    {
        try
        {
            AtomicFile.WriteAllBytes(CacheMetaFile, JsonSerializer.SerializeToUtf8Bytes(meta, RegistryJsonContext.Default.RegistryCacheMetaJson));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Saving the registry index record failed");
        }
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Megabytes(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024 * 1024)} MB" : $"{bytes} bytes";

    private sealed record CachedIndex(RegistryIndex Index, RegistryCacheMetaJson Meta, IReadOnlyList<Packs.PackFormatError> Skipped);

    private readonly record struct Failure(RegistryIndexProblem Problem, string Detail, bool MayPass);
}
