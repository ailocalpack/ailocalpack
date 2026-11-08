using System.Text.RegularExpressions;
using AiLocalPack.Toolkit.Engines;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using AiLocalPack.Toolkit.Validation;

namespace AiLocalPack.Toolkit.Recipes.Unreal;

/// <summary>
/// UE4/5 localization resources (<c>.locres</c> + <c>.locmeta</c>) in standard pak or indexed IoStore containers.
/// Extract reads each target's native culture (from its locmeta, English without one). Rebuild writes the translated
/// resources into one separate mod pak, <c>Content/Paks/~mods/ailocalpack_P.pak</c> (with an empty <c>.utoc</c>/<c>.ucas</c>
/// pair in an IoStore game, which mounts a pak only with one): into the game's own culture of
/// the table's language when it ships one (<see cref="UnrealCultures"/>), else over the native culture (the player
/// keeps the game on that language, <see cref="RebuildResult.InGameLanguage"/>). No AES key is guessed, no original
/// archive is rewritten, no game code runs.
/// </summary>
public sealed partial class UnrealRecipe : IRecipeTool
{
    public const string ToolId = "unreal";

    /// <summary>The mod pak's name: <c>_P</c> makes Unreal mount it after (over) the game's own paks.</summary>
    public const string ModName = "ailocalpack_P.pak";

    /// <summary>The empty IoStore pair written next to the mod pak in an IoStore game (<see cref="UnrealIoStoreWriter"/>).</summary>
    public const string ModToc = "ailocalpack_P.utoc";

    /// <inheritdoc cref="ModToc"/>
    public const string ModData = "ailocalpack_P.ucas";

    private const long MaxResourceSize = 32L << 20;

    public string Id => ToolId;

    public Version Version { get; } = new(1, 0, 0);

    public GameEngine Engine => GameEngine.Unreal;

    /// <summary>Unreal's markup: rich text tags pair up (<c>&lt;Bold&gt;</c> … <c>&lt;/&gt;</c>), FText arguments and printf fields don't.</summary>
    public TextMarkup Markup => UnrealMarkup;

    public RecipeExtraction Extract(string gameDir, CancellationToken cancellationToken = default)
    {
        using var game = Load(gameDir, cancellationToken);
        var strings = game.Sources
            .SelectMany(s => s.Resource.Entries.Select(e => new ExtractedString(
                Key(s.Path, e),
                e.Text,
                $"Unreal localization: {s.Path}; namespace {e.Namespace}; key {e.Key}. Native culture {s.Culture}.",
                null,
                UnrealMarkup.Placeholders(e.Text))))
            .OrderBy(s => s.Key, StringComparer.Ordinal)
            .ToList();
        return new RecipeExtraction(Id, Version, strings, SourceFiles(game.Sources));
    }

    public RebuildResult Rebuild(string gameDir, string language, IReadOnlyDictionary<string, string> translations, IRebuildOutput output, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        if (language.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
        {
            throw new ArgumentException("Invalid Unreal culture tag.", nameof(language));
        }

        ArgumentNullException.ThrowIfNull(translations);
        ArgumentNullException.ThrowIfNull(output);
        using var game = Load(gameDir, cancellationToken);
        var known = game.Sources.SelectMany(s => s.Resource.Entries.Select(e => Key(s.Path, e))).ToHashSet(StringComparer.Ordinal);
        var unknown = translations.Keys.Where(k => !known.Contains(k)).Order(StringComparer.Ordinal).ToList();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        string? inGame = null;
        var applied = 0;
        foreach (var source in game.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chosen = source.Resource.Entries
                .Where(e => translations.TryGetValue(Key(source.Path, e), out var text) && text.Length > 0 && text != e.Text)
                .ToList();
            if (chosen.Count == 0)
            {
                continue;
            }

            var culture = UnrealCultures.Pick(language, source.Cultures);
            if (culture is null)
            {
                // The game has no such culture: the translation goes over the native one. A culture of our own would
                // need the game's config and its language menu to list it, which a pak can't promise.
                inGame ??= source.Culture;
            }

            var destination = culture is null || culture.Equals(source.Culture, StringComparison.OrdinalIgnoreCase)
                ? source.Path
                : $"{source.Target}/{culture}/{source.FileName}";
            var existing = destination == source.Path ? null : game.Files.Keys.FirstOrDefault(k => k.Equals(destination, StringComparison.OrdinalIgnoreCase));
            destination = existing ?? destination;
            if (game.IsInIoStore(existing ?? source.Path))
            {
                throw new RecipeException(
                    "This game keeps its localization inside IoStore containers (.utoc/.ucas); the toolkit can read it but can't write a mod for it yet.",
                    existing ?? source.Path);
            }

            // The game's own culture keeps its layout and untranslated entries; a new file starts from the native one.
            var resource = existing is null ? source.Resource : Guard(existing, () => UnrealLocres.Read(Read(game.Files[existing])));
            var byId = resource.Entries.ToDictionary(e => (e.Namespace, e.Key));
            var replacements = new Dictionary<(string, string), string>();
            foreach (var text in chosen)
            {
                if (!byId.TryGetValue((text.Namespace, text.Key), out var entry) || entry.SourceHash != text.SourceHash)
                {
                    throw new RecipeException($"The game's {culture} entry {text.Namespace}/{text.Key} is missing or stale in {destination}; rebuild refused.", destination);
                }

                replacements[(text.Namespace, text.Key)] = translations[Key(source.Path, text)];
            }

            files.Add(destination, Guard(destination, () => resource.Write(replacements)));
            applied += replacements.Count;
        }

        if (files.Count == 0)
        {
            return new RebuildResult([], 0, unknown);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var mods = game.PaksPath + "/~mods/";
        var written = new List<string>();
        Guard(mods + ModName, () =>
        {
            output.Write(mods + ModName, UnrealPakWriter.Write(files));
            written.Add(mods + ModName);
            if (game.TocVersion is { } version)
            {
                output.Write(mods + ModToc, UnrealIoStoreWriter.EmptyToc(version, ModToc));
                output.Write(mods + ModData, []);
                written.AddRange([mods + ModData, mods + ModToc]);
            }

            return true;
        });
        return new RebuildResult([.. written.Order(StringComparer.Ordinal)], applied, unknown) { InGameLanguage = inGame };
    }

    public IReadOnlyList<string> TouchedFiles(string gameDir, IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        using var game = Load(gameDir, CancellationToken.None);
        var wanted = keys.ToHashSet(StringComparer.Ordinal);

        // A rebuild reads the native resource, the requested culture's and the metadata: every container of the target.
        return SourceFiles(game.Sources.Where(s => s.Resource.Entries.Any(e => wanted.Contains(Key(s.Path, e)))));
    }

    /// <summary><c>locres:&lt;resource path&gt;:&lt;namespace&gt;:&lt;key&gt;</c>, each part percent-escaped.</summary>
    internal static string Key(string path, UnrealText entry) =>
        "locres:" + Uri.EscapeDataString(path) + ":" + Uri.EscapeDataString(entry.Namespace) + ":" + Uri.EscapeDataString(entry.Key);

    /// <summary>FText arguments (<c>{Name}</c>, <c>{0}</c>), rich text tags (<c>&lt;b&gt;</c>, <c>&lt;/&gt;</c>), printf fields.</summary>
    private static readonly TextMarkup UnrealMarkup = new(Placeholder(), TagBrackets.Angle);

    [GeneratedRegex(@"\{[^{}\r\n]+\}|</>|</?[A-Za-z_][^<>\r\n]*>|%%|%[-+0#]*\d*(?:\.\d+)?[sdif]")]
    private static partial Regex Placeholder();

    private static Game Load(string gameDir, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(gameDir);
        var root = Path.GetFullPath(gameDir);
        var detection = new EngineDetector().Detect(root);
        if (detection.Match is not { Kind.Engine: GameEngine.Unreal } match)
        {
            throw new RecipeException("This folder isn't an unambiguous Unreal game.");
        }

        var marker = Path.Combine(root, match.Marker);
        var folder = Directory.Exists(marker) ? marker : Path.GetDirectoryName(marker)!;
        var paksPath = Path.GetRelativePath(root, folder).Replace('\\', '/');
        var provider = new StreamedFileProvider("ailocalpack", new VersionContainer(EGame.GAME_UE4_27), StringComparer.Ordinal);
        var ioStores = new List<UnrealIoStore>();
        try
        {
            var archives = TopFiles(folder, ".pak");
            var tocs = TopFiles(folder, ".utoc");
            foreach (var data in TopFiles(folder, ".ucas"))
            {
                if (!tocs.Any(t => IsPartitionOf(data, t)))
                {
                    throw new RecipeException("IoStore data has no matching .utoc index.", Path.GetRelativePath(root, data));
                }
            }

            foreach (var toc in tocs)
            {
                ct.ThrowIfCancellationRequested();

                // Global metadata serves cooked packages, not raw localization: no key or package parsing is needed.
                if (Path.GetFileName(toc).Equals("global.utoc", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ioStores.Add(Guard(Path.GetRelativePath(root, toc), () => new UnrealIoStore(toc, ct)));
            }

            foreach (var archive in archives)
            {
                ct.ThrowIfCancellationRequested();
                var before = provider.UnloadedVfs.Count;
                Guard(Path.GetRelativePath(root, archive), () =>
                {
                    provider.RegisterVfs(archive);
                    return true;
                });
                if (provider.UnloadedVfs.Count != before + 1)
                {
                    throw new RecipeException("Unsupported or corrupt Unreal archive.", Path.GetRelativePath(root, archive));
                }
            }

            ct.ThrowIfCancellationRequested();
            Guard(paksPath, () => provider.Mount());
            if (provider.UnloadedVfs.Count > 0)
            {
                var unloaded = provider.UnloadedVfs.First();
                throw new RecipeException(
                    unloaded.IsEncrypted
                        ? "AES-encrypted Unreal archive: no key is configured; this game is unsupported."
                        : "Unreal archive could not be mounted (unsupported or corrupt index).",
                    Path.GetRelativePath(root, unloaded.Path));
            }

            var files = provider.Files.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            var paths = files.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var inIoStore = new HashSet<string>(StringComparer.Ordinal);
            foreach (var store in ioStores)
            {
                foreach (var (path, file) in store.Files)
                {
                    if (!paths.Add(path))
                    {
                        throw new RecipeException("Ambiguous duplicate path across pak/IoStore containers; localization precedence is unsupported.", path);
                    }

                    files.Add(path, file);
                    inIoStore.Add(path);
                }
            }

            var sources = ReadSources(root, paksPath, files, ioStores, ct);
            byte? tocVersion = ioStores.Count > 0 ? ioStores.Max(s => s.Version) : tocs.Count > 0 ? TocVersion(tocs[0]) : null;
            return new Game(provider, paksPath, sources, files, inIoStore, ioStores, tocVersion);
        }
        catch (Exception e)
        {
            provider.Dispose();
            foreach (var store in ioStores)
            {
                store.Dispose();
            }

            if (e is RecipeException or OperationCanceledException or OutOfMemoryException)
            {
                throw;
            }

            throw new RecipeException($"Can't read Unreal localization in {paksPath}: {e.Message}", paksPath, e);
        }
    }

    /// <summary>
    /// The native culture's resources of each localization target (<c>…/Localization/&lt;target&gt;</c>, Engine's
    /// left out), with the target's cultures and containers.
    /// </summary>
    private static List<Source> ReadSources(string root, string paksPath, Dictionary<string, GameFile> files, List<UnrealIoStore> ioStores, CancellationToken ct)
    {
        var sources = new List<Source>();
        var all = files.Values.Where(f => !f.Path.StartsWith("Engine/", StringComparison.OrdinalIgnoreCase)).ToList();
        var resources = all.Where(f => f.Path.EndsWith(".locres", StringComparison.OrdinalIgnoreCase)).ToList();
        if (resources.Any(f => f.Path.Count(c => c == '/') < 3 || f.Path.Split('/').Any(p => p is "" or "." or "..")))
        {
            throw new RecipeException("Invalid localization resource path.", paksPath);
        }

        foreach (var target in resources.GroupBy(f => Parent(Parent(f.Path)), StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var metadata = all.Where(f => f.Path.EndsWith(".locmeta", StringComparison.OrdinalIgnoreCase) && Parent(f.Path) == target.Key).ToList();
            if (metadata.Count > 1)
            {
                throw new RecipeException("Multiple locmeta resources in one target are unsupported; native culture is ambiguous.", target.Key);
            }

            var meta = metadata.SingleOrDefault();
            var culture = meta is null ? "en" : Guard(meta.Path, () => UnrealLocmeta.Read(Read(meta)).NativeCulture);
            var cultures = target.Select(f => Name(Parent(f.Path))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var native = target.Where(f => Name(Parent(f.Path)).Equals(culture, StringComparison.OrdinalIgnoreCase)).ToList();
            if (native.Count == 0)
            {
                throw new RecipeException($"Native culture '{culture}' has no locres in {target.Key}.", target.Key);
            }

            var containers = target.Concat(meta is null ? [] : [meta])
                .SelectMany(f => Containers(root, f, ioStores))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            foreach (var file in native)
            {
                var resource = Guard(file.Path, () => UnrealLocres.Read(Read(file)));
                sources.Add(new Source(file.Path, target.Key, Name(file.Path), culture, cultures, resource, containers));
            }
        }

        if (!sources.Any(s => s.Resource.Entries.Count > 0))
        {
            throw new RecipeException("No supported game localization resources (.locres) were found.", paksPath);
        }

        return sources;
    }

    /// <summary>The TOC version byte of a <c>.utoc</c> not opened as a container (a lone <c>global.utoc</c>).</summary>
    private static byte TocVersion(string toc)
    {
        using var file = File.OpenRead(toc);
        Span<byte> header = stackalloc byte[17];
        file.ReadExactly(header);
        return header[16] is >= 3 and <= 8 ? header[16] : throw new RecipeException($"Unsupported IoStore TOC version {header[16]}.", toc);
    }

    private static List<string> TopFiles(string folder, string extension) =>
        [.. Directory.EnumerateFiles(folder).Where(f => Path.GetExtension(f).Equals(extension, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)];

    /// <summary>Whether <paramref name="data"/> is <c>X.ucas</c> or a partition <c>X_sN.ucas</c> of <c>X.utoc</c>.</summary>
    private static bool IsPartitionOf(string data, string toc)
    {
        var stem = Path.GetFileNameWithoutExtension(data);
        var tocStem = Path.GetFileNameWithoutExtension(toc);
        return stem.Equals(tocStem, StringComparison.OrdinalIgnoreCase)
            || (stem.StartsWith(tocStem + "_s", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(stem[(tocStem.Length + 2)..], out var partition)
                && partition > 0);
    }

    private static string Parent(string path) => path[..Math.Max(path.LastIndexOf('/'), 0)];

    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static IReadOnlyList<string> SourceFiles(IEnumerable<Source> sources) =>
        sources.SelectMany(s => s.Containers).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    private static IEnumerable<string> Containers(string root, GameFile file, IReadOnlyList<UnrealIoStore> stores)
    {
        if (file is not VfsEntry entry)
        {
            throw new InvalidDataException("Expected a container entry.");
        }

        var store = stores.FirstOrDefault(s => s.Files.TryGetValue(file.Path, out var held) && ReferenceEquals(held, file));
        return (store?.Containers ?? [entry.Vfs.Path]).Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'));
    }

    private static byte[] Read(GameFile file)
    {
        if (file.IsEncrypted)
        {
            throw new RecipeException("AES-encrypted localization: no key is configured; this game is unsupported.", file.Path);
        }

        if (file.Size < 0 || file.Size > MaxResourceSize)
        {
            throw new InvalidDataException("Unreal localization resource exceeds 32 MiB.");
        }

        if (file is FIoStoreEntry io)
        {
            CheckIoStoreEntry(io);
        }

        return file.Read();
    }

    /// <summary>Only raw files in blocks our own decompressors handle: a game DLL never supplies a codec.</summary>
    private static void CheckIoStoreEntry(FIoStoreEntry io)
    {
        if (io.ChunkId.ChunkType != (byte)EIoChunkType5.ExternalFile)
        {
            throw new RecipeException("IoStore localization must be a raw external file; cooked asset FText is unsupported.", io.Path);
        }

        var toc = io.IoStoreReader.TocResource;
        var blockSize = toc.Header.CompressionBlockSize;
        if (io.Size == 0)
        {
            return;
        }

        for (var i = io.Offset / blockSize; i <= (io.Offset + io.Size - 1) / blockSize; i++)
        {
            var required = Math.Min(blockSize, io.Offset + io.Size - (i * blockSize));
            if (required > toc.CompressionBlocks[i].UncompressedSize)
            {
                throw new InvalidDataException("IoStore localization extends beyond its uncompressed block.");
            }

            var method = toc.CompressionMethods[toc.CompressionBlocks[i].CompressionMethodIndex];
            if (method is not (CUE4Parse.Compression.CompressionMethod.None or CUE4Parse.Compression.CompressionMethod.Zlib or CUE4Parse.Compression.CompressionMethod.Gzip))
            {
                throw new RecipeException($"Unavailable IoStore localization compression '{method}'; only None, Zlib and Gzip are supported; no game DLL is loaded.", io.Path);
            }
        }
    }

    private static T Guard<T>(string path, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception e) when (e is not RecipeException and not OperationCanceledException and not OutOfMemoryException)
        {
            throw new RecipeException($"Can't process {path}: {e.Message}", path, e);
        }
    }

    /// <param name="Path">The native resource (<c>…/Localization/Game/en/Game.locres</c>).</param>
    /// <param name="Target">Its target folder (<c>…/Localization/Game</c>).</param>
    /// <param name="Cultures">Culture folders of the target holding a resource.</param>
    private sealed record Source(string Path, string Target, string FileName, string Culture, IReadOnlyList<string> Cultures, UnrealLocres Resource, IReadOnlyList<string> Containers);

    private sealed record Game(
        StreamedFileProvider Provider,
        string PaksPath,
        IReadOnlyList<Source> Sources,
        IReadOnlyDictionary<string, GameFile> Files,
        IReadOnlySet<string> IoStorePaths,
        IReadOnlyList<UnrealIoStore> IoStores,
        byte? TocVersion) : IDisposable
    {
        public bool IsInIoStore(string path) => IoStorePaths.Contains(path);

        public void Dispose()
        {
            Provider.Dispose();
            foreach (var store in IoStores)
            {
                store.Dispose();
            }
        }
    }
}
