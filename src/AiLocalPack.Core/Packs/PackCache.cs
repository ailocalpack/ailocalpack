using System.Security.Cryptography;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Core.Packs;

/// <summary>
/// A pack revision in the cache: its manifest and the verified files it downloads (string table, binary patch).
/// </summary>
/// <param name="Folder">The cache entry (<see cref="PackCache.EntryFolder"/>).</param>
/// <param name="ManifestSha256">SHA-256 of the manifest file, part of the entry's name.</param>
public sealed record CachedPack(PackManifest Manifest, string Folder, string ManifestSha256)
{
    public string ManifestFile => Path.Combine(Folder, PackCache.ManifestFileName);

    /// <summary>The gzip string table, or <c>null</c> for a patch pack without one.</summary>
    public string? StringsFile => Manifest.Strings is { } strings ? PackCache.PathIn(Folder, strings.Path) : null;

    /// <summary>The binary patch archive (<see cref="PackCache.PatchFileName"/>, or <see cref="PatchArchive"/>), or <c>null</c> for a recipe pack.</summary>
    public string? PatchFile => Manifest.Patch is null ? null : PatchArchive ?? Path.Combine(Folder, PackCache.PatchFileName);

    /// <summary>Where the archive is when it isn't in the folder: a local patch pack's (task 57, <see cref="LocalPackStore.PatchArchivePath"/>).</summary>
    public string? PatchArchive { get; init; }

    /// <summary>Reads the string table (decompressed); a recipe pack always has one.</summary>
    public PackReadResult<StringTable> ReadStrings()
    {
        if (StringsFile is not { } file)
        {
            return new PackReadResult<StringTable>(null, [new PackFormatError("strings", "The pack has no string table.")]);
        }

        using var stream = File.OpenRead(file);
        return StringTableSerializer.Read(stream);
    }
}

/// <summary>
/// Downloaded packs in <see cref="AppPaths.Packs"/>, one folder per pack revision:
/// <c>&lt;game folder&gt;/&lt;language&gt;/&lt;pack id&gt;/r&lt;revision&gt;-&lt;manifest SHA-256&gt;/</c> holding
/// <c>manifest.json</c> (the exact bytes the registry served), the string table at its manifest path and the patch as
/// <c>patch.zip</c>. Each part of the key is a fixed-alphabet slug or hex and the parts are folder levels, so two
/// different packs or manifests never share an entry. Files enter an entry only after their hash was checked
/// (downloaded as <c>*.part</c>, renamed once verified) and <c>manifest.json</c> is written last, so an entry with a
/// readable manifest and every file of the right size is complete. Never throws for disk state.
/// </summary>
public sealed class PackCache
{
    public const string ManifestFileName = "manifest.json";

    /// <summary>The binary patch archive of a patch pack (see CLAUDE.md → Pack format).</summary>
    public const string PatchFileName = "patch.zip";

    /// <summary>A download in progress, kept between attempts so it resumes.</summary>
    public const string PartSuffix = ".part";

    private readonly ILogger _logger;

    public PackCache(AppPaths paths, ILogger<PackCache>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Root = paths.Packs;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    public string Root { get; }

    /// <summary>The folder of every cached revision of one pack.</summary>
    public string PackFolder(GameId game, string language, string packId)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        if (!PackFormat.IsSlug(packId))
        {
            throw new ArgumentException($"'{packId}' is not a pack ID.", nameof(packId));
        }

        return Path.Combine(Root, game.ToString().Replace(':', '-'), language, packId);
    }

    /// <summary>The entry of one manifest: <c>r&lt;revision&gt;-&lt;manifest SHA-256&gt;</c> in its pack folder.</summary>
    public string EntryFolder(PackManifest manifest, string manifestSha256)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!PackFormat.IsSha256(manifestSha256))
        {
            throw new ArgumentException("Not a SHA-256 hex string.", nameof(manifestSha256));
        }

        return Path.Combine(PackFolder(manifest.Game, manifest.Language, manifest.Id), $"r{manifest.Revision}-{manifestSha256}");
    }

    /// <summary>The complete entry for these manifest bytes, or <c>null</c>.</summary>
    public CachedPack? Find(PackManifest manifest, byte[] manifestBytes)
    {
        ArgumentNullException.ThrowIfNull(manifestBytes);
        var sha = Sha256Hex(manifestBytes);
        return Open(EntryFolder(manifest, sha), manifest.Game, manifest.Language, manifest.Id);
    }

    /// <summary>
    /// The newest complete entry of the pack's <paramref name="revision"/> (used offline, when the manifest can't be
    /// fetched), or <c>null</c>.
    /// </summary>
    public CachedPack? Find(GameId game, string language, string packId, int revision)
    {
        var folder = PackFolder(game, language, packId);
        try
        {
            if (!Directory.Exists(folder))
            {
                return null;
            }

            return Directory.EnumerateDirectories(folder, $"r{revision}-*")
                .Select(d => Open(d, game, language, packId))
                .OfType<CachedPack>()
                .Where(p => p.Manifest.Revision == revision)
                .OrderByDescending(p => p.Manifest.Updated)
                .ThenByDescending(p => File.GetLastWriteTimeUtc(p.ManifestFile))
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Reading the pack cache {Folder} failed", folder);
            return null;
        }
    }

    /// <summary>Bytes the cache takes on disk, unfinished downloads included (Settings → Storage).</summary>
    public long Size()
    {
        try
        {
            return Directory.Exists(Root)
                ? new DirectoryInfo(Root).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }).Sum(f => f.Length)
                : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Measuring the pack cache failed");
            return 0;
        }
    }

    /// <summary>Deletes every cached pack; <c>false</c> when something couldn't be deleted (in use).</summary>
    public bool Clear()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Clearing the pack cache failed");
            return false;
        }
    }

    /// <summary>A manifest-relative path (validated: relative, '/'-separated, no <c>..</c>) inside <paramref name="folder"/>.</summary>
    internal static string PathIn(string folder, string relativePath) =>
        Path.Combine([folder, .. relativePath.Split('/')]);

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The entry when it is complete and belongs to this pack.</summary>
    private CachedPack? Open(string folder, GameId game, string language, string packId)
    {
        var manifestFile = Path.Combine(folder, ManifestFileName);
        try
        {
            if (!File.Exists(manifestFile))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(manifestFile);
            var sha = Sha256Hex(bytes);
            if (!Path.GetFileName(folder).EndsWith("-" + sha, StringComparison.Ordinal))
            {
                _logger.LogWarning("Cached manifest {File} doesn't match its entry; ignored", manifestFile);
                return null;
            }

            using var stream = new MemoryStream(bytes, writable: false);
            if (PackManifestSerializer.Read(stream).Value is not { } manifest
                || manifest.Game != game || manifest.Language != language || manifest.Id != packId)
            {
                return null;
            }

            var pack = new CachedPack(manifest, folder, sha);
            if (manifest.Strings is { } strings && !HasSize(pack.StringsFile!, strings.Size))
            {
                return null;
            }

            if (manifest.Patch is { } patch && !HasSize(pack.PatchFile!, patch.Size))
            {
                return null;
            }

            return pack;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Reading the cached pack {Folder} failed", folder);
            return null;
        }
    }

    private static bool HasSize(string file, long size)
    {
        var info = new FileInfo(file);
        return info.Exists && info.Length == size;
    }
}
