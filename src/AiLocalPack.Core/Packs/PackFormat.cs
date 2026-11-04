using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiLocalPack.Core.Packs;

/// <summary>One problem in a pack file, in plain language.</summary>
/// <param name="Field">Where: a JSON path without the leading <c>$.</c> (<c>authors[1].github</c>), or empty for the whole file.</param>
public sealed record PackFormatError(string Field, string Message)
{
    public override string ToString() => Field.Length == 0 ? Message : $"{Field}: {Message}";
}

/// <summary>Rules shared by the pack manifest and the string table (see the schemas under <c>schemas/</c>).</summary>
internal static partial class PackFormat
{
    public static bool IsSlug(string? text) => text is { Length: > 0 and <= 64 } && SlugPattern().IsMatch(text);

    public static bool IsSha256(string? text) => text is not null && Sha256Pattern().IsMatch(text);

    public static bool IsGitHubLogin(string? text) => text is { Length: > 0 and <= 39 } && GitHubLoginPattern().IsMatch(text);

    public static bool IsSemVer(string? text) => text is not null && SemVerPattern().IsMatch(text);

    public static bool IsText(string? text, int maxLength) =>
        text is not null && text.Length <= maxLength && !string.IsNullOrWhiteSpace(text);

    /// <summary>Relative, '/'-separated, no empty / '.' / '..' parts, no backslashes.</summary>
    public static bool IsRelativePath(string? path) =>
        !string.IsNullOrEmpty(path)
        && !path.Contains('\\', StringComparison.Ordinal)
        && path.Split('/').All(part => part is not ("" or "." or ".."));

    /// <summary>RFC 3339 date-time with an explicit offset (<c>2026-11-04T12:00:00Z</c>).</summary>
    public static bool TryParseDate(string? text, out DateTimeOffset date)
    {
        date = default;
        return text is not null
            && DatePattern().IsMatch(text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    /// <summary>UTC, fractional seconds only when present.</summary>
    public static string FormatDate(DateTimeOffset date) =>
        date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    /// <summary>A plain-language error for a <see cref="JsonException"/> thrown while reading.</summary>
    public static PackFormatError FromJsonException(JsonException e)
    {
        var field = FieldOf(e.Path);
        var where = e.LineNumber is { } line ? $" (line {line + 1}, column {(e.BytePositionInLine ?? 0) + 1})" : "";
        if (e.Message.StartsWith("The JSON value could not be converted", StringComparison.Ordinal))
        {
            return new PackFormatError(field, $"has the wrong type{where}.");
        }

        if (e.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase))
        {
            return new PackFormatError(field, $"a property is listed twice{where}.");
        }

        return new PackFormatError("", $"Not valid JSON{where}.");
    }

    /// <summary><c>$.authors[1].github</c> → <c>authors[1].github</c>.</summary>
    public static string FieldOf(string? jsonPath) =>
        jsonPath is null or "$" ? "" : jsonPath.StartsWith("$.", StringComparison.Ordinal) ? jsonPath[2..] : jsonPath.TrimStart('$');

    /// <summary><c>files["a/b.json"]</c>.</summary>
    public static string Key(string field, string key) => $"{field}[{JsonSerializer.Serialize(key, PackJsonContext.Default.String)}]";

    /// <summary>Errors for a schemaVersion other than <paramref name="current"/>, or none.</summary>
    public static PackFormatError? CheckSchemaVersion(int? version, int current) => version switch
    {
        null => new PackFormatError("schemaVersion", "is missing."),
        _ when version == current => null,
        > 0 when version > current => new PackFormatError(
            "schemaVersion", $"is {version}: made for a newer version of the app (this one reads {current}). Update the app."),
        _ => new PackFormatError("schemaVersion", $"must be {current}, not {version}."),
    };

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex("^[A-Za-z0-9](-?[A-Za-z0-9])*$")]
    private static partial Regex GitHubLoginPattern();

    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")]
    private static partial Regex SemVerPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[Tt]\d{2}:\d{2}:\d{2}(\.\d+)?([Zz]|[+-]\d{2}:\d{2})$")]
    private static partial Regex DatePattern();
}

/// <summary>Collects errors while a file is checked.</summary>
internal sealed class PackFormatErrors
{
    private readonly List<PackFormatError> _errors = [];

    public IReadOnlyList<PackFormatError> List => _errors;

    public bool Any => _errors.Count > 0;

    public void Add(string field, string message) => _errors.Add(new PackFormatError(field, message));

    public void Add(PackFormatError error) => _errors.Add(error);
}
