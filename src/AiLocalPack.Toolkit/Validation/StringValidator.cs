using System.Globalization;
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
/// Checks one translation against the extracted original before it is accepted. Rules (ID, severity):
/// <c>empty</c> (error: nothing but whitespace), <c>placeholder-missing</c> (error: a placeholder of the original
/// appears fewer times in the translation), <c>too-long</c> (error: more characters, counted as text elements, than
/// the engine's limit), <c>untranslated</c> (warning: same as the original), <c>whitespace</c> (warning: leading or
/// trailing whitespace differs), <c>line-breaks</c> (warning: another number of line breaks).
/// Task 48 adds engine placeholder patterns (extra placeholders), tag balance and display width.
/// </summary>
public static class StringValidator
{
    public const string Empty = "empty";
    public const string PlaceholderMissing = "placeholder-missing";
    public const string TooLong = "too-long";
    public const string Untranslated = "untranslated";
    public const string Whitespace = "whitespace";
    public const string LineBreaks = "line-breaks";

    public static IReadOnlyList<StringIssue> Validate(ExtractedString source, string translation)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(translation);
        if (string.IsNullOrWhiteSpace(translation))
        {
            return [new StringIssue(StringIssueSeverity.Error, Empty, "The translation is empty.")];
        }

        var issues = new List<StringIssue>();
        foreach (var group in source.Placeholders.GroupBy(p => p, StringComparer.Ordinal))
        {
            var expected = group.Count();
            var found = Occurrences(translation, group.Key);
            if (found < expected)
            {
                issues.Add(new StringIssue(StringIssueSeverity.Error, PlaceholderMissing,
                    $"Placeholder {group.Key} appears {expected} time(s) in the original but {found} in the translation; keep it exactly as written."));
            }
        }

        if (source.MaxLength is { } limit && new StringInfo(translation).LengthInTextElements is var length && length > limit)
        {
            issues.Add(new StringIssue(StringIssueSeverity.Error, TooLong,
                $"The translation has {length} characters; the game allows at most {limit}."));
        }

        if (string.Equals(translation, source.Original, StringComparison.Ordinal))
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
}
