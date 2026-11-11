using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Languages;
using AiLocalPack.Core.Packs;

namespace AiLocalPack.Core.Registry;

/// <summary>Address of one pack in the registry: <c>steam:620/ru/full</c>.</summary>
public sealed record RegistryPackKey(GameId Game, string Language, string Id)
{
    public override string ToString() => $"{Game}/{Language}/{Id}";

    /// <summary>Parses <c>&lt;game id&gt;/&lt;language&gt;/&lt;pack id&gt;</c> with a supported language and a valid pack ID.</summary>
    public static bool TryParse(string? text, out RegistryPackKey key)
    {
        key = null!;
        var parts = text?.Split('/');
        if (parts is not { Length: 3 } || !GameId.TryParse(parts[0], out var game)
            || !GameLanguages.Tags.Contains(parts[1], StringComparer.Ordinal) || !PackFormat.IsSlug(parts[2]))
        {
            return false;
        }

        key = new RegistryPackKey(game, parts[1], parts[2]);
        return true;
    }
}

/// <summary>Community numbers of one pack, as the registry's scheduled Action collects them from its Discussion.</summary>
/// <param name="Revision">
/// The pack revision <see cref="RegistryRating.Works"/> / <see cref="RegistryRating.DoesNotWork"/> count reports of
/// (task 64: only the current revision's count); <c>null</c> in a file written by hand = the current one.
/// </param>
public sealed record RegistryPackRating(RegistryRating Rating, Uri? Discussion, int? Revision = null);

/// <summary>
/// <c>ratings.json</c>, written by <c>ratings collect</c> (task 64) and read by <c>index build --ratings</c>:
/// <c>{schemaVersion: 1, packs: {"steam:620/ru/full": {likes, works, doesNotWork, revision?, discussion?}}}</c>.
/// </summary>
public static class RegistryRatings
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>The ratings, or every problem (the whole file is refused: a half-wrong ratings file is not published).</summary>
    public static (IReadOnlyDictionary<RegistryPackKey, RegistryPackRating>? Ratings, IReadOnlyList<PackFormatError> Errors) Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        RatingsFileJson? file;
        try
        {
            file = JsonSerializer.Deserialize(json, RegistryInputsJsonContext.Default.RatingsFileJson);
        }
        catch (JsonException e)
        {
            return (null, [PackFormat.FromJsonException(e)]);
        }

        if (file is null)
        {
            return (null, [new PackFormatError("", "The file holds no ratings (null).")]);
        }

        if (PackFormat.CheckSchemaVersion(file.SchemaVersion, CurrentSchemaVersion) is { } versionError)
        {
            return (null, [versionError]);
        }

        var errors = new PackFormatErrors();
        var ratings = new Dictionary<RegistryPackKey, RegistryPackRating>();
        foreach (var (text, entry) in file.Packs ?? new Dictionary<string, RatingJson?>())
        {
            var field = PackFormat.Key("packs", text);
            if (!RegistryPackKey.TryParse(text, out var key))
            {
                errors.Add(field, $"'{text}' is not a pack (<game id>/<language>/<pack id>).");
                continue;
            }

            if (entry is null)
            {
                errors.Add(field, "is empty.");
                continue;
            }

            int Count(string name, int? value)
            {
                if (value < 0)
                {
                    errors.Add($"{field}.{name}", $"must be 0 or more, not {value}.");
                }

                return Math.Max(value ?? 0, 0);
            }

            var rating = new RegistryRating(Count("likes", entry.Likes), Count("works", entry.Works), Count("doesNotWork", entry.DoesNotWork));
            Uri? discussion = null;
            if (entry.Discussion is { } link
                && (!Uri.TryCreate(link, UriKind.Absolute, out discussion) || discussion.Scheme != Uri.UriSchemeHttps))
            {
                errors.Add($"{field}.discussion", $"'{link}' is not an https:// URL.");
            }

            if (entry.Revision is < 1)
            {
                errors.Add($"{field}.revision", $"must be 1 or more, not {entry.Revision}.");
            }

            ratings[key] = new RegistryPackRating(rating, discussion, entry.Revision);
        }

        return errors.Any ? (null, errors.List) : (ratings, []);
    }

    /// <summary>The file <see cref="Read"/> reads: packs in ordinal order, indented, <c>\n</c> line ends and a trailing newline.</summary>
    public static string Write(IReadOnlyDictionary<RegistryPackKey, RegistryPackRating> ratings)
    {
        ArgumentNullException.ThrowIfNull(ratings);
        var packs = new SortedDictionary<string, RatingJson?>(StringComparer.Ordinal);
        foreach (var (key, value) in ratings)
        {
            packs[key.ToString()] = new RatingJson(
                value.Rating.Likes, value.Rating.Works, value.Rating.DoesNotWork, value.Discussion?.AbsoluteUri, value.Revision);
        }

        var json = JsonSerializer.Serialize(new RatingsFileJson(CurrentSchemaVersion, packs), RegistryRatingsWriteContext.Default.RatingsFileJson);
        return json.ReplaceLineEndings("\n") + "\n";
    }
}

/// <summary>
/// <c>policy/opt-out.json</c> in the registry: games whose publisher asked not to distribute fan translations
/// (<c>{schemaVersion: 1, games: [{game, note?}]}</c>; the note, e.g. a link to the request, is for maintainers).
/// Task 65 adds its schema and CI checks.
/// </summary>
public static class RegistryOptOuts
{
    public const int CurrentSchemaVersion = 1;

    public const string Path = "policy/opt-out.json";

    /// <summary>The games, or every problem (the whole file is refused: dropping an entry would unblock a game).</summary>
    public static (IReadOnlySet<GameId>? Games, IReadOnlyList<PackFormatError> Errors) Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        OptOutFileJson? file;
        try
        {
            file = JsonSerializer.Deserialize(json, RegistryInputsJsonContext.Default.OptOutFileJson);
        }
        catch (JsonException e)
        {
            return (null, [PackFormat.FromJsonException(e)]);
        }

        if (file is null)
        {
            return (null, [new PackFormatError("", "The file holds no list (null).")]);
        }

        if (PackFormat.CheckSchemaVersion(file.SchemaVersion, CurrentSchemaVersion) is { } versionError)
        {
            return (null, [versionError]);
        }

        var errors = new PackFormatErrors();
        var games = new HashSet<GameId>();
        for (var i = 0; i < (file.Games?.Count ?? 0); i++)
        {
            var text = file.Games![i]?.Game;
            if (!GameId.TryParse(text, out var game))
            {
                errors.Add($"games[{i}].game", $"'{text}' is not a game ID (steam:<app id> or fp:<64 lowercase hex>).");
            }
            else if (!games.Add(game))
            {
                errors.Add($"games[{i}].game", $"{game} is listed twice.");
            }
        }

        return errors.Any ? (null, errors.List) : (games, []);
    }
}

internal sealed record RatingsFileJson(int? SchemaVersion, IReadOnlyDictionary<string, RatingJson?>? Packs);

internal sealed record RatingJson(int? Likes, int? Works, int? DoesNotWork, string? Discussion, int? Revision = null);

internal sealed record OptOutFileJson(int? SchemaVersion, IReadOnlyList<OptOutJson?>? Games);

internal sealed record OptOutJson(string? Game, string? Note);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(RatingsFileJson))]
[JsonSerializable(typeof(OptOutFileJson))]
internal sealed partial class RegistryInputsJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RatingsFileJson))]
internal sealed partial class RegistryRatingsWriteContext : JsonSerializerContext;
