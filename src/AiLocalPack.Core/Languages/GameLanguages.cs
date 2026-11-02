using System.Globalization;

namespace AiLocalPack.Core.Languages;

/// <summary>
/// The languages a game can be translated into: the player's "Translate games into" choice, pack languages in the
/// registry (<c>games/&lt;game-id&gt;/&lt;language&gt;/</c>) and generation targets. BCP 47 tags; the set follows
/// the languages Steam supports.
/// </summary>
public static class GameLanguages
{
    /// <summary>Used when neither the setting nor the OS gives a known language.</summary>
    public const string Fallback = "en";

    // Where one language has several variants, the more widely played one comes first: it is picked for an OS
    // culture that names only the language (pt → pt-BR, es → es-ES).
    public static IReadOnlyList<string> Tags { get; } =
    [
        "ar", "bg", "cs", "da", "de", "el", "en", "es-ES", "es-419", "fi", "fr", "hu", "id", "it", "ja", "ko", "nb",
        "nl", "pl", "pt-BR", "pt-PT", "ro", "ru", "sv", "th", "tr", "uk", "vi", "zh-Hans", "zh-Hant",
    ];

    /// <summary>Whether <paramref name="tag"/> is one of <see cref="Tags"/> (case-insensitive).</summary>
    public static bool IsKnown(string? tag) => Find(tag) is not null;

    /// <summary>
    /// The language games are translated into: the setting when it is a known tag, else the OS UI culture
    /// (<c>ru-RU</c> → <c>ru</c>, <c>zh-CN</c> → <c>zh-Hans</c>, <c>es-MX</c> → <c>es-419</c>, <c>pt</c> → <c>pt-BR</c>), else <see cref="Fallback"/>.
    /// </summary>
    public static string Resolve(string? setting, CultureInfo osCulture) =>
        Find(setting) ?? FromCulture(osCulture) ?? Fallback;

    /// <summary>The tag <paramref name="culture"/> maps to, or <c>null</c> when its language is not in <see cref="Tags"/>.</summary>
    public static string? FromCulture(CultureInfo? culture)
    {
        for (var c = culture; c is not null && !c.Equals(CultureInfo.InvariantCulture); c = c.Parent)
        {
            if (Find(c.Name) is { } tag)
            {
                return tag;
            }
        }

        if (culture is null || culture.Equals(CultureInfo.InvariantCulture))
        {
            return null;
        }

        var language = culture.TwoLetterISOLanguageName;
        if (!culture.IsNeutralCulture && RegionalVariant.TryGetValue(language, out var regional))
        {
            return regional;
        }

        return Tags.FirstOrDefault(t => CultureInfo.GetCultureInfo(t).TwoLetterISOLanguageName == language);
    }

    // A regional culture with no tag of its own: Spanish outside Spain reads Latin American Spanish, Portuguese
    // outside Brazil reads European Portuguese.
    private static readonly Dictionary<string, string> RegionalVariant = new(StringComparer.Ordinal)
    {
        ["es"] = "es-419",
        ["pt"] = "pt-PT",
    };

    private static string? Find(string? tag) =>
        string.IsNullOrWhiteSpace(tag) ? null : Tags.FirstOrDefault(t => string.Equals(t, tag.Trim(), StringComparison.OrdinalIgnoreCase));
}
