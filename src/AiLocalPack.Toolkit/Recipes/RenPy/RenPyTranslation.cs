using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AiLocalPack.Toolkit.Validation;

namespace AiLocalPack.Toolkit.Recipes.RenPy;

/// <summary>
/// Ren'Py's translation conventions, as in <c>renpy/translation/__init__.py</c> (6.99 – 8.x): the canonical code of a
/// say statement, the dialogue identifier (<c>&lt;label&gt;_&lt;first 8 hex of MD5 over each statement's code + CRLF&gt;</c>,
/// <c>_1</c>, <c>_2</c>… on a clash) and the string quoting of <c>.rpy</c> files.
/// </summary>
internal static partial class RenPyTranslation
{
    /// <summary><c>encode_say_string</c>: a string as a say statement writes it (quotes, backslashes, newlines, runs of spaces escaped).</summary>
    public static string EncodeSayString(string text)
    {
        var s = text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        s = SpaceAfterSpace().Replace(s, "\\ ");
        return "\"" + s + "\"";
    }

    /// <summary>
    /// <c>Say.get_code()</c>: <c>who attributes @ temporary "what" nointeract id x (arguments) with y</c>, the parts
    /// present joined by spaces. <paramref name="what"/> replaces the statement's text (a translation).
    /// </summary>
    public static string SayCode(RenPySay say, string? what = null, bool withIdentifier = true)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(say.Who))
        {
            parts.Add(say.Who);
        }

        if (say.Attributes is { } attributes)
        {
            parts.AddRange(attributes);
        }

        if (say.TemporaryAttributes is { Count: > 0 } temporary)
        {
            parts.Add("@");
            parts.AddRange(temporary);
        }

        parts.Add(EncodeSayString(what ?? say.What));
        if (!say.Interact)
        {
            parts.Add("nointeract");
        }

        if (withIdentifier && say.ExplicitIdentifier is { } identifier)
        {
            parts.Add("id");
            parts.Add(identifier);
        }

        if (!string.IsNullOrEmpty(say.Arguments))
        {
            parts.Add(say.Arguments);
        }

        if (!string.IsNullOrEmpty(say.With))
        {
            parts.Add("with");
            parts.Add(say.With);
        }

        return string.Join(' ', parts);
    }

    /// <summary>The 8 hex digits of a translation block: MD5 over each statement's code followed by CRLF, UTF-8.</summary>
    public static string Digest(IEnumerable<string> codes)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        foreach (var code in codes)
        {
            md5.AppendData(Encoding.UTF8.GetBytes(code + "\r\n"));
        }

        return Convert.ToHexStringLower(md5.GetHashAndReset())[..8];
    }

    /// <summary>
    /// The identifier: <c>&lt;label with . → _&gt;_&lt;digest&gt;</c> (just the digest before any label), with the
    /// first free <c>_1</c>, <c>_2</c>… suffix when <paramref name="taken"/> has it.
    /// </summary>
    public static string Identifier(string? label, string digest, Func<string, bool> taken)
    {
        var baseId = label is null ? digest : label.Replace('.', '_') + "_" + digest;
        var identifier = baseId;
        for (var i = 1; taken(identifier); i++)
        {
            identifier = $"{baseId}_{i}";
        }

        return identifier;
    }

    /// <summary>The <c>translate</c> language name a pack installs as: <c>ailocalpack_&lt;tag&gt;</c> (<c>-</c> → <c>_</c>), so it never clashes with the game's own translations.</summary>
    public static string LanguageName(string tag) => "ailocalpack_" + tag.Replace('-', '_');

    /// <summary>Escape codes a translation must keep: text tags <c>{b}</c>, interpolation <c>[name]</c>, their doubled escapes, <c>%%</c>, <c>%(name)s</c>.</summary>
    public static IReadOnlyList<string> Placeholders(string text) => Markup.Placeholders(text);

    /// <summary>Ren'Py's markup: text tags in braces pair up (<c>{b}</c> … <c>{/b}</c>), interpolation in square brackets doesn't.</summary>
    public static TextMarkup Markup { get; } = new(PlaceholderPattern(), TagBrackets.Braces);

    /// <summary>
    /// The texts of <c>_("…")</c> / <c>__("…")</c> calls in Python code (Ren'Py's <c>scanstrings</c> pattern), decoded
    /// as Python reads the literal (the text the game passes at run time, so the key string translation matches).
    /// </summary>
    public static IEnumerable<string> TranslatableCalls(string code)
    {
        if (!code.Contains('_', StringComparison.Ordinal))
        {
            yield break;
        }

        foreach (Match match in StringCall().Matches(code))
        {
            var text = PythonLiteral.DecodeRepr(match.Groups[1].Value);
            if (text.Trim().Length > 0)
            {
                yield return text;
            }
        }
    }

    [GeneratedRegex("(?<= ) ")]
    private static partial Regex SpaceAfterSpace();

    [GeneratedRegex(@"\{\{|\[\[|\{[^{}]*\}|\[[^\[\]]*\]|%%|%\([^)]*\)[-#0 +]*\d*(?:\.\d+)?[a-zA-Z]")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex("""
        (?<![\w])__?\s*\(\s*[uU]?(
        \"\"\"(?:\\.|\"{1,2}|[^\\"])*?\"\"\"
        |'''(?:\\.|'{1,2}|[^\\'])*?'''
        |"(?:\\.|[^\\"])*"
        |'(?:\\.|[^\\'])*'
        )\s*\)
        """, RegexOptions.IgnorePatternWhitespace | RegexOptions.Singleline)]
    private static partial Regex StringCall();
}

/// <summary>A say statement: what <see cref="RenPyTranslation.SayCode"/> needs.</summary>
internal sealed record RenPySay(
    string? Who,
    IReadOnlyList<string>? Attributes,
    IReadOnlyList<string>? TemporaryAttributes,
    string What,
    bool Interact,
    string? ExplicitIdentifier,
    string? Arguments,
    string? With);
