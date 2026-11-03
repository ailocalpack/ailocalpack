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
/// Settings search (<see cref="TextSearch"/> matching). A row matches by its own texts; a matching group or section title
/// brings all its rows.
/// </summary>
public static class SettingsSearch
{
    /// <inheritdoc cref="TextSearch.Find"/>
    public static (int Start, int Length)? Find(string? text, string? query) => TextSearch.Find(text, query);

    public static bool Matches(string? text, string? query) => TextSearch.Matches(text, query);

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
