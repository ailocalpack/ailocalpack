using System.Text.RegularExpressions;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Toolkit.Validation;

/// <summary>How bad a <see cref="StringIssue"/> is.</summary>
public enum StringIssueSeverity
{
    /// <summary>Reported; the translation is still accepted.</summary>
    Warning,

    /// <summary>The translation is refused.</summary>
    Error,
}

/// <param name="Rule">Stable rule ID (kebab-case): <see cref="StringValidator"/> lists them.</param>
/// <param name="Message">Plain English, for the agent and the log.</param>
public sealed record StringIssue(StringIssueSeverity Severity, string Rule, string Message);

/// <summary>
/// Checks one translation against the extracted original before it is accepted, with the engine's markup
/// (<see cref="IRecipeTool.Markup"/>). Rules (ID, severity):
/// <list type="bullet">
/// <item><c>empty</c> (error): nothing but whitespace; no other rule runs.</item>
/// <item><c>placeholder-missing</c> (error): a placeholder or tag of the original appears fewer times in the translation.</item>
/// <item><c>placeholder-extra</c> (error): a markup token appears more times than in the original (an added
/// <c>%s</c> or <c>{1}</c> can crash the game, an added tag changes the look); order is free.</item>
/// <item><c>placeholder-order</c> (error): unnumbered printf fields (<c>%s</c>, <c>%d</c>, <c>%.1f</c>) in another order:
/// the game fills them by position.</item>
/// <item><c>tag-unbalanced</c> (error): the tags the original pairs (opened and closed there) are closed without being
/// opened, left open or crossed (<c>{b}{i}{/b}{/i}</c>); tags the original never closes (Ren'Py <c>{w}</c>) are free.
/// Skipped when the original itself isn't balanced (a tag spanning several strings).</item>
/// <item><c>too-long</c> (error): the drawn text (markup left out, line breaks not counted) is longer than the engine's
/// limit, in <see cref="ExtractedString.MaxLengthUnit"/>.</item>
/// <item><c>untranslated</c> (warning): same as the original, when the original has letters.</item>
/// <item><c>whitespace</c> (warning): leading or trailing whitespace differs.</item>
/// <item><c>line-breaks</c> (warning): another number of line breaks.</item>
/// </list>
/// </summary>
public static partial class StringValidator
{
    public const string Empty = "empty";
    public const string PlaceholderMissing = "placeholder-missing";
    public const string PlaceholderExtra = "placeholder-extra";
    public const string PlaceholderOrder = "placeholder-order";
    public const string TagUnbalanced = "tag-unbalanced";
    public const string TooLong = "too-long";
    public const string Untranslated = "untranslated";
    public const string Whitespace = "whitespace";
    public const string LineBreaks = "line-breaks";

    /// <summary>Every rule ID, in the order issues are reported.</summary>
    public static IReadOnlyList<string> Rules { get; } =
        [Empty, PlaceholderMissing, PlaceholderExtra, PlaceholderOrder, TagUnbalanced, TooLong, Untranslated, Whitespace, LineBreaks];

    public static IReadOnlyList<StringIssue> Validate(ExtractedString source, string translation, TextMarkup markup)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(translation);
        ArgumentNullException.ThrowIfNull(markup);
        if (string.IsNullOrWhiteSpace(translation))
        {
            return [new StringIssue(StringIssueSeverity.Error, Empty, "The translation is empty.")];
        }

        var issues = new List<StringIssue>();
        var expected = Counts(source.Placeholders);
        foreach (var (token, count) in expected.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var present = Occurrences(translation, token);
            if (present < count)
            {
                issues.Add(new StringIssue(StringIssueSeverity.Error, PlaceholderMissing,
                    $"Placeholder {token} appears {count} time(s) in the original but {present} in the translation; keep it exactly as written."));
            }
        }

        var translatedTokens = markup.Placeholders(translation);
        foreach (var (token, count) in Counts(translatedTokens).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (count > expected.GetValueOrDefault(token))
            {
                issues.Add(new StringIssue(StringIssueSeverity.Error, PlaceholderExtra,
                    $"Placeholder {token} appears {count} time(s) in the translation but {expected.GetValueOrDefault(token)} in the original; "
                    + "don't add placeholders or tags the original doesn't have."));
            }
        }

        var originalFields = PrintfFields(source.Placeholders);
        var translatedFields = PrintfFields(translatedTokens);
        if (originalFields.Distinct(StringComparer.Ordinal).Count() > 1
            && !originalFields.SequenceEqual(translatedFields, StringComparer.Ordinal)
            && originalFields.Order(StringComparer.Ordinal).SequenceEqual(translatedFields.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            issues.Add(new StringIssue(StringIssueSeverity.Error, PlaceholderOrder,
                $"The fields {string.Join(" ", originalFields)} must stay in this order (the translation has {string.Join(" ", translatedFields)}): "
                + "the game fills them by position."));
        }

        if (TagProblem(markup, source.Original, translation) is { } tagProblem)
        {
            issues.Add(new StringIssue(StringIssueSeverity.Error, TagUnbalanced, tagProblem));
        }

        if (source.MaxLength is { } limit && TextLength.Measure(markup.Visible(translation), source.MaxLengthUnit) is var length && length > limit)
        {
            issues.Add(new StringIssue(StringIssueSeverity.Error, TooLong, source.MaxLengthUnit == LengthUnit.DisplayWidth
                ? $"The translation is {length} cells wide (wide characters such as CJK and emoji count 2); the game allows at most {limit}."
                : $"The translation has {length} characters; the game allows at most {limit}."));
        }

        if (string.Equals(translation, source.Original, StringComparison.Ordinal) && markup.Visible(source.Original).Any(char.IsLetter))
        {
            issues.Add(new StringIssue(StringIssueSeverity.Warning, Untranslated, "The translation is the same as the original."));
        }

        if (Leading(translation) != Leading(source.Original) || Trailing(translation) != Trailing(source.Original))
        {
            issues.Add(new StringIssue(StringIssueSeverity.Warning, Whitespace,
                "Leading or trailing whitespace differs from the original."));
        }

        var breaks = Breaks(source.Original);
        if (Breaks(translation) is var translated && translated != breaks)
        {
            issues.Add(new StringIssue(StringIssueSeverity.Warning, LineBreaks,
                $"The original has {breaks} line break(s), the translation {translated}."));
        }

        return issues;
    }

    /// <summary>
    /// Checks a translation without its original (registry CI has no game files): only tags closed without being
    /// opened or crossed (<c>tag-unbalanced</c>), reported as warnings, since an original could leave a tag open.
    /// </summary>
    public static IReadOnlyList<StringIssue> ValidateAlone(string translation, TextMarkup markup)
    {
        ArgumentNullException.ThrowIfNull(translation);
        ArgumentNullException.ThrowIfNull(markup);
        var tokens = markup.Tokens(translation);
        var paired = PairedNames(tokens);
        return Balance(tokens, paired, reportUnclosed: false) is { } problem
            ? [new StringIssue(StringIssueSeverity.Warning, TagUnbalanced, problem)]
            : [];
    }

    /// <summary>The first tag problem of <paramref name="translation"/>, judged by the tags <paramref name="original"/> pairs.</summary>
    private static string? TagProblem(TextMarkup markup, string original, string translation)
    {
        if (markup.Tags == TagBrackets.None)
        {
            return null;
        }

        var originalTokens = markup.Tokens(original);
        var paired = PairedNames(originalTokens);
        if (paired.Count == 0 || Balance(originalTokens, paired, reportUnclosed: true) is not null)
        {
            return null;
        }

        // Tags the translation adds and pairs itself are placeholder-extra already; pairing them here words a crossing right.
        var translated = markup.Tokens(translation);
        paired.UnionWith(PairedNames(translated));
        return Balance(translated, paired, reportUnclosed: true);
    }

    /// <summary>Names of the tags a text opens and closes (a closing tag closes the innermost open one of its name).</summary>
    private static HashSet<string> PairedNames(IReadOnlyList<MarkupToken> tokens)
    {
        var paired = new HashSet<string>(StringComparer.Ordinal);
        var open = new List<MarkupToken>();
        foreach (var token in tokens)
        {
            if (token.Kind == MarkupTokenKind.Open)
            {
                open.Add(token);
            }
            else if (token.Kind == MarkupTokenKind.Close)
            {
                var at = token.Name == TextMarkup.AnyTag ? open.Count - 1 : open.FindLastIndex(o => o.Name == token.Name);
                if (at >= 0)
                {
                    paired.Add(open[at].Name!);
                    open.RemoveRange(at, open.Count - at);
                }
                else if (token.Name != TextMarkup.AnyTag)
                {
                    paired.Add(token.Name!);
                }
            }
        }

        return paired;
    }

    /// <summary>The first problem of the paired tags in <paramref name="tokens"/> (others ignored), or <c>null</c>.</summary>
    private static string? Balance(IReadOnlyList<MarkupToken> tokens, HashSet<string> paired, bool reportUnclosed)
    {
        var open = new Stack<MarkupToken>();
        foreach (var token in tokens)
        {
            if (token.Kind == MarkupTokenKind.Open && paired.Contains(token.Name!))
            {
                open.Push(token);
            }
            else if (token.Kind == MarkupTokenKind.Close)
            {
                if (open.Count == 0)
                {
                    return $"Tag {token.Value} closes nothing: no tag it closes is open before it.";
                }

                if (token.Name != TextMarkup.AnyTag && open.Peek().Name != token.Name)
                {
                    return open.Any(o => o.Name == token.Name)
                        ? $"Tag {token.Value} comes before the closing tag of {open.Peek().Value}; close tags in the reverse order they were opened."
                        : $"Tag {token.Value} closes nothing: no tag it closes is open before it.";
                }

                open.Pop();
            }
        }

        return reportUnclosed && open.Count > 0 ? $"Tag {open.Last().Value} is never closed." : null;
    }

    private static Dictionary<string, int> Counts(IEnumerable<string> tokens)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            counts[token] = counts.GetValueOrDefault(token) + 1;
        }

        return counts;
    }

    private static List<string> PrintfFields(IEnumerable<string> tokens) => [.. tokens.Where(t => PrintfField().IsMatch(t))];

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string Leading(string text) => text[..(text.Length - text.TrimStart().Length)];

    private static string Trailing(string text) => text[text.TrimEnd().Length..];

    private static int Breaks(string text) => text.Count(c => c == '\n');

    /// <summary>An unnumbered printf field: not <c>%%</c>, not <c>%1</c> (RPG Maker), not <c>%(name)s</c> (Python).</summary>
    [GeneratedRegex(@"^%[-+0# ]*\d*(?:\.\d+)?[a-zA-Z]$")]
    private static partial Regex PrintfField();
}
