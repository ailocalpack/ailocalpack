namespace AiLocalPack.Core.Generation;

/// <summary>
/// The player's changes to a glossary in the review (task 54, design §6c). Pure. An entry the player changed is marked
/// <see cref="GlossaryEntry.Edited"/>: the agent can't replace or remove it any more (<c>put_glossary</c>) and
/// <see cref="ForRegenerate"/> keeps it, so a regenerated glossary replaces only what the player didn't touch.
/// </summary>
public static class GlossaryReview
{
    /// <summary>
    /// The glossary with <paramref name="term"/> translated as <paramref name="translation"/> (trimmed) and marked edited;
    /// a translation equal to the term keeps it as is. Unchanged when the term isn't there or the text is the same.
    /// </summary>
    /// <exception cref="ArgumentException">The translation is blank or longer than <see cref="Glossary.MaxTextLength"/>.</exception>
    public static Glossary SetTranslation(Glossary glossary, string term, string translation)
    {
        ArgumentNullException.ThrowIfNull(glossary);
        ArgumentNullException.ThrowIfNull(term);
        ArgumentException.ThrowIfNullOrWhiteSpace(translation);
        var text = translation.Trim();
        if (text.Length > Glossary.MaxTextLength)
        {
            throw new ArgumentException($"The translation is longer than {Glossary.MaxTextLength} characters.", nameof(translation));
        }

        var at = IndexOf(glossary, term);
        if (at < 0 || string.Equals(glossary.Entries[at].Translation, text, StringComparison.Ordinal))
        {
            return glossary;
        }

        var entries = glossary.Entries.ToList();
        var entry = entries[at];
        entries[at] = entry with
        {
            Translation = text,
            KeepAsIs = string.Equals(text, entry.Term, StringComparison.Ordinal),
            Edited = true,
        };
        return glossary with { Entries = entries };
    }

    /// <summary>The glossary with the player's form of address (<c>null</c> = as the agent's tone says).</summary>
    public static Glossary SetFormOfAddress(Glossary glossary, FormOfAddress? address)
    {
        ArgumentNullException.ThrowIfNull(glossary);
        return glossary with { FormOfAddress = address };
    }

    /// <summary>
    /// What <c>Regenerate</c> leaves for the agent to complete: only the entries the player edited and the player's form
    /// of address; the agent's entries and tone go.
    /// </summary>
    public static Glossary ForRegenerate(Glossary glossary)
    {
        ArgumentNullException.ThrowIfNull(glossary);
        return glossary with { Tone = null, Entries = [.. glossary.Entries.Where(e => e.Edited)] };
    }

    /// <summary>The agent stored something of its own: a tone or an entry the player didn't edit.</summary>
    public static bool HasAgentPart(Glossary glossary)
    {
        ArgumentNullException.ThrowIfNull(glossary);
        return glossary.Tone is not null || glossary.Entries.Any(e => !e.Edited);
    }

    /// <summary>
    /// The tone line the translation prompt gets: the agent's tone, then the player's form of address, which wins
    /// (<c>informal, playful. Form of address chosen by the player: informal (ты); it overrides anything above.</c>).
    /// <c>null</c> when neither is set.
    /// </summary>
    public static string? ToneForPrompt(Glossary? glossary)
    {
        if (glossary is null)
        {
            return null;
        }

        var tone = string.IsNullOrWhiteSpace(glossary.Tone) ? null : glossary.Tone.Trim();
        if (glossary.FormOfAddress is not { } address)
        {
            return tone;
        }

        var word = address == FormOfAddress.Informal ? "informal" : "formal";
        var pronoun = FormsOfAddress.Pronoun(glossary.Language, address) is { } p ? $" ({p})" : string.Empty;
        var choice = $"Form of address chosen by the player: {word}{pronoun}; it overrides anything above.";
        return tone is null ? choice : $"{tone.TrimEnd('.')}. {choice}";
    }

    private static int IndexOf(Glossary glossary, string term)
    {
        for (var i = 0; i < glossary.Entries.Count; i++)
        {
            if (Glossary.TermComparer.Equals(glossary.Entries[i].Term, term))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>The informal and formal "you" of languages that make the difference (shown in the review's tone list).</summary>
public static class FormsOfAddress
{
    private static readonly Dictionary<string, (string Informal, string Formal)> Pronouns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bg"] = ("ти", "Вие"),
        ["cs"] = ("ty", "Vy"),
        ["da"] = ("du", "De"),
        ["de"] = ("du", "Sie"),
        ["el"] = ("εσύ", "εσείς"),
        ["es-ES"] = ("tú", "usted"),
        ["es-419"] = ("tú", "usted"),
        ["fi"] = ("sinä", "te"),
        ["fr"] = ("tu", "vous"),
        ["hu"] = ("te", "ön"),
        ["it"] = ("tu", "Lei"),
        ["nb"] = ("du", "De"),
        ["nl"] = ("jij", "u"),
        ["pl"] = ("ty", "pan / pani"),
        ["pt-BR"] = ("você", "o senhor"),
        ["pt-PT"] = ("tu", "você"),
        ["ro"] = ("tu", "dumneavoastră"),
        ["ru"] = ("ты", "вы"),
        ["sv"] = ("du", "ni"),
        ["tr"] = ("sen", "siz"),
        ["uk"] = ("ти", "ви"),
    };

    /// <summary>"ты" / "вы" for Russian; <c>null</c> for a language without the pair here.</summary>
    public static string? Pronoun(string language, FormOfAddress address)
    {
        ArgumentNullException.ThrowIfNull(language);
        return Pronouns.TryGetValue(language, out var pair) ? address == FormOfAddress.Informal ? pair.Informal : pair.Formal : null;
    }
}
