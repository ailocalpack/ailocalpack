using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Dependencies;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Languages;
using AiLocalPack.Core.Packs;

namespace AiLocalPack.Core.Registry;

/// <summary>Result of reading <c>index.json</c>.</summary>
/// <param name="Index">The index, or <c>null</c> when the file can't be used at all (not JSON, wrong schema version…).</param>
/// <param name="Errors">
/// Every problem. With an <paramref name="Index"/> these are the entries left out (a damaged pack or game is skipped,
/// the rest of the index is still good); without one, why the file was refused.
/// </param>
/// <param name="IsNewerFormat">The file is from a newer registry format: the app needs an update to read it.</param>
public sealed record RegistryIndexReadResult(RegistryIndex? Index, IReadOnlyList<PackFormatError> Errors, bool IsNewerFormat = false)
{
    public bool IsValid => Index is not null;
}

/// <summary>
/// Reads, checks and writes <c>index.json</c> (<see cref="RegistryIndex"/>), following
/// <c>schemas/registry-index.schema.json</c>. Never throws for bad content. A file that is not JSON, has another
/// <c>schemaVersion</c> or lacks <c>generated</c> / <c>games</c> is refused; a damaged game or pack entry is left out
/// with its reason, so one bad entry doesn't hide the whole registry.
/// </summary>
public static class RegistryIndexSerializer
{
    private static readonly RegistryJsonContext Writer = new(new JsonSerializerOptions(RegistryJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    public static RegistryIndexReadResult Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        RegistryIndexJson? file;
        try
        {
            file = JsonSerializer.Deserialize(json, RegistryJsonContext.Default.RegistryIndexJson);
        }
        catch (JsonException e)
        {
            return new RegistryIndexReadResult(null, [PackFormat.FromJsonException(e)]);
        }

        if (file is null)
        {
            return new RegistryIndexReadResult(null, [new PackFormatError("", "The file holds no index (null).")]);
        }

        if (PackFormat.CheckSchemaVersion(file.SchemaVersion, RegistryIndex.CurrentSchemaVersion) is { } versionError)
        {
            return new RegistryIndexReadResult(null, [versionError], IsNewerFormat: file.SchemaVersion > RegistryIndex.CurrentSchemaVersion);
        }

        var fatal = new PackFormatErrors();
        DateTimeOffset generated = default;
        if (file.Generated is null)
        {
            fatal.Add("generated", "is missing.");
        }
        else if (!PackFormat.TryParseDate(file.Generated, out generated))
        {
            fatal.Add("generated", $"'{file.Generated}' is not a date and time with a time zone (e.g. 2026-11-04T12:00:00Z).");
        }

        if (file.Games is null)
        {
            fatal.Add("games", "is missing.");
        }

        if (fatal.Any)
        {
            return new RegistryIndexReadResult(null, fatal.List);
        }

        var skipped = new PackFormatErrors();
        var games = new Dictionary<GameId, RegistryGame>();
        foreach (var (key, entry) in file.Games!)
        {
            var field = PackFormat.Key("games", key);
            if (!GameId.TryParse(key, out var id))
            {
                skipped.Add(field, $"'{key}' is not a game ID (steam:<app id> or fp:<64 lowercase hex>).");
                continue;
            }

            if (entry is null)
            {
                skipped.Add(field, "is empty.");
                continue;
            }

            games[id] = ReadGame(id, field, entry, skipped);
        }

        var dependencies = DependencyWhitelist.Read(file.Dependencies, "dependencies", skipped);
        var antiCheats = ReadAntiCheats(file.AntiCheats, skipped);
        return new RegistryIndexReadResult(new RegistryIndex(generated, games) { Dependencies = dependencies, AntiCheats = antiCheats }, skipped.List);
    }

    /// <summary>The registry's anti-cheat rules; a damaged or repeated one is left out with its error, like a pack.</summary>
    private static List<AntiCheatRule> ReadAntiCheats(IReadOnlyList<RegistryAntiCheatJson?>? list, PackFormatErrors skipped)
    {
        var rules = new List<AntiCheatRule>();
        for (var i = 0; i < (list?.Count ?? 0); i++)
        {
            var entry = list![i];
            if (!AntiCheatPolicy.TryCreateRule(entry?.Id, entry?.Name, entry?.Markers, out var rule, out var error))
            {
                skipped.Add($"antiCheats[{i}]", error);
            }
            else if (rules.Any(r => r.Id == rule.Id))
            {
                skipped.Add($"antiCheats[{i}].id", $"'{rule.Id}' is listed twice.");
            }
            else
            {
                rules.Add(rule);
            }
        }

        return rules;
    }

    public static RegistryIndexReadResult Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return Read(reader.ReadToEnd());
    }

    /// <summary>
    /// The index as indented JSON: games, languages and packs in ordinal order (the same index always gives the same
    /// bytes), defaults left out (<c>false</c> flags, no rating, no discussion, no dependency whitelist or anti-cheat rules);
    /// the whitelist's entries by ID, the rules in the policy's order.
    /// </summary>
    public static string Write(RegistryIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        var games = new SortedDictionary<string, RegistryGameJson?>(StringComparer.Ordinal);
        foreach (var game in index.Games.Values)
        {
            var packs = new SortedDictionary<string, IReadOnlyList<RegistryPackJson?>?>(StringComparer.Ordinal);
            foreach (var (language, list) in game.Packs.Where(p => p.Value.Count > 0))
            {
                packs[language] = [.. list.OrderBy(p => p.Id, StringComparer.Ordinal).Select(WritePack)];
            }

            games[game.Id.ToString()] = new RegistryGameJson(
                game.OptOut ? true : null,
                game.AntiCheatAllowed ? true : null,
                game.AntiCheat,
                packs.Count > 0 ? packs : null);
        }

        var file = new RegistryIndexJson(
            RegistryIndex.CurrentSchemaVersion,
            PackFormat.FormatDate(index.Generated),
            games,
            index.Dependencies.Dependencies.Count > 0 ? index.Dependencies.ToJson() : null,
            index.AntiCheats.Count > 0 ? [.. index.AntiCheats.Select(r => new RegistryAntiCheatJson(r.Id, r.Name, [.. r.Markers]))] : null);
        return JsonSerializer.Serialize(file, Writer.RegistryIndexJson);
    }

    private static RegistryPackJson WritePack(RegistryPack pack) => new(
        pack.Id,
        pack.Title,
        new RegistryAuthorJson(pack.Author.GitHub, pack.Author.Name),
        PackManifestSerializer.StatusText(pack.Status),
        pack.Revision,
        PackFormat.FormatDate(pack.Updated),
        new RegistryCoverageJson(pack.Coverage.Translated, pack.Coverage.Total),
        [.. pack.BuildIds],
        pack.BuildCount,
        pack.ManifestPath,
        pack.Size,
        pack.Rating == RegistryRating.None ? null : new RegistryRatingJson(pack.Rating.Likes, pack.Rating.Works, pack.Rating.DoesNotWork),
        pack.Discussion?.AbsoluteUri);

    private static RegistryGame ReadGame(GameId id, string field, RegistryGameJson entry, PackFormatErrors skipped)
    {
        var languages = new Dictionary<string, IReadOnlyList<RegistryPack>>(StringComparer.Ordinal);
        foreach (var (language, list) in entry.Packs ?? new Dictionary<string, IReadOnlyList<RegistryPackJson?>?>())
        {
            var languageField = $"{field}.packs{(IsPlainName(language) ? "." + language : PackFormat.Key("", language))}";
            if (!GameLanguages.Tags.Contains(language, StringComparer.Ordinal))
            {
                skipped.Add(languageField, $"'{language}' is not a supported language.");
                continue;
            }

            var packs = new List<RegistryPack>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < (list?.Count ?? 0); i++)
            {
                var packField = $"{languageField}[{i}]";
                if (list![i] is not { } json)
                {
                    skipped.Add(packField, "is empty.");
                    continue;
                }

                var errors = new PackFormatErrors();
                var pack = new PackChecker(packField, errors).Check(json, id, language);
                if (pack is not null && !ids.Add(pack.Id))
                {
                    errors.Add($"{packField}.id", $"'{pack.Id}' is listed twice for this game and language.");
                }

                if (errors.Any)
                {
                    foreach (var error in errors.List)
                    {
                        skipped.Add(error);
                    }
                }
                else
                {
                    packs.Add(pack!);
                }
            }

            languages[language] = packs;
        }

        var antiCheat = entry.AntiCheat;
        if (antiCheat is not null && !AntiCheatPolicy.IsValidId(antiCheat))
        {
            // The block stays on: an unreadable ID is still an anti-cheat, shown by its ID.
            skipped.Add($"{field}.antiCheat", $"'{antiCheat}' is not an anti-cheat ID (lowercase letters, digits, '-').");
        }

        return new RegistryGame(id, entry.OptOut ?? false, entry.AntiCheatAllowed ?? false, languages) { AntiCheat = antiCheat };
    }

    private static bool IsPlainName(string key) => key.Length > 0 && key.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>Maps one pack entry to the model, recording every rule broken.</summary>
    private sealed class PackChecker(string field, PackFormatErrors errors)
    {
        public RegistryPack? Check(RegistryPackJson json, GameId game, string language)
        {
            var id = Require("id", json.Id, PackFormat.IsSlug, "is not a valid pack ID");
            var title = Require("title", json.Title, t => PackFormat.IsText(t, PackManifestSerializer.MaxTitleLength), "is empty or too long");
            var author = Author(json.Author);
            var status = Status(json.Status);
            var revision = AtLeast("revision", json.Revision, 1);
            var updated = Date(json.Updated);
            var coverage = Coverage(json.Coverage);
            var buildIds = BuildIds(json.BuildIds, game);
            var buildCount = AtLeast("buildCount", json.BuildCount, 1);
            if (buildIds is not null && buildCount is { } count && buildIds.Count > count)
            {
                errors.Add($"{field}.buildCount", $"is {count}, fewer than the {buildIds.Count} build IDs listed.");
            }

            var manifest = Require("manifest", json.Manifest, IsManifestPath, "is not a relative path to a manifest.json");
            var size = json.Size switch
            {
                null => Missing("size"),
                < 1 => Wrong("size", $"must be 1 byte or more, not {json.Size}."),
                { } s => s,
            };
            var rating = Rating(json.Rating);
            var discussion = Discussion(json.Discussion);

            return errors.Any
                ? null
                : new RegistryPack(
                    id!, game, language, title!, author!, status!.Value, revision!.Value, updated!.Value, coverage!, buildIds!,
                    buildCount!.Value, manifest!, size!.Value, rating, discussion);
        }

        private static bool IsManifestPath(string path) =>
            PackFormat.IsRelativePath(path) && (path == "manifest.json" || path.EndsWith("/manifest.json", StringComparison.Ordinal));

        private string? Require(string name, string? value, Func<string, bool> isValid, string problem)
        {
            if (value is null)
            {
                Missing(name);
                return null;
            }

            if (!isValid(value))
            {
                errors.Add($"{field}.{name}", $"'{value}' {problem}.");
                return null;
            }

            return value;
        }

        private long? Missing(string name)
        {
            errors.Add($"{field}.{name}", "is missing.");
            return null;
        }

        private long? Wrong(string name, string message)
        {
            errors.Add($"{field}.{name}", message);
            return null;
        }

        private int? AtLeast(string name, int? value, int minimum)
        {
            if (value is null)
            {
                Missing(name);
                return null;
            }

            if (value < minimum)
            {
                Wrong(name, $"must be {minimum} or more, not {value}.");
                return null;
            }

            return value;
        }

        private PackAuthor? Author(RegistryAuthorJson? author)
        {
            if (author is null)
            {
                Missing("author");
                return null;
            }

            var login = Require("author.github", author.Github, PackFormat.IsGitHubLogin, "is not a GitHub login");
            if (author.Name is { } name && !PackFormat.IsText(name, PackManifestSerializer.MaxNameLength))
            {
                Wrong("author.name", "is empty or too long.");
            }

            return login is null ? null : new PackAuthor(login, author.Name);
        }

        private PackStatus? Status(string? text)
        {
            switch (text)
            {
                case null:
                    Missing("status");
                    return null;
                case "alpha":
                    return PackStatus.Alpha;
                case "verified":
                    return PackStatus.Verified;
                default:
                    Wrong("status", $"'{text}' is not a status (alpha or verified).");
                    return null;
            }
        }

        private DateTimeOffset? Date(string? text)
        {
            if (text is null)
            {
                Missing("updated");
                return null;
            }

            if (!PackFormat.TryParseDate(text, out var date))
            {
                Wrong("updated", $"'{text}' is not a date and time with a time zone.");
                return null;
            }

            return date;
        }

        private PackCoverage? Coverage(RegistryCoverageJson? coverage)
        {
            if (coverage is null)
            {
                Missing("coverage");
                return null;
            }

            var translated = coverage.Translated is null ? (int?)Missing("coverage.translated") : coverage.Translated;
            var total = coverage.Total is null ? (int?)Missing("coverage.total") : coverage.Total;
            if (translated is not { } t || total is not { } n)
            {
                return null;
            }

            if (t < 0 || n < 1 || t > n)
            {
                Wrong("coverage", $"{t} of {n} strings is not possible.");
                return null;
            }

            return new PackCoverage(t, n);
        }

        private List<string>? BuildIds(IReadOnlyList<string?>? list, GameId game)
        {
            if (list is null)
            {
                Missing("buildIds");
                return null;
            }

            if (game.Kind == GameIdKind.Fingerprint && list.Count > 0)
            {
                Wrong("buildIds", "must be empty: a manually added game is matched by file hashes.");
                return null;
            }

            var ids = new List<string>();
            foreach (var buildId in list)
            {
                if (buildId is not { Length: > 0 and <= 20 } || !buildId.All(char.IsAsciiDigit))
                {
                    Wrong("buildIds", $"'{buildId}' is not a build ID (digits).");
                    return null;
                }

                if (ids.Contains(buildId, StringComparer.Ordinal))
                {
                    Wrong("buildIds", $"build {buildId} is listed twice.");
                    return null;
                }

                ids.Add(buildId);
            }

            return ids;
        }

        private RegistryRating Rating(RegistryRatingJson? rating)
        {
            if (rating is null)
            {
                return RegistryRating.None;
            }

            int Count(string name, int? value)
            {
                if (value < 0)
                {
                    Wrong($"rating.{name}", $"must be 0 or more, not {value}.");
                }

                return Math.Max(value ?? 0, 0);
            }

            return new RegistryRating(Count("likes", rating.Likes), Count("works", rating.Works), Count("doesNotWork", rating.DoesNotWork));
        }

        private Uri? Discussion(string? text)
        {
            if (text is null)
            {
                return null;
            }

            if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            {
                Wrong("discussion", $"'{text}' is not an https:// URL.");
                return null;
            }

            return url;
        }
    }
}
