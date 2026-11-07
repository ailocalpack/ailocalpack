using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Core.Fonts;

/// <summary>Why a whitelisted font file couldn't be had.</summary>
public enum FontFetchProblem
{
    None,

    /// <summary>No connection.</summary>
    Offline,

    /// <summary>No data within the timeout.</summary>
    Timeout,

    /// <summary>The server answered with an error status (other than 404 / 410).</summary>
    HttpError,

    /// <summary>The file is gone from its address (HTTP 404 / 410), or the app lacks a file it should ship.</summary>
    FileMissing,

    /// <summary>The download doesn't have the size or SHA-256 the whitelist gives; it was deleted.</summary>
    HashMismatch,

    /// <summary>The cache folder can't be written or read.</summary>
    DiskError,
}

/// <summary>A font file's bytes, or why there are none.</summary>
/// <param name="Downloaded">Bytes received in this call (0 for a bundled or cached file).</param>
public sealed record FontFetchResult(byte[]? Content, FontFetchProblem Problem, string? Detail = null, long Downloaded = 0)
{
    /// <summary>Worth trying again later (no connection, timeout, server error); a partial download resumes.</summary>
    public bool MayPass => Problem is FontFetchProblem.Offline or FontFetchProblem.Timeout or FontFetchProblem.HttpError;
}

/// <summary>
/// Gives the bytes of a whitelisted font file (<see cref="FontWhitelist"/>): a bundled one from inside the app, a
/// downloaded one from <c>&lt;cache&gt;/fonts/&lt;font id&gt;/&lt;first 16 hex of its SHA-256&gt;-&lt;file&gt;</c>, downloading it
/// first (resumable, checked against the whitelist's size and SHA-256 before it enters the cache). A cached file of
/// the right size is used as is (its hash was checked when it came in). Never throws except for cancellation.
/// </summary>
public sealed class FontStore
{
    private readonly string _root;
    private readonly VerifiedDownloader _downloader;
    private readonly ILogger _logger;

    public FontStore(string cacheRoot, HttpClient http, TimeProvider? time = null, ILogger<FontStore>? logger = null, PackFetchOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheRoot);
        ArgumentNullException.ThrowIfNull(http);
        _root = Path.GetFullPath(cacheRoot);
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _downloader = new VerifiedDownloader(http, time ?? TimeProvider.System, _logger, options ?? PackFetchOptions.Default);
    }

    public FontStore(AppPaths paths, HttpClient http, TimeProvider? time = null, ILogger<FontStore>? logger = null)
        : this(paths?.Fonts ?? throw new ArgumentNullException(nameof(paths)), http, time, logger)
    {
    }

    /// <summary>Where a downloaded file is kept.</summary>
    public string CachedPath(WhitelistFont font, WhitelistFontFile file)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(file);
        return Path.Combine(_root, font.Id, $"{file.Sha256[..16]}-{file.FileName}");
    }

    /// <summary>Whether the file is at hand without a download (bundled, or already in the cache).</summary>
    public bool IsAvailable(WhitelistFont font, WhitelistFontFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.IsBundled)
        {
            return true;
        }

        var path = CachedPath(font, file);
        return File.Exists(path) && new FileInfo(path).Length == file.Size;
    }

    public async Task<FontFetchResult> GetAsync(
        WhitelistFont font,
        WhitelistFontFile file,
        IProgress<PackFetchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(file);
        if (file.IsBundled)
        {
            return ReadBundled(file);
        }

        var target = CachedPath(font, file);
        if (!IsAvailable(font, file))
        {
            var counter = new DownloadCounter(file.Size, progress is null ? null : (done, total) => progress.Report(new PackFetchProgress(done, total)));
            if (await _downloader.DownloadAsync(file.Url!, target, file.Size, file.Sha256, counter, cancellationToken).ConfigureAwait(false) is { } failure)
            {
                _logger.LogWarning("Font {Font} ({File}): {Problem} ({Detail})", font.Id, file.FileName, failure.Problem, failure.Detail);
                return new FontFetchResult(null, Map(failure.Problem), failure.Detail, counter.Downloaded);
            }

            _logger.LogInformation("Font {Font} ({File}) downloaded into {Path}", font.Id, file.FileName, target);
            return Read(target, counter.Downloaded);
        }

        progress?.Report(new PackFetchProgress(file.Size, file.Size));
        return Read(target, 0);
    }

    private static FontFetchResult ReadBundled(WhitelistFontFile file)
    {
        using var stream = FontWhitelist.OpenBundled(file);
        if (stream is null)
        {
            return new FontFetchResult(null, FontFetchProblem.FileMissing, $"The app doesn't ship {file.FileName}.");
        }

        using var memory = new MemoryStream((int)file.Size);
        stream.CopyTo(memory);
        return new FontFetchResult(memory.ToArray(), FontFetchProblem.None);
    }

    private FontFetchResult Read(string path, long downloaded)
    {
        try
        {
            return new FontFetchResult(File.ReadAllBytes(path), FontFetchProblem.None, Downloaded: downloaded);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Can't read the cached font {Path}", path);
            return new FontFetchResult(null, FontFetchProblem.DiskError, e.Message, downloaded);
        }
    }

    private static FontFetchProblem Map(PackFetchProblem problem) => problem switch
    {
        PackFetchProblem.Offline => FontFetchProblem.Offline,
        PackFetchProblem.Timeout => FontFetchProblem.Timeout,
        PackFetchProblem.HttpError => FontFetchProblem.HttpError,
        PackFetchProblem.FileMissing => FontFetchProblem.FileMissing,
        PackFetchProblem.HashMismatch => FontFetchProblem.HashMismatch,
        _ => FontFetchProblem.DiskError,
    };
}
