using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Core.Generation;

/// <summary>
/// A registry pack of the job's game that a generation learns from (decided 2026-11-16, _Reference packs_ in
/// <c>docs/generation.md</c>): its manifest, its string table (none for a binary patch pack, whose archive is never
/// downloaded) and whether it was made for the game's current build.
/// </summary>
/// <param name="SameBuild">One of the manifest's builds is the game's current one (Steam build ID; a manual game's file hashes).</param>
public sealed record ReferencePack(PackManifest Manifest, StringTable? Strings, bool SameBuild)
{
    /// <summary>The job options' and the new manifest's entry for it.</summary>
    public PackReference Reference => new(Manifest.Language, Manifest.Id, Manifest.Revision);

    /// <summary>Whether <paramref name="version"/> is one of the builds the manifest was made for (on <paramref name="platform"/> when known).</summary>
    public static bool IsSameBuild(PackManifest manifest, GameVersion? version, Storage.OsKind? platform = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (version is null)
        {
            return false;
        }

        if (version.BuildId is { } buildId)
        {
            return manifest.Builds.Any(b => b.Matches(buildId, platform));
        }

        var hashes = version.FileHashes ?? new Dictionary<string, string>();
        return manifest.Builds.Any(b => b.BuildId is null
            && b.Files.Any(f => hashes.ContainsKey(f.Key))
            && b.Files.All(f => !hashes.TryGetValue(f.Key, out var hash) || hash == f.Value));
    }
}

/// <summary>
/// The reference packs of a job, in priority order (<see cref="ReferenceSelection"/>), and what they tell: the
/// translations of one string, the reader, the files where the text is, the fonts.
/// </summary>
public sealed class ReferenceSet
{
    public ReferenceSet(IReadOnlyList<ReferencePack> packs)
    {
        ArgumentNullException.ThrowIfNull(packs);
        Packs = packs;
    }

    public static ReferenceSet Empty { get; } = new([]);

    public IReadOnlyList<ReferencePack> Packs { get; }

    public bool IsEmpty => Packs.Count == 0;

    /// <summary>
    /// How the references translated one string, by language: only a translation made from this very original (the
    /// table's <see cref="StringTable.Originals"/> hash matches), or, in a table without a hash for it, from a pack made
    /// for the game's current build. Empty translations are left out.
    /// </summary>
    public IReadOnlyDictionary<string, string> For(string key, string original)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(original);
        Dictionary<string, string>? found = null;
        string? hash = null;
        foreach (var pack in Packs)
        {
            if (pack.Strings is not { } table || !table.Strings.TryGetValue(key, out var text) || string.IsNullOrEmpty(text))
            {
                continue;
            }

            var fits = table.Originals.TryGetValue(key, out var made)
                ? made == (hash ??= OriginalHash.Of(original))
                : pack.SameBuild;
            if (fits)
            {
                found ??= new Dictionary<string, string>(StringComparer.Ordinal);
                found.TryAdd(pack.Manifest.Language, text);
            }
        }

        return found ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>();
    }

    /// <summary>The first reference with a recipe: its toolkit tool read and rebuilt this game.</summary>
    public PackRecipe? Recipe => Packs.Select(p => p.Manifest.Recipe).FirstOrDefault(r => r is not null);

    /// <summary>Every reference is a binary patch pack: the toolkit didn't read the game for anyone.</summary>
    public bool OnlyPatches => !IsEmpty && Packs.All(p => p.Manifest.Patch is not null);

    /// <summary>The game files the references touched (those for the current build first), in ordinal order within each group.</summary>
    public IReadOnlyList<string> TouchedFiles =>
    [
        .. Packs.Where(p => p.SameBuild).SelectMany(p => p.Manifest.TouchedFiles)
            .Concat(Packs.Where(p => !p.SameBuild).SelectMany(p => p.Manifest.TouchedFiles))
            .Distinct(StringComparer.Ordinal),
    ];

    /// <summary>The fonts the references of a language in the same script as <paramref name="language"/> swapped in, in their order.</summary>
    public IReadOnlyList<string> FontsFor(string language) =>
    [
        .. Packs.Where(p => LanguageKinship.Script(p.Manifest.Language) == LanguageKinship.Script(language))
            .SelectMany(p => p.Manifest.Fonts)
            .Distinct(StringComparer.Ordinal),
    ];

    /// <summary>The strings whose original or reference translation holds <paramref name="text"/> (case-insensitive), at most <paramref name="limit"/>.</summary>
    public IReadOnlyList<string> KeysMentioning(string text, IReadOnlyDictionary<string, string> originals, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(originals);
        var keys = new List<string>();
        foreach (var (key, original) in originals)
        {
            if (keys.Count >= limit)
            {
                break;
            }

            if (original.Contains(text, StringComparison.OrdinalIgnoreCase)
                || For(key, original).Values.Any(t => t.Contains(text, StringComparison.OrdinalIgnoreCase)))
            {
                keys.Add(key);
            }
        }

        return keys;
    }
}

/// <summary>
/// Which registry packs of a game are references for a new translation (decided 2026-11-16): packs the players
/// confirmed (<c>verified</c>, or more "works" than "doesn't work" reports), the best one per language (made for the
/// game's build first, then verified, then the rating), languages in order English (a pivot: a game with an English
/// pack wasn't written in English), the target language, then the closest to it (<see cref="LanguageKinship"/>),
/// then the rating; at most <see cref="MaxLanguages"/>.
/// </summary>
public static class ReferenceSelection
{
    public const int MaxLanguages = 2;

    public const string Pivot = "en";

    /// <summary>A pack players found working.</summary>
    public static bool Qualifies(RegistryPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return pack.Status == PackStatus.Verified || pack.Rating.Works > pack.Rating.DoesNotWork;
    }

    public static IReadOnlyList<RegistryPack> Select(RegistryIndex? index, GameId game, string language, string? buildId, Storage.OsKind? platform = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        if (index?.Find(game) is not { } entry)
        {
            return [];
        }

        var best = entry.AllPacks
            .Where(Qualifies)
            .GroupBy(p => p.Language, StringComparer.Ordinal)
            .Select(g => g
                .OrderByDescending(p => p.IsMadeFor(buildId, platform))
                .ThenByDescending(p => p.Status == PackStatus.Verified)
                .ThenByDescending(p => p.Rating.Works - p.Rating.DoesNotWork)
                .ThenByDescending(p => p.Rating.Likes)
                .ThenByDescending(p => p.Coverage.Ratio)
                .ThenBy(p => p.Id, StringComparer.Ordinal)
                .First());
        return
        [
            .. best
                .OrderByDescending(p => p.Language == Pivot)
                .ThenByDescending(p => p.Language == language)
                .ThenByDescending(p => LanguageKinship.Closeness(p.Language, language))
                .ThenByDescending(p => p.Status == PackStatus.Verified)
                .ThenByDescending(p => p.Rating.Works - p.Rating.DoesNotWork)
                .ThenBy(p => p.Language, StringComparer.Ordinal)
                .Take(MaxLanguages),
        ];
    }
}

/// <summary>How close two of <see cref="Languages.GameLanguages.Tags"/> are: same language, script, family.</summary>
public static class LanguageKinship
{
    private static readonly Dictionary<string, string> Scripts = new(StringComparer.Ordinal)
    {
        ["ru"] = "Cyrl", ["uk"] = "Cyrl", ["bg"] = "Cyrl",
        ["el"] = "Grek", ["ar"] = "Arab", ["th"] = "Thai",
        ["zh-Hans"] = "Hans", ["zh-Hant"] = "Hant", ["ja"] = "Jpan", ["ko"] = "Kore",
    };

    private static readonly Dictionary<string, string> Families = new(StringComparer.Ordinal)
    {
        ["ru"] = "slavic", ["uk"] = "slavic", ["bg"] = "slavic", ["pl"] = "slavic", ["cs"] = "slavic",
        ["es-ES"] = "romance", ["es-419"] = "romance", ["fr"] = "romance", ["it"] = "romance", ["pt-BR"] = "romance", ["pt-PT"] = "romance", ["ro"] = "romance",
        ["en"] = "germanic", ["de"] = "germanic", ["nl"] = "germanic", ["sv"] = "germanic", ["da"] = "germanic", ["nb"] = "germanic",
        ["fi"] = "uralic", ["hu"] = "uralic",
        ["zh-Hans"] = "chinese", ["zh-Hant"] = "chinese",
    };

    /// <summary>The script a language is written in (<c>Latn</c> unless listed).</summary>
    public static string Script(string language) => Scripts.GetValueOrDefault(language, "Latn");

    /// <summary>4 = the same language (<c>es-ES</c> / <c>es-419</c>), + 2 the same script, + 1 the same family.</summary>
    public static int Closeness(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var score = Base(a) == Base(b) ? 4 : 0;
        if (Script(a) == Script(b))
        {
            score += 2;
        }

        if (Families.TryGetValue(a, out var family) && Families.GetValueOrDefault(b) == family)
        {
            score++;
        }

        return score;

        static string Base(string tag) => tag.Split('-')[0];
    }
}
