using System.Globalization;

namespace AiLocalPack.Desktop.ViewModels.Settings;

public interface ISearchableRow
{
    /// <summary>Texts a query is matched against (label, keywords).</summary>
    IEnumerable<string> SearchTexts { get; }
}

public interface ISearchableGroup
{
    IEnumerable<string> SearchTexts { get; }

    IEnumerable<ISearchableRow> Rows { get; }
}

public interface ISearchableSection
{
    IEnumerable<string> SearchTexts { get; }

    IEnumerable<ISearchableGroup> Groups { get; }
}

/// <summary>
/// Settings search: case and diacritics insensitive substring match ("francais" finds "Français", "ёлка" and "елка"
/// match each other). A row matches by its own texts; a matching group or section title brings all its rows.
/// </summary>
public static class SettingsSearch
{
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;
    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;

    /// <summary>Where <paramref name="query"/> is in <paramref name="text"/> (to highlight it), or <c>null</c>. A blank query matches nothing.</summary>
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

    /// <summary>
    /// The sections with matching rows, in section order, rows in their order. A section whose title matches is listed
    /// even without rows (its link still opens it). A blank query gives no results.
    /// </summary>
    public static IReadOnlyList<(TSection Section, IReadOnlyList<TRow> Rows)> Run<TSection, TRow>(IEnumerable<TSection> sections, string? query)
        where TSection : ISearchableSection
        where TRow : ISearchableRow
    {
        ArgumentNullException.ThrowIfNull(sections);
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var results = new List<(TSection Section, IReadOnlyList<TRow> Rows)>();
        foreach (var section in sections)
        {
            var sectionHit = section.SearchTexts.Any(t => Matches(t, query));
            var rows = section.Groups
                .SelectMany(g => sectionHit || g.SearchTexts.Any(t => Matches(t, query))
                    ? g.Rows
                    : g.Rows.Where(r => r.SearchTexts.Any(t => Matches(t, query))))
                .OfType<TRow>()
                .ToList();
            if (sectionHit || rows.Count > 0)
            {
                results.Add((section, rows));
            }
        }

        return results;
    }
}
