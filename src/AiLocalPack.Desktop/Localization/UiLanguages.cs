using System.Globalization;

namespace AiLocalPack.Desktop.Localization;

/// <summary>The languages the app UI ships in, and how the UI language is picked.</summary>
public static class UiLanguages
{
    /// <summary>English is the neutral (fallback) language: <c>Strings.resx</c>. Every other one has <c>Strings.&lt;tag&gt;.resx</c>.</summary>
    public const string Fallback = "en";

    /// <summary>
    /// BCP 47 tags, also the satellite folder names. A tag with a region or script (<c>pt-BR</c>, <c>zh-Hans</c>) is the
    /// variant the file is written in; <see cref="Resolve"/> also gives it to the language's other variants.
    /// </summary>
    public static IReadOnlyList<string> Supported { get; } =
        [Fallback, "de", "es", "fr", "it", "ja", "ko", "pl", "pt-BR", "ru", "tr", "uk", "zh-Hans"];

    /// <summary>The OS UI culture, captured at startup before the app overrides the process UI culture.</summary>
    public static CultureInfo Os { get; } = CultureInfo.CurrentUICulture;

    /// <summary>
    /// Picks the UI culture: the language from settings, else the OS UI culture, else English. A specific culture
    /// (<c>ru-RU</c>, <c>zh-CN</c> → <c>zh-Hans</c>) is kept when its language is supported, so numbers and dates follow
    /// its region; another variant of a supported language gets the supported one (<c>pt-PT</c> → <c>pt-BR</c>), except
    /// Traditional Chinese, which stays in English rather than read Simplified.
    /// </summary>
    public static CultureInfo Resolve(string? setting, CultureInfo osCulture) =>
        Match(Parse(setting)) ?? Match(osCulture) ?? CultureInfo.GetCultureInfo(Fallback);

    /// <summary>The supported tag <paramref name="culture"/> reads (<c>zh-CN</c> → <c>zh-Hans</c>), <see cref="Fallback"/> when none.</summary>
    public static string TagOf(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return Own(culture) ?? Fallback;
    }

    private static string? Own(CultureInfo culture)
    {
        for (var c = culture; !c.Equals(CultureInfo.InvariantCulture); c = c.Parent)
        {
            if (Supported.FirstOrDefault(t => string.Equals(t, c.Name, StringComparison.OrdinalIgnoreCase)) is { } tag)
            {
                return tag;
            }
        }

        return null;
    }

    private static CultureInfo? Parse(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        try
        {
            return CultureInfo.GetCultureInfo(tag.Trim(), predefinedOnly: true);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    private static CultureInfo? Match(CultureInfo? culture)
    {
        if (culture is null || culture.Equals(CultureInfo.InvariantCulture))
        {
            return null;
        }

        if (Own(culture) is not null)
        {
            return culture;
        }

        // Resources follow the parent chain (pt-PT → pt), which never reaches pt-BR: hand over the supported variant.
        var language = culture.TwoLetterISOLanguageName;
        var variant = language == "zh" ? null : Supported.FirstOrDefault(t => CultureInfo.GetCultureInfo(t).TwoLetterISOLanguageName == language);
        return variant is null ? null : CultureInfo.GetCultureInfo(variant);
    }
}
