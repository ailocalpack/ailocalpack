using AiLocalPack.Core.Generation;

namespace AiLocalPack.Pipeline;

/// <summary>
/// The glossary entries a chunk needs (task 50): those whose term occurs in one of its originals, ignoring case. In
/// scripts that separate words with spaces a term must start a word (<c>Potion</c> matches <c>Potions</c> and
/// <c>potion's</c>, not <c>Hipotion</c>), so inflected and plural forms still count; in Chinese, Japanese and Thai any
/// occurrence counts. Entries keep the glossary's order.
/// </summary>
public static class GlossaryFilter
{
    public static IReadOnlyList<GlossaryEntry> Relevant(Glossary? glossary, IEnumerable<string> originals)
    {
        ArgumentNullException.ThrowIfNull(originals);
        if (glossary is null || glossary.Entries.Count == 0)
        {
            return [];
        }

        var texts = originals.ToList();
        return [.. glossary.Entries.Where(e => texts.Any(t => Mentions(t, e.Term)))];
    }

    /// <summary>Whether <paramref name="text"/> contains <paramref name="term"/> as described above.</summary>
    public static bool Mentions(string text, string term)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(term);
        if (term.Length == 0)
        {
            return false;
        }

        for (var at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(term, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (at == 0 || !IsSpacedWordChar(term[0]) || !IsSpacedWordChar(text[at - 1]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A letter or digit of a script that puts spaces between words.</summary>
    private static bool IsSpacedWordChar(char c) => char.IsLetterOrDigit(c) && !IsUnspacedScript(c);

    private static bool IsUnspacedScript(char c) =>
        (int)c is (>= 0x0E00 and <= 0x0EFF) // Thai, Lao
            or (>= 0x3040 and <= 0x30FF) // Hiragana, Katakana
            or (>= 0x3400 and <= 0x4DBF) // CJK extension A
            or (>= 0x4E00 and <= 0x9FFF) // CJK unified ideographs
            or (>= 0xF900 and <= 0xFAFF) // CJK compatibility ideographs
            or (>= 0xFF66 and <= 0xFF9F); // Halfwidth Katakana
}
