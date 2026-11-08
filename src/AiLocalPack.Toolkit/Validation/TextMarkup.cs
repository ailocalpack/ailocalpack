using System.Text.RegularExpressions;

namespace AiLocalPack.Toolkit.Validation;

/// <summary>Which bracket kinds an engine's markup uses for paired formatting tags.</summary>
[Flags]
public enum TagBrackets
{
    None = 0,

    /// <summary>Ren'Py text tags: <c>{b}</c> … <c>{/b}</c>, <c>{=style}</c> … <c>{/=style}</c> (<c>{{</c> is an escape).</summary>
    Braces = 1,

    /// <summary>BBCode (<c>[color=red]</c> … <c>[/color]</c>) and NGUI (<c>[ff0000]</c> … <c>[-]</c>, <c>[b]</c> … <c>[/b]</c>).</summary>
    Square = 2,

    /// <summary>Rich text (<c>&lt;b&gt;</c> … <c>&lt;/b&gt;</c>, Unreal <c>&lt;Style&gt;</c> … <c>&lt;/&gt;</c>); <c>&lt;br/&gt;</c> stands alone.</summary>
    Angle = 4,
}

/// <summary>What a markup token of a text is: an opening tag, a closing tag, or neither (a format field, an escape code).</summary>
public enum MarkupTokenKind
{
    Placeholder,
    Open,
    Close,
}

/// <param name="Value">The token as written.</param>
/// <param name="Name">The tag's name for <see cref="MarkupTokenKind.Open"/> / <see cref="MarkupTokenKind.Close"/>
/// (<see cref="TextMarkup.AnyTag"/> for a closing tag that closes whatever is open: Unreal <c>&lt;/&gt;</c>), else <c>null</c>.</param>
public sealed record MarkupToken(string Value, MarkupTokenKind Kind, string? Name);

/// <summary>
/// An engine's markup in game texts: the tokens a translation keeps as they are (escape codes, format fields, tags:
/// the recipe's <see cref="Recipes.ExtractedString.Placeholders"/>) and which of them are paired formatting tags. The
/// recipe tools give theirs (<see cref="Recipes.IRecipeTool.Markup"/>); <see cref="StringValidator"/> checks with it.
/// </summary>
public sealed class TextMarkup
{
    /// <summary>The name of a closing tag that closes the innermost open tag, whatever its name (Unreal <c>&lt;/&gt;</c>).</summary>
    public const string AnyTag = "*";

    private const string NguiColor = "#color";

    private readonly Regex? pattern;

    public TextMarkup(Regex? pattern, TagBrackets tags)
    {
        this.pattern = pattern;
        Tags = tags;
    }

    /// <summary>No markup at all (an engine the toolkit doesn't know): nothing is a placeholder.</summary>
    public static TextMarkup None { get; } = new(null, TagBrackets.None);

    public TagBrackets Tags { get; }

    /// <summary>The markup tokens of <paramref name="text"/>, in order.</summary>
    public IReadOnlyList<string> Placeholders(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return pattern is null ? [] : [.. pattern.Matches(text).Select(m => m.Value)];
    }

    /// <summary>The markup tokens of <paramref name="text"/>, in order, each classified.</summary>
    public IReadOnlyList<MarkupToken> Tokens(string text) => [.. Placeholders(text).Select(Classify)];

    /// <summary><paramref name="text"/> without its markup: what the game draws (escape codes left out).</summary>
    public string Visible(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return pattern is null ? text : pattern.Replace(text, "");
    }

    /// <summary>Classifies one token of this markup.</summary>
    public MarkupToken Classify(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var bracket = token.Length < 3 ? TagBrackets.None : (token[0], token[^1]) switch
        {
            ('{', '}') => TagBrackets.Braces,
            ('[', ']') => TagBrackets.Square,
            ('<', '>') => TagBrackets.Angle,
            _ => TagBrackets.None,
        };
        if (bracket == TagBrackets.None || (Tags & bracket) == 0)
        {
            return new MarkupToken(token, MarkupTokenKind.Placeholder, null);
        }

        var inner = token[1..^1];
        if (bracket == TagBrackets.Braces && token.StartsWith("{{", StringComparison.Ordinal))
        {
            return new MarkupToken(token, MarkupTokenKind.Placeholder, null);
        }

        if (inner == "/")
        {
            return new MarkupToken(token, MarkupTokenKind.Close, AnyTag);
        }

        if (bracket == TagBrackets.Angle && inner.EndsWith('/'))
        {
            return new MarkupToken(token, MarkupTokenKind.Placeholder, null);
        }

        if (bracket == TagBrackets.Square && inner == "-")
        {
            return new MarkupToken(token, MarkupTokenKind.Close, NguiColor);
        }

        if (bracket == TagBrackets.Square && inner.Length is 6 or 8 && inner.All(char.IsAsciiHexDigit))
        {
            return new MarkupToken(token, MarkupTokenKind.Open, NguiColor);
        }

        var closing = inner.StartsWith('/');
        var name = Name(closing ? inner[1..] : inner);
        return name.Length == 0
            ? new MarkupToken(token, MarkupTokenKind.Placeholder, null)
            : new MarkupToken(token, closing ? MarkupTokenKind.Close : MarkupTokenKind.Open, name);
    }

    /// <summary>A tag's name: Ren'Py's style tag keeps its <c>=</c> (<c>{=red}</c> ↔ <c>{/=red}</c>), others stop at a parameter.</summary>
    private static string Name(string inner)
    {
        if (inner.StartsWith('='))
        {
            return inner.Trim().ToLowerInvariant();
        }

        var end = inner.IndexOfAny(['=', ' ', ':', '\t']);
        return (end < 0 ? inner : inner[..end]).Trim().ToLowerInvariant();
    }
}
