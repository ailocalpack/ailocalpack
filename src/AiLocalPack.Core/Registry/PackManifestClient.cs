using System.Text;
using System.Text.Json;
using AiLocalPack.Core.Packs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Core.Registry;

/// <summary>Why a pack's manifest couldn't be read.</summary>
public enum PackManifestProblem
{
    None,

    /// <summary>No connection (DNS, refused, reset…).</summary>
    Offline,

    /// <summary>No answer within the timeout.</summary>
    Timeout,

    /// <summary>The server answered with an error status (other than 404 / 410).</summary>
    HttpError,

    /// <summary>No such file: a local registry lacks it or can't read it, or the server answered 404 / 410 (pack removed since the index was built).</summary>
    FileMissing,

    /// <summary>The file is not a valid manifest (its errors in <see cref="PackManifestFetch.Errors"/>).</summary>
    Invalid,

    /// <summary>The manifest is in a newer format: the app needs an update.</summary>
    NewerFormat,

    /// <summary>The manifest is valid but names another pack, game or language than the index entry.</summary>
    Mismatch,
}

/// <summary>A pack's manifest, or why it couldn't be read.</summary>
/// <param name="Detail">Plain English for logs ("HTTP 404 Not Found", "timed out after 15 s").</param>
/// <param name="Bytes">The manifest file as read, when <see cref="Manifest"/> is set (what the pack cache keeps).</param>
/// <param name="Uri">Where the manifest was read from; the pack's other files resolve against it.</param>
public sealed record PackManifestFetch(
    PackManifest? Manifest,
    PackManifestProblem Problem,
    string? Detail = null,
    IReadOnlyList<PackFormatError>? FormatErrors = null,
    byte[]? Bytes = null,
    Uri? Uri = null)
{
    public bool IsLoaded => Manifest is not null;

    public IReadOnlyList<PackFormatError> Errors => FormatErrors ?? [];

    /// <summary>Worth trying again later (no connection, timeout, server error).</summary>
    public bool MayPass => Problem is PackManifestProblem.Offline or PackManifestProblem.Timeout or PackManifestProblem.HttpError;
}

/// <summary>
/// Reads the manifest of one pack the index lists (<see cref="RegistryPack.ManifestPath"/>, relative to
/// <c>index.json</c>): straight from a local registry folder, or one https GET (no retries: the Packs tab has
/// <c>Retry</c>), a timeout and a size cap. The manifest must be valid and be the index entry's pack (ID, game,
/// language). Never throws except for cancellation by the caller.
/// </summary>
public sealed class PackManifestClient
{
    /// <summary>A bigger manifest is refused (thousands of builds × files would still fit).</summary>
    public const long MaxManifestBytes = 8L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly ILogger _logger;

    public PackManifestClient(HttpClient http, TimeSpan? timeout = null, ILogger<PackManifestClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    public async Task<PackManifestFetch> FetchAsync(RegistryLocation location, RegistryPack pack, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(pack);
        Uri uri;
        try
        {
            uri = location.Resolve(pack.ManifestPath);
        }
        catch (UriFormatException e)
        {
            return Fail(pack, PackManifestProblem.Invalid, $"The manifest path {pack.ManifestPath} is not valid: {e.Message}");
        }

        // The index names files relative to itself; anything else (another scheme, http) is refused.
        if (location.IsLocal ? !uri.IsFile : uri.Scheme != Uri.UriSchemeHttps)
        {
            return Fail(pack, PackManifestProblem.Invalid, $"The manifest address {uri} is outside the registry.");
        }

        var (bytes, failure) = uri.IsFile
            ? await ReadLocalAsync(uri.LocalPath, cancellationToken).ConfigureAwait(false)
            : await DownloadAsync(uri, cancellationToken).ConfigureAwait(false);
        if (failure is { } problem)
        {
            return Fail(pack, problem.Problem, problem.Detail);
        }

        var fetch = Check(pack, bytes!);
        return fetch.IsLoaded ? fetch with { Bytes = bytes, Uri = uri } : fetch;
    }

    private PackManifestFetch Check(RegistryPack pack, byte[] bytes)
    {
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Fail(pack, PackManifestProblem.Invalid, "The file is not UTF-8 text.");
        }

        if (IsNewerFormat(text))
        {
            return Fail(pack, PackManifestProblem.NewerFormat, "The manifest is in a newer format; update the app.");
        }

        var read = PackManifestSerializer.Read(text);
        if (read.Value is not { } manifest)
        {
            var fetch = new PackManifestFetch(null, PackManifestProblem.Invalid, string.Join("; ", read.Errors.Take(3)), read.Errors);
            _logger.LogWarning("Manifest of {Game}/{Language}/{Pack} is not valid: {Detail}", pack.Game, pack.Language, pack.Id, fetch.Detail);
            return fetch;
        }

        if (manifest.Id != pack.Id || manifest.Game != pack.Game || manifest.Language != pack.Language)
        {
            return Fail(pack, PackManifestProblem.Mismatch, $"The manifest is for {manifest.Game}/{manifest.Language}/{manifest.Id}.");
        }

        return new PackManifestFetch(manifest, PackManifestProblem.None);
    }

    private static bool IsNewerFormat(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("schemaVersion", out var version)
                && version.TryGetInt32(out var number)
                && number > PackManifest.CurrentSchemaVersion;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<(byte[]? Bytes, (PackManifestProblem Problem, string Detail)? Failure)> ReadLocalAsync(
        string path, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return (null, (PackManifestProblem.FileMissing, $"There is no manifest at {path}."));
            }

            if (info.Length > MaxManifestBytes)
            {
                return (null, (PackManifestProblem.Invalid, $"{path} is too large for a manifest."));
            }

            return (await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, (PackManifestProblem.FileMissing, $"Can't read {path}: {e.Message}"));
        }
    }

    private async Task<(byte[]? Bytes, (PackManifestProblem Problem, string Detail)? Failure)> DownloadAsync(
        Uri uri, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Gone since the index was built (or the saved index is old): the same as a missing local file.
                var gone = response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone;
                return (null, (gone ? PackManifestProblem.FileMissing : PackManifestProblem.HttpError, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd()));
            }

            if (response.Content.Headers.ContentLength > MaxManifestBytes)
            {
                return (null, (PackManifestProblem.Invalid, "The manifest is too large."));
            }

            var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxManifestBytes)
                    {
                        return (null, (PackManifestProblem.Invalid, "The manifest is too large."));
                    }

                    buffer.Write(chunk, 0, read);
                }

                return (buffer.ToArray(), null);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, (PackManifestProblem.Timeout, $"No answer within {_timeout.TotalSeconds:0.#} s"));
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            return (null, (PackManifestProblem.Offline, e.Message));
        }
    }

    private PackManifestFetch Fail(RegistryPack pack, PackManifestProblem problem, string detail)
    {
        _logger.LogWarning("Manifest of {Game}/{Language}/{Pack}: {Problem} ({Detail})", pack.Game, pack.Language, pack.Id, problem, detail);
        return new PackManifestFetch(null, problem, detail);
    }
}
