using System.Globalization;

namespace AiLocalPack.Core.Languages;

/// <summary>Language names as players expect to see them in a language picker.</summary>
public static class LanguageNames
{
    /// <summary>
    /// The language's name in that language, starting with a capital letter where the script has one
    /// (<c>ru</c> → <c>Русский</c>, <c>pt-BR</c> → <c>Português (Brasil)</c>, <c>ja</c> → <c>日本語</c>).
    /// </summary>
    public static string Native(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var name = culture.NativeName;
        return name.Length == 0 ? culture.Name : string.Concat(culture.TextInfo.ToUpper(name[0]).ToString(), name.AsSpan(1));
    }

    /// <summary><see cref="Native(CultureInfo)"/> for a language tag; the tag itself when .NET doesn't know it.</summary>
    public static string Native(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        try
        {
            return Native(CultureInfo.GetCultureInfo(tag, predefinedOnly: true));
        }
        catch (CultureNotFoundException)
        {
            return tag;
        }
    }

    /// <summary>Orders names for a picker: case-insensitive, Latin, then other scripts (invariant culture order).</summary>
    public static StringComparer Order { get; } = StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase);
}
