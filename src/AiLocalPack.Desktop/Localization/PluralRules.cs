using System.Globalization;

namespace AiLocalPack.Desktop.Localization;

/// <summary>CLDR plural category of a count. Resource keys for plural strings end in <c>_One</c>, <c>_Few</c>, <c>_Many</c> or <c>_Other</c>.</summary>
public enum PluralCategory
{
    One,
    Few,
    Many,
    Other,
}

/// <summary>
/// CLDR cardinal plural rules for whole numbers, per UI language. Languages without their own rule use the English one
/// (<c>one</c> / <c>other</c>); add a rule here together with a new UI language that needs it.
/// </summary>
public static class PluralRules
{
    private static readonly PluralCategory[] OneOther = [PluralCategory.One, PluralCategory.Other];
    private static readonly PluralCategory[] OneFewMany = [PluralCategory.One, PluralCategory.Few, PluralCategory.Many];

    /// <summary>The forms every plural key must have in <paramref name="culture"/>'s resource file.</summary>
    public static IReadOnlyList<PluralCategory> CategoriesFor(CultureInfo culture) =>
        Language(culture) switch
        {
            "ru" => OneFewMany,
            _ => OneOther,
        };

    public static PluralCategory Select(CultureInfo culture, long count)
    {
        var n = Math.Abs(count);
        return Language(culture) switch
        {
            "ru" => Russian(n),
            _ => n == 1 ? PluralCategory.One : PluralCategory.Other,
        };
    }

    /// <summary><c>Library_GameCount_One</c> → <c>Library_GameCount</c>; a key without a plural suffix is returned as is.</summary>
    public static string BaseKey(string key)
    {
        foreach (var category in Enum.GetValues<PluralCategory>())
        {
            var suffix = $"_{category}";
            if (key.EndsWith(suffix, StringComparison.Ordinal))
            {
                return key[..^suffix.Length];
            }
        }

        return key;
    }

    /// <summary>The plural form of a resource key, or <c>null</c> when the key is not a plural form.</summary>
    public static PluralCategory? CategoryOf(string key) =>
        Enum.GetValues<PluralCategory>().Cast<PluralCategory?>().FirstOrDefault(c => key.EndsWith($"_{c}", StringComparison.Ordinal));

    private static PluralCategory Russian(long n)
    {
        var (mod10, mod100) = (n % 10, n % 100);
        if (mod10 == 1 && mod100 != 11)
        {
            return PluralCategory.One;
        }

        return mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? PluralCategory.Few : PluralCategory.Many;
    }

    private static string Language(CultureInfo culture) => culture.TwoLetterISOLanguageName;
}
