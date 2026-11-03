using System.Globalization;
using System.Net;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Covers;

/// <summary>
/// Finds a game's portrait cover (Steam <c>library_600x900.jpg</c>, 2:3) as a local file. Order: Steam's own library
/// cache (<c>appcache/librarycache/&lt;appid&gt;/[&lt;hash&gt;/]library_600x900.jpg</c> since 2024, the older flat
/// <c>&lt;appid&gt;_library_600x900.jpg</c>), our download cache (<see cref="AppPaths.Covers"/>), then the Steam CDN.
/// A cover the CDN doesn't have is remembered for <see cref="MissRetry"/> so it isn't asked for on every start; a
/// network failure isn't remembered (offline now, maybe not next time). <c>null</c> = no cover, show the placeholder.
/// Never throws for disk or network state.
/// </summary>
public sealed class CoverResolver
{
    public const string FileName = "library_600x900.jpg";

    /// <summary>How long a CDN miss is trusted before asking again (a game may get its art later).</summary>
    public static readonly TimeSpan MissRetry = TimeSpan.FromDays(7);

    /// <summary>CDN locations, tried in order: the current store asset host, then the older one. A 404 / 403 moves on.</summary>
    public static IReadOnlyList<string> CdnUrlTemplates { get; } =
    [
        "https://shared.steamstatic.com/store_item_assets/steam/apps/{0}/" + FileName,
        "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/" + FileName,
    ];

    private readonly Func<IReadOnlyList<string>> _steamRoots;
    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <param name="steamRoots">Steam installs to look in (<c>SteamLocator.FindRoots</c>); asked once per resolve.</param>
    public CoverResolver(Func<IReadOnlyList<string>> steamRoots, HttpClient http, AppPaths paths, TimeProvider time, ILogger<CoverResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(steamRoots);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _steamRoots = steamRoots;
        _http = http;
        _cacheDir = paths.Covers;
        _time = time;
        _logger = logger;
    }

    /// <summary>Path of the cover image, or <c>null</c> (manual game, no art anywhere, offline).</summary>
    public async Task<string?> ResolveAsync(Game game, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.Id.SteamAppId is not { } appId)
        {
            return null;
        }

        var id = appId.ToString(CultureInfo.InvariantCulture);
        if (FindInSteamCache(id) is { } local)
        {
            return local;
        }

        var cached = Path.Combine(_cacheDir, $"steam-{id}.jpg");
        if (File.Exists(cached))
        {
            return cached;
        }

        var miss = Path.Combine(_cacheDir, $"steam-{id}.miss");
        if (IsRecentMiss(miss))
        {
            return null;
        }

        return await DownloadAsync(id, cached, miss, cancellationToken).ConfigureAwait(false);
    }

    private string? FindInSteamCache(string id)
    {
        IReadOnlyList<string> roots;
        try
        {
            roots = _steamRoots();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Can't list Steam roots for covers");
            return null;
        }

        foreach (var root in roots)
        {
            var cache = Path.Combine(root, "appcache", "librarycache");
            var folder = Path.Combine(cache, id);
            var candidates = new List<string> { Path.Combine(folder, FileName) };
            try
            {
                // Since 2024 some covers sit one level deeper, in a folder named after the asset hash.
                if (Directory.Exists(folder))
                {
                    candidates.AddRange(Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal).Select(d => Path.Combine(d, FileName)));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(e, "Can't read Steam cover folder {Folder}", folder);
            }

            candidates.Add(Path.Combine(cache, $"{id}_{FileName}"));
            if (candidates.FirstOrDefault(IsNonEmptyFile) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private bool IsRecentMiss(string marker)
    {
        try
        {
            return File.Exists(marker)
                && DateTimeOffset.TryParse(File.ReadAllText(marker), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                && _time.GetUtcNow() - at < MissRetry;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<string?> DownloadAsync(string id, string target, string missMarker, CancellationToken cancellationToken)
    {
        foreach (var template in CdnUrlTemplates)
        {
            var url = string.Format(CultureInfo.InvariantCulture, template, id);
            byte[] bytes;
            try
            {
                using var response = await _http.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
                {
                    continue;
                }

                response.EnsureSuccessStatusCode();
                bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                _logger.LogInformation("Can't download cover {Url}: {Reason}", url, e.Message);
                return null;
            }

            if (bytes.Length == 0)
            {
                continue;
            }

            try
            {
                AtomicFile.WriteAllBytes(target, bytes);
                return target;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(e, "Can't cache cover {Path}", target);
                return null;
            }
        }

        RememberMiss(missMarker);
        return null;
    }

    private void RememberMiss(string marker)
    {
        try
        {
            // The miss time is stored in the file: file times can't be trusted across copies and clock changes.
            AtomicFile.WriteAllBytes(marker, System.Text.Encoding.UTF8.GetBytes(_time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Can't remember missing cover {Path}", marker);
        }
    }

    private static bool IsNonEmptyFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
