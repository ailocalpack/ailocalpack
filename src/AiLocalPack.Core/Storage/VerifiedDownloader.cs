using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Core.Storage;

/// <summary>Why a download failed (a subset of <see cref="PackFetchProblem"/>), and whether trying again may help.</summary>
internal readonly record struct DownloadFailure(PackFetchProblem Problem, string Detail, bool MayPass);

/// <summary>
/// Gets one file of known size and SHA-256 into place: a local file is copied, an https one downloaded into
/// <c>&lt;target&gt;.part</c> and resumed with a range request after a dropped connection (a few tries within one call,
/// and in the next call). The file is moved to <c>target</c> only after its size and hash check out; a mismatch deletes
/// it. Shared by the pack fetcher and the font store. Never throws except for cancellation by the caller.
/// </summary>
internal sealed class VerifiedDownloader(HttpClient http, TimeProvider time, ILogger logger, PackFetchOptions options)
{
    private const int BufferSize = 81920;

    private readonly HttpClient _http = http;
    private readonly TimeProvider _time = time;
    private readonly ILogger _logger = logger;
    private readonly PackFetchOptions _options = options;

    /// <summary>Gets <paramref name="source"/> into <paramref name="target"/> verified, or says why not.</summary>
    public async Task<DownloadFailure?> DownloadAsync(Uri source, string target, long size, string sha256, DownloadCounter counter, CancellationToken cancellationToken)
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
            return new DownloadFailure(PackFetchProblem.DiskError, e.Message, MayPass: false);
        }

        DownloadFailure? failure = null;
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

    private static DownloadFailure? Verify(string part, string target, long size, string sha256)
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
                return new DownloadFailure(PackFetchProblem.HashMismatch, $"{Path.GetFileName(target)}: expected {size} bytes with SHA-256 {sha256}, got {length} bytes with {actual}; deleted.", MayPass: false);
            }

            File.Move(part, target, overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new DownloadFailure(PackFetchProblem.DiskError, e.Message, MayPass: false);
        }
    }

    private static DownloadFailure? CopyLocal(string path, string part, long size)
    {
        FileStream input;
        try
        {
            input = File.OpenRead(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new DownloadFailure(PackFetchProblem.FileMissing, $"There is no file at {path}.", MayPass: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new DownloadFailure(PackFetchProblem.FileMissing, $"Can't read {path}: {e.Message}", MayPass: false);
        }

        using (input)
        {
            if (input.Length != size)
            {
                return new DownloadFailure(PackFetchProblem.HashMismatch, $"{path}: expected {size} bytes, the file has {input.Length}.", MayPass: false);
            }

            try
            {
                using var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new DownloadFailure(PackFetchProblem.DiskError, e.Message, MayPass: false);
            }
        }
    }

    /// <summary>One request: from the start, or the rest of the target's <c>.part</c> file with a range request.</summary>
    private async Task<DownloadFailure?> TryDownloadAsync(Uri source, string target, long size, DownloadCounter counter, CancellationToken cancellationToken)
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
            return new DownloadFailure(PackFetchProblem.DiskError, e.Message, MayPass: false);
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
                return new DownloadFailure(PackFetchProblem.HttpError, "HTTP 416: the partial download doesn't fit; starting over", MayPass: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                {
                    return new DownloadFailure(PackFetchProblem.FileMissing, $"HTTP {code} {response.ReasonPhrase}".TrimEnd(), MayPass: false);
                }

                var mayPass = code >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
                return new DownloadFailure(PackFetchProblem.HttpError, $"HTTP {code} {response.ReasonPhrase}".TrimEnd(), mayPass);
            }

            var resumes = existing > 0
                && response.StatusCode == HttpStatusCode.PartialContent
                && response.Content.Headers.ContentRange is { From: { } from } && from == existing;
            if (!resumes && response.StatusCode == HttpStatusCode.PartialContent)
            {
                // Another range than asked: start over.
                TryDelete(part);
                return new DownloadFailure(PackFetchProblem.HttpError, "HTTP 206 with another range than asked; starting over", MayPass: true);
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
                return new DownloadFailure(PackFetchProblem.HashMismatch, $"The server sends {existing + response.Content.Headers.ContentLength} bytes, the manifest gives {size}.", MayPass: false);
            }

            FileStream output;
            try
            {
                output = new FileStream(part, resumes ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new DownloadFailure(PackFetchProblem.DiskError, e.Message, MayPass: false);
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
                            return new DownloadFailure(PackFetchProblem.HashMismatch, $"The server sends more than the {size} bytes the manifest gives.", MayPass: false);
                        }

                        try
                        {
                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        }
                        catch (IOException e)
                        {
                            return new DownloadFailure(PackFetchProblem.DiskError, e.Message, MayPass: false);
                        }

                        written += read;
                        counter.Add(target, read);
                    }

                    return written == size
                        ? null
                        : new DownloadFailure(PackFetchProblem.Offline, $"The connection closed after {written} of {size} bytes.", MayPass: true);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DownloadFailure(PackFetchProblem.Timeout, $"No data within {_options.Timeout.TotalSeconds:0.#} s", MayPass: true);
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            return new DownloadFailure(PackFetchProblem.Offline, e.Message, MayPass: true);
        }
        catch (UnauthorizedAccessException e)
        {
            return new DownloadFailure(PackFetchProblem.DiskError, e.Message, MayPass: false);
        }
    }

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
}

/// <summary>Progress over several files: bytes in place per file, so a resumed part counts once.</summary>
internal sealed class DownloadCounter(long total, Action<long, long>? report)
{
    private readonly Dictionary<string, long> _files = new(StringComparer.Ordinal);

    public long Total { get; } = total;

    /// <summary>Bytes received in this call.</summary>
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

    private void Report() => report?.Invoke(_files.Values.Sum(), Total);
}
