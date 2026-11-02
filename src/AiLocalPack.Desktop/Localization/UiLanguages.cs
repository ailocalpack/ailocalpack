using System.Globalization;

namespace AiLocalPack.Desktop.Localization;

/// <summary>The languages the app UI ships in, and how the UI language is picked.</summary>
public static class UiLanguages
{
    /// <summary>English is the neutral (fallback) language: <c>Strings.resx</c>. Every other one has <c>Strings.&lt;tag&gt;.resx</c>.</summary>
    public const string Fallback = "en";

    public static IReadOnlyList<string> Supported { get; } = [Fallback, "ru"];

    /// <summary>The OS UI culture, captured at startup before the app overrides the process UI culture.</summary>
    public static CultureInfo Os { get; } = CultureInfo.CurrentUICulture;

    /// <summary>
    /// Picks the UI culture: the language from settings, else the OS UI culture, else English. A specific culture
    /// (<c>ru-RU</c>) is kept when its language is supported, so numbers and dates follow its region.
    /// </summary>
    public static CultureInfo Resolve(string? setting, CultureInfo osCulture) =>
        Match(Parse(setting)) ?? Match(osCulture) ?? CultureInfo.GetCultureInfo(Fallback);

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
        for (var c = culture; c is not null && !c.Equals(CultureInfo.InvariantCulture); c = c.Parent)
        {
            if (Supported.Contains(c.Name, StringComparer.OrdinalIgnoreCase))
            {
                return culture;
            }
        }

        return null;
    }
}
