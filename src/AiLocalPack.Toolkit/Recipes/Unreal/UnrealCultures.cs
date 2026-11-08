using System.Globalization;

namespace AiLocalPack.Toolkit.Recipes.Unreal;

/// <summary>
/// Which of a game's cultures (its <c>Localization/&lt;target&gt;/&lt;culture&gt;</c> folders) a string table's language
/// replaces. Games name cultures their own way (<c>ru</c> or <c>ru-RU</c>, <c>zh-Hans</c> or <c>zh-CN</c>, <c>es-MX</c> for
/// Latin American Spanish), so the match goes: the same name, then the names Unreal and stores use for the same
/// language, then the bare language for a regional tag; never another variant of the language (Brazilian Portuguese
/// isn't European Portuguese, Simplified Chinese isn't Traditional).
/// </summary>
public static class UnrealCultures
{
    /// <summary>Other names of a language tag, best first (<see cref="AiLocalPack.Core.Languages.GameLanguages"/> tags).</summary>
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["es-ES"] = ["es"],
        ["es-419"] = ["es-MX", "es-US", "es-AR", "es-CO", "es-CL"],
        ["pt-PT"] = ["pt"],
        ["pt-BR"] = [],
        ["zh-Hans"] = ["zh-CN", "zh-Hans-CN", "zh-SG", "zh"],
        ["zh-Hant"] = ["zh-TW", "zh-Hant-TW", "zh-HK", "zh-Hant-HK"],
        ["nb"] = ["nb-NO", "no", "no-NO"],
    };

    /// <summary>
    /// The culture of <paramref name="cultures"/> that <paramref name="language"/> replaces, or <c>null</c> when the
    /// game has none (the translation then goes over the native culture).
    /// </summary>
    public static string? Pick(string language, IEnumerable<string> cultures)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentNullException.ThrowIfNull(cultures);
        var available = cultures.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        var exact = available.FirstOrDefault(c => c.Equals(language, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        if (Aliases.TryGetValue(language, out var aliases))
        {
            return aliases.Select(a => available.FirstOrDefault(c => c.Equals(a, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(c => c is not null);
        }

        var dash = language.IndexOf('-', StringComparison.Ordinal);
        if (dash > 0)
        {
            // A regional tag the table doesn't list variants of: its bare language.
            return available.FirstOrDefault(c => c.Equals(language[..dash], StringComparison.OrdinalIgnoreCase));
        }

        // A bare language: the game's only regional culture of it, else the language's main region (fr → fr-FR, ja → ja-JP).
        var regional = available.Where(c => c.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase)).ToList();
        if (regional.Count == 1)
        {
            return regional[0];
        }

        var main = MainRegion(language);
        return main is null ? null : regional.FirstOrDefault(c => c.Equals(main, StringComparison.OrdinalIgnoreCase));
    }

    private static string? MainRegion(string language)
    {
        try
        {
            var specific = CultureInfo.CreateSpecificCulture(language).Name;
            return specific.Equals(language, StringComparison.OrdinalIgnoreCase) ? null : specific;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }
}
