using System.Globalization;

namespace AiLocalPack.Desktop.ViewModels;

/// <summary>
/// Search-box matching shared by the screens (Settings, Library): case and diacritics insensitive substring match
/// ("francais" finds "Français", "ёлка" and "елка" match each other). A blank query matches nothing.
/// </summary>
public static class TextSearch
{
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;
    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;

    /// <summary>Where <paramref name="query"/> is in <paramref name="text"/> (to highlight it), or <c>null</c>.</summary>
    public static (int Start, int Length)? Find(string? text, string? query)
    {
        var q = query?.Trim();
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(q))
        {
            return null;
        }

        var start = Compare.IndexOf(text, q, Options, out var length);
        return start < 0 ? null : (start, length);
    }

    public static bool Matches(string? text, string? query) => Find(text, query) is not null;
}
