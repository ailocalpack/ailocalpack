using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Languages;

namespace AiLocalPack.Core.Packs;

/// <summary>Result of reading a pack file: the model when the file is valid, else every problem found.</summary>
public sealed record PackReadResult<T>(T? Value, IReadOnlyList<PackFormatError> Errors)
    where T : class
{
    public bool IsValid => Value is not null;
}

/// <summary>
/// Reads, checks and writes <c>manifest.json</c> (<see cref="PackManifest"/>). The checks are those of
/// <c>schemas/pack-manifest.schema.json</c> plus the rules a schema cannot state (revision numbering, revision authors
/// listed in authors, build IDs only for store games, one build for a patch…). Unknown properties are ignored.
/// </summary>
public static class PackManifestSerializer
{
    public const int MaxTitleLength = 80;
    public const int MaxNameLength = 80;
    public const int MaxNotesLength = 2000;
    public const int MaxVersionLength = 64;
    public const int MaxModelLength = 128;

    public static PackReadResult<PackManifest> Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        PackManifestJson? file;
        try
        {
            file = JsonSerializer.Deserialize(json, PackJsonContext.Default.PackManifestJson);
        }
        catch (JsonException e)
        {
            return new PackReadResult<PackManifest>(null, [PackFormat.FromJsonException(e)]);
        }

        if (file is null)
        {
            return new PackReadResult<PackManifest>(null, [new PackFormatError("", "The file holds no manifest (null).")]);
        }

        if (PackFormat.CheckSchemaVersion(file.SchemaVersion, PackManifest.CurrentSchemaVersion) is { } versionError)
        {
            return new PackReadResult<PackManifest>(null, [versionError]);
        }

        var errors = new PackFormatErrors();
        var manifest = new ManifestChecker(errors).Check(file);
        return new PackReadResult<PackManifest>(errors.Any ? null : manifest, errors.List);
    }

    public static PackReadResult<PackManifest> Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return Read(reader.ReadToEnd());
    }

    /// <summary>The manifest as indented JSON (UTF-8 text, non-ASCII kept as is).</summary>
    public static string Write(PackManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var file = new PackManifestJson(
            PackManifest.CurrentSchemaVersion,
            manifest.Id,
            manifest.Game.ToString(),
            manifest.Language,
            manifest.Title,
            [.. manifest.Authors.Select(a => new PackAuthorJson(a.GitHub, a.Name))],
            manifest.Revision,
            [.. manifest.Revisions.Select(r => new PackRevisionJson(r.Number, PackFormat.FormatDate(r.Date), r.By, r.Notes))],
            StatusText(manifest.Status),
            manifest.Agent is { } agent ? new PackAgentJson(agent.Id, agent.Version, agent.Model) : null,
            [.. manifest.Builds.Select(b => new PackBuildJson(b.BuildId, Sorted(b.Files)))],
            manifest.Recipe is { } recipe ? new PackRecipeJson(recipe.Tool, recipe.Version, recipe.Parameters) : null,
            manifest.Strings is { } strings ? new PackStringsJson(strings.Path, strings.Sha256, strings.Size) : null,
            manifest.Patch is { } patch
                ? new PackPatchJson(patch.Format, patch.Url.AbsoluteUri, patch.Sha256, patch.Size, Sorted(patch.Files))
                : null,
            manifest.Dependencies.Count > 0 ? [.. manifest.Dependencies.Select(d => new PackDependencyJson(d.Id, d.Version))] : null,
            manifest.Fonts.Count > 0 ? [.. manifest.Fonts] : null,
            new PackCoverageJson(manifest.Coverage.Translated, manifest.Coverage.Total),
            PackManifest.License,
            PackFormat.FormatDate(manifest.Created),
            PackFormat.FormatDate(manifest.Updated));
        return JsonSerializer.Serialize(file, PackJsonWriter.Context.PackManifestJson);
    }

    internal static string StatusText(PackStatus status) => status switch
    {
        PackStatus.Alpha => "alpha",
        PackStatus.Verified => "verified",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    private static SortedDictionary<string, string?> Sorted(IReadOnlyDictionary<string, string> files) =>
        new(files.ToDictionary(f => f.Key, string? (f) => f.Value), StringComparer.Ordinal);

    /// <summary>Maps the JSON shape to the model, recording every rule broken.</summary>
    private sealed class ManifestChecker(PackFormatErrors errors)
    {
        public PackManifest? Check(PackManifestJson file)
        {
            var id = Slug("id", file.Id);
            var game = GameIdOf(file.Game);
            var language = Language("language", file.Language);
            var title = Text("title", file.Title, MaxTitleLength, required: true);
            var authors = Authors(file.Authors);
            var revisions = Revisions(file.Revisions, file.Revision, authors);
            var status = Status(file.Status);
            var agent = Agent(file.Agent);
            var builds = Builds(file.Builds, game);
            var (recipe, strings, patch) = Rebuild(file, builds);
            var dependencies = Dependencies(file.Dependencies);
            var fonts = Fonts(file.Fonts);
            var coverage = Coverage(file.Coverage);
            License(file.License);
            var (created, updated) = Dates(file.Created, file.Updated);

            return errors.Any
                ? null
                : new PackManifest(
                    id!, game!, language!, title!, authors, revisions, status!.Value, agent, builds, recipe, strings, patch,
                    dependencies, fonts, coverage!, created, updated);
        }

        private string? Slug(string field, string? value, bool required = true)
        {
            if (value is null)
            {
                if (required)
                {
                    errors.Add(field, "is missing.");
                }

                return null;
            }

            if (!PackFormat.IsSlug(value))
            {
                errors.Add(field, $"'{value}' is not a valid ID: use lowercase letters, digits and single '-' between them (at most 64 characters).");
                return null;
            }

            return value;
        }

        private string? Text(string field, string? value, int maxLength, bool required)
        {
            if (value is null)
            {
                if (required)
                {
                    errors.Add(field, "is missing.");
                }

                return null;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add(field, "is empty.");
                return null;
            }

            if (value.Length > maxLength)
            {
                errors.Add(field, $"is too long ({value.Length} characters, at most {maxLength}).");
                return null;
            }

            return value;
        }

        private string? Login(string field, string? value)
        {
            if (value is null)
            {
                errors.Add(field, "is missing.");
                return null;
            }

            if (!PackFormat.IsGitHubLogin(value))
            {
                errors.Add(field, $"'{value}' is not a GitHub login.");
                return null;
            }

            return value;
        }

        private string? Sha256(string field, string? value)
        {
            if (value is null)
            {
                errors.Add(field, "is missing.");
                return null;
            }

            if (!PackFormat.IsSha256(value))
            {
                errors.Add(field, $"'{value}' is not a SHA-256 hash (64 lowercase hex characters).");
                return null;
            }

            return value;
        }

        private long Size(string field, long? value)
        {
            if (value is null)
            {
                errors.Add(field, "is missing.");
                return 0;
            }

            if (value < 1)
            {
                errors.Add(field, $"must be 1 byte or more, not {value}.");
            }

            return value.Value;
        }

        private GameId? GameIdOf(string? text)
        {
            if (text is null)
            {
                errors.Add("game", "is missing.");
                return null;
            }

            if (!GameId.TryParse(text, out var id))
            {
                errors.Add("game", $"'{text}' is not a game ID (steam:<app id> or fp:<64 lowercase hex>).");
                return null;
            }

            return id;
        }

        private string? Language(string field, string? tag)
        {
            if (tag is null)
            {
                errors.Add(field, "is missing.");
                return null;
            }

            if (!GameLanguages.Tags.Contains(tag, StringComparer.Ordinal))
            {
                errors.Add(field, $"'{tag}' is not a supported language (one of {string.Join(", ", GameLanguages.Tags)}).");
                return null;
            }

            return tag;
        }

        private List<PackAuthor> Authors(IReadOnlyList<PackAuthorJson?>? list)
        {
            var authors = new List<PackAuthor>();
            if (list is not { Count: > 0 })
            {
                errors.Add("authors", list is null ? "is missing." : "must name at least one author.");
                return authors;
            }

            var logins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < list.Count; i++)
            {
                var field = $"authors[{i}]";
                if (list[i] is not { } entry)
                {
                    errors.Add(field, "is empty.");
                    continue;
                }

                var login = Login($"{field}.github", entry.Github);
                var name = Text($"{field}.name", entry.Name, MaxNameLength, required: false);
                if (login is null)
                {
                    continue;
                }

                if (!logins.Add(login))
                {
                    errors.Add($"{field}.github", $"'{login}' is listed twice.");
                    continue;
                }

                authors.Add(new PackAuthor(login, name));
            }

            return authors;
        }

        private List<PackRevision> Revisions(IReadOnlyList<PackRevisionJson?>? list, int? current, List<PackAuthor> authors)
        {
            var revisions = new List<PackRevision>();
            if (current is null)
            {
                errors.Add("revision", "is missing.");
            }
            else if (current < 1)
            {
                errors.Add("revision", $"must be 1 or more, not {current}.");
            }

            if (list is not { Count: > 0 })
            {
                errors.Add("revisions", list is null ? "is missing." : "must list at least revision 1.");
                return revisions;
            }

            var logins = authors.Select(a => a.GitHub).ToHashSet(StringComparer.OrdinalIgnoreCase);
            DateTimeOffset? previous = null;
            for (var i = 0; i < list.Count; i++)
            {
                var field = $"revisions[{i}]";
                if (list[i] is not { } entry)
                {
                    errors.Add(field, "is empty.");
                    continue;
                }

                if (entry.Revision is null)
                {
                    errors.Add($"{field}.revision", "is missing.");
                }
                else if (entry.Revision != i + 1)
                {
                    errors.Add($"{field}.revision", $"is {entry.Revision}, expected {i + 1}: revisions are numbered 1, 2, 3… oldest first.");
                }

                var date = Date($"{field}.date", entry.Date);
                if (date is { } d && previous is { } p && d < p)
                {
                    errors.Add($"{field}.date", "is earlier than the previous revision.");
                }

                previous = date ?? previous;
                var by = Login($"{field}.by", entry.By);
                if (by is not null && authors.Count > 0 && !logins.Contains(by))
                {
                    errors.Add($"{field}.by", $"'{by}' is not listed in authors.");
                }

                var notes = entry.Notes;
                if (notes is { Length: > MaxNotesLength })
                {
                    errors.Add($"{field}.notes", $"is too long ({notes.Length} characters, at most {MaxNotesLength}).");
                }

                if (entry.Revision is { } number && date is { } revisionDate && by is not null)
                {
                    revisions.Add(new PackRevision(number, revisionDate, by, string.IsNullOrWhiteSpace(notes) ? null : notes));
                }
            }

            if (current is { } c && c >= 1 && list.Count > 0 && list[^1]?.Revision is { } last && last != c)
            {
                errors.Add("revision", $"is {c}, but the last entry of revisions is {last}.");
            }

            return revisions;
        }

        private PackStatus? Status(string? text)
        {
            switch (text)
            {
                case null:
                    errors.Add("status", "is missing.");
                    return null;
                case "alpha":
                    return PackStatus.Alpha;
                case "verified":
                    return PackStatus.Verified;
                default:
                    errors.Add("status", $"'{text}' is not a status (alpha or verified).");
                    return null;
            }
        }

        private PackAgent? Agent(PackAgentJson? agent)
        {
            if (agent is null)
            {
                return null;
            }

            var id = Slug("agent.id", agent.Id);
            var version = Text("agent.version", agent.Version, MaxVersionLength, required: false);
            var model = Text("agent.model", agent.Model, MaxModelLength, required: false);
            return id is null ? null : new PackAgent(id, version, model);
        }

        private List<PackBuild> Builds(IReadOnlyList<PackBuildJson?>? list, GameId? game)
        {
            var builds = new List<PackBuild>();
            if (list is not { Count: > 0 })
            {
                errors.Add("builds", list is null ? "is missing." : "must list at least one game build.");
                return builds;
            }

            var buildIds = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < list.Count; i++)
            {
                var field = $"builds[{i}]";
                if (list[i] is not { } entry)
                {
                    errors.Add(field, "is empty.");
                    continue;
                }

                var buildId = entry.BuildId;
                if (buildId is not null)
                {
                    if (buildId.Length is 0 or > 20 || !buildId.All(char.IsAsciiDigit))
                    {
                        errors.Add($"{field}.buildId", $"'{buildId}' is not a build ID (digits).");
                        buildId = null;
                    }
                    else if (game?.Kind == GameIdKind.Fingerprint)
                    {
                        errors.Add($"{field}.buildId", "is only for store games; a manually added game is matched by file hashes.");
                    }
                    else if (!buildIds.Add(buildId))
                    {
                        errors.Add($"{field}.buildId", $"build {buildId} is listed twice.");
                    }
                }
                else if (game?.Kind == GameIdKind.Steam)
                {
                    errors.Add($"{field}.buildId", "is missing: a Steam game build needs its build ID.");
                }

                var files = FileHashes($"{field}.files", entry.Files);
                if (files is not null)
                {
                    builds.Add(new PackBuild(buildId, files));
                }
            }

            return builds;
        }

        private SortedDictionary<string, string>? FileHashes(string field, IReadOnlyDictionary<string, string?>? files)
        {
            if (files is not { Count: > 0 })
            {
                errors.Add(field, files is null ? "is missing." : "must list at least one file.");
                return null;
            }

            var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var ok = true;
            foreach (var (path, hash) in files)
            {
                var key = PackFormat.Key(field, path);
                if (!PackFormat.IsRelativePath(path))
                {
                    errors.Add(key, "is not a relative file path ('/' separators, no '.' or '..' parts).");
                    ok = false;
                    continue;
                }

                if (Sha256(key, hash) is { } valid)
                {
                    result[path] = valid;
                }
                else
                {
                    ok = false;
                }
            }

            return ok ? result : null;
        }

        private (PackRecipe?, PackStringsFile?, PackPatch?) Rebuild(PackManifestJson file, List<PackBuild> builds)
        {
            if (file.Recipe is not null && file.Patch is not null)
            {
                errors.Add("", "A pack has either a recipe or a binary patch, not both.");
                return (null, null, null);
            }

            if (file.Recipe is null && file.Patch is null)
            {
                errors.Add("", "A pack needs a recipe (strings rebuilt by a toolkit tool) or a binary patch.");
                return (null, null, null);
            }

            var strings = Strings(file.Strings);
            if (file.Recipe is { } recipe)
            {
                if (file.Strings is null)
                {
                    errors.Add("strings", "is missing: a recipe pack needs its string table.");
                }

                return (Recipe(recipe), strings, null);
            }

            return (null, strings, Patch(file.Patch!, builds, file.Builds?.Count ?? 0));
        }

        private PackRecipe? Recipe(PackRecipeJson recipe)
        {
            var tool = Slug("recipe.tool", recipe.Tool);
            string? version = null;
            if (recipe.Version is null)
            {
                errors.Add("recipe.version", "is missing.");
            }
            else if (!PackFormat.IsSemVer(recipe.Version))
            {
                errors.Add("recipe.version", $"'{recipe.Version}' is not a version (major.minor.patch, e.g. 1.0.0).");
            }
            else
            {
                version = recipe.Version;
            }

            var parameters = recipe.Parameters;
            if (parameters is { ValueKind: not JsonValueKind.Object and not JsonValueKind.Null })
            {
                errors.Add("recipe.parameters", "must be an object.");
            }

            return tool is null || version is null
                ? null
                : new PackRecipe(tool, version, parameters is { ValueKind: JsonValueKind.Object } p ? p.Clone() : null);
        }

        private PackStringsFile? Strings(PackStringsJson? strings)
        {
            if (strings is null)
            {
                return null;
            }

            string? path = null;
            if (strings.Path is null)
            {
                errors.Add("strings.path", "is missing.");
            }
            else if (!strings.Path.StartsWith("strings/", StringComparison.Ordinal) || !PackFormat.IsRelativePath(strings.Path)
                || !strings.Path.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/'))
            {
                errors.Add("strings.path", $"'{strings.Path}' must be a file under strings/ (letters, digits, '.', '_', '-').");
            }
            else
            {
                path = strings.Path;
            }

            var sha = Sha256("strings.sha256", strings.Sha256);
            var size = Size("strings.size", strings.Size);
            return path is null || sha is null || size < 1 ? null : new PackStringsFile(path, sha, size);
        }

        private PackPatch? Patch(PackPatchJson patch, List<PackBuild> builds, int buildCount)
        {
            if (patch.Format is null)
            {
                errors.Add("patch.format", "is missing.");
            }
            else if (patch.Format != PackPatch.VcdiffFormat)
            {
                errors.Add("patch.format", $"'{patch.Format}' is not a supported patch format ({PackPatch.VcdiffFormat}).");
            }

            Uri? url = null;
            if (patch.Url is null)
            {
                errors.Add("patch.url", "is missing.");
            }
            else if (!Uri.TryCreate(patch.Url, UriKind.Absolute, out url) || url.Scheme != Uri.UriSchemeHttps)
            {
                errors.Add("patch.url", $"'{patch.Url}' is not an https:// URL.");
                url = null;
            }

            var sha = Sha256("patch.sha256", patch.Sha256);
            var size = Size("patch.size", patch.Size);
            var files = FileHashes("patch.files", patch.Files);
            if (buildCount > 1)
            {
                errors.Add("builds", "A binary patch applies to exactly one game build; list only that one.");
            }
            else if (files is not null && builds is [var build] && !files.Keys.SequenceEqual(build.Files.Keys, StringComparer.Ordinal))
            {
                errors.Add("patch.files", "must list the same files as builds[0].files.");
            }

            return errors.Any || url is null || sha is null || files is null
                ? null
                : new PackPatch(patch.Format!, url, sha, size, files);
        }

        private List<PackDependency> Dependencies(IReadOnlyList<PackDependencyJson?>? list)
        {
            var dependencies = new List<PackDependency>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < (list?.Count ?? 0); i++)
            {
                var field = $"dependencies[{i}]";
                if (list![i] is not { } entry)
                {
                    errors.Add(field, "is empty.");
                    continue;
                }

                var id = Slug($"{field}.id", entry.Id);
                var version = Text($"{field}.version", entry.Version, MaxVersionLength, required: true);
                if (id is not null && !ids.Add(id))
                {
                    errors.Add($"{field}.id", $"'{id}' is listed twice.");
                }
                else if (id is not null && version is not null)
                {
                    dependencies.Add(new PackDependency(id, version));
                }
            }

            return dependencies;
        }

        private List<string> Fonts(IReadOnlyList<string?>? list)
        {
            var fonts = new List<string>();
            for (var i = 0; i < (list?.Count ?? 0); i++)
            {
                var font = Slug($"fonts[{i}]", list![i]);
                if (font is not null && fonts.Contains(font, StringComparer.Ordinal))
                {
                    errors.Add($"fonts[{i}]", $"'{font}' is listed twice.");
                }
                else if (font is not null)
                {
                    fonts.Add(font);
                }
            }

            return fonts;
        }

        private PackCoverage? Coverage(PackCoverageJson? coverage)
        {
            if (coverage is null)
            {
                errors.Add("coverage", "is missing.");
                return null;
            }

            var count = errors.List.Count;
            if (coverage.Translated is null)
            {
                errors.Add("coverage.translated", "is missing.");
            }
            else if (coverage.Translated < 0)
            {
                errors.Add("coverage.translated", $"must be 0 or more, not {coverage.Translated}.");
            }

            if (coverage.Total is null)
            {
                errors.Add("coverage.total", "is missing.");
            }
            else if (coverage.Total < 1)
            {
                errors.Add("coverage.total", $"must be 1 or more, not {coverage.Total}.");
            }

            if (errors.List.Count > count)
            {
                return null;
            }

            if (coverage.Translated > coverage.Total)
            {
                errors.Add("coverage.translated", $"is {coverage.Translated}, more than the total of {coverage.Total} strings.");
                return null;
            }

            return new PackCoverage(coverage.Translated!.Value, coverage.Total!.Value);
        }

        private void License(string? license)
        {
            if (license is null)
            {
                errors.Add("license", $"is missing (registry content is {PackManifest.License}).");
            }
            else if (license != PackManifest.License)
            {
                errors.Add("license", $"'{license}' is not allowed: registry content is {PackManifest.License}.");
            }
        }

        private DateTimeOffset? Date(string field, string? text)
        {
            if (text is null)
            {
                errors.Add(field, "is missing.");
                return null;
            }

            if (!PackFormat.TryParseDate(text, out var date))
            {
                errors.Add(field, $"'{text}' is not a date and time with a time zone (e.g. 2026-11-04T12:00:00Z).");
                return null;
            }

            return date;
        }

        private (DateTimeOffset Created, DateTimeOffset Updated) Dates(string? createdText, string? updatedText)
        {
            var created = Date("created", createdText);
            var updated = Date("updated", updatedText);
            if (created is { } c && updated is { } u && u < c)
            {
                errors.Add("updated", "is earlier than created.");
            }

            return (created ?? default, updated ?? default);
        }
    }
}

/// <summary>Writer options: indented, non-ASCII text written as is (translations stay readable).</summary>
internal static class PackJsonWriter
{
    public static PackJsonContext Context { get; } = new(new JsonSerializerOptions(PackJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}
