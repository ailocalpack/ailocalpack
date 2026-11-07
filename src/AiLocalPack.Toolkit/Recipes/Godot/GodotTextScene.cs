using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AiLocalPack.Toolkit.Recipes.Godot;

/// <summary>
/// The texts of a Godot text scene (<c>.tscn</c>, Godot 3 and 4): string literals of the visible text properties
/// (<see cref="Properties"/>) of <c>[node]</c> sections, read with their place in the file so <see cref="Replace"/>
/// changes only those literals. Other values (arrays, dictionaries, <c>&amp;"…"</c> / <c>^"…"</c> names, numbers),
/// properties and sections are skipped. Strings are read as Godot's parser reads them (escapes <c>\n \t \r \b \f \" \\
/// \' \uXXXX \UXXXXXX</c>; real line breaks kept) and written as its writer does (only <c>\</c> and <c>"</c> escaped).
/// </summary>
public static partial class GodotTextScene
{
    /// <summary>Node properties holding text the player sees (Godot 3 and 4 names) and how a translator should read them.</summary>
    public static IReadOnlyDictionary<string, string> Properties { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["text"] = "Text",
        ["bbcode_text"] = "Rich text",
        ["placeholder_text"] = "Placeholder",
        ["hint_tooltip"] = "Tooltip",
        ["tooltip_text"] = "Tooltip",
        ["window_title"] = "Title",
        ["title"] = "Title",
        ["dialog_text"] = "Dialog text",
    };

    /// <summary>The visible texts of the scene, in file order (only those with a letter in them).</summary>
    public static IReadOnlyList<SceneText> Read(string scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var texts = new List<SceneText>();
        string? node = null;
        string? type = null;
        var at = 0;
        while (at < scene.Length)
        {
            var lineEnd = scene.IndexOf('\n', at);
            if (lineEnd < 0)
            {
                lineEnd = scene.Length;
            }

            if (scene[at] == '[')
            {
                var end = SkipValue(scene, at);
                (node, type) = Section(scene[at..end]);
                at = NextLine(scene, end);
                continue;
            }

            var match = PropertyPattern().Match(scene, at, lineEnd - at);
            if (!match.Success)
            {
                at = lineEnd + 1;
                continue;
            }

            var valueStart = match.Index + match.Length;
            var valueEnd = SkipValue(scene, valueStart);
            if (node is not null && Properties.ContainsKey(match.Groups[1].Value) && scene[valueStart] == '"')
            {
                var (value, literalEnd) = ReadString(scene, valueStart);
                if (literalEnd == valueEnd && value.Any(char.IsLetter))
                {
                    texts.Add(new SceneText(node, type, match.Groups[1].Value, value, valueStart, literalEnd - valueStart));
                }
            }

            at = NextLine(scene, valueEnd);
        }

        return texts;
    }

    /// <summary>The scene with each replaced text's literal written anew; everything else as it was.</summary>
    public static string Replace(string scene, IEnumerable<(SceneText Text, string Value)> replacements)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(replacements);
        var builder = new StringBuilder(scene.Length);
        var at = 0;
        foreach (var (text, value) in replacements.OrderBy(r => r.Text.Start))
        {
            builder.Append(scene, at, text.Start - at).Append(Quote(value));
            at = text.Start + text.Length;
        }

        return builder.Append(scene, at, scene.Length - at).ToString();
    }

    /// <summary>A string literal as Godot writes one: <c>\</c> and <c>"</c> escaped, line breaks kept.</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>Node path of a <c>[node]</c> header (<c>.</c> for the root) and its type, or <c>null</c> for other sections.</summary>
    private static (string? Node, string? Type) Section(string header)
    {
        var kind = SectionKindPattern().Match(header);
        if (!kind.Success || kind.Groups[1].Value != "node")
        {
            return (null, null);
        }

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match attribute in AttributePattern().Matches(header))
        {
            var start = attribute.Index + attribute.Length - 1;
            attributes.TryAdd(attribute.Groups[1].Value, ReadString(header, start).Value);
        }

        if (!attributes.TryGetValue("name", out var name))
        {
            return (null, null);
        }

        attributes.TryGetValue("type", out var type);
        var path = attributes.TryGetValue("parent", out var parent)
            ? parent == "." ? name : $"{parent}/{name}"
            : ".";
        return (path, type);
    }

    /// <summary>A string literal starting at the quote at <paramref name="start"/>: its text and the index after the closing quote.</summary>
    private static (string Value, int End) ReadString(string text, int start)
    {
        var builder = new StringBuilder();
        var i = start + 1;
        while (i < text.Length && text[i] != '"')
        {
            if (text[i] != '\\' || i + 1 >= text.Length)
            {
                builder.Append(text[i++]);
                continue;
            }

            var escape = text[i + 1];
            i += 2;
            switch (escape)
            {
                case 'n': builder.Append('\n'); break;
                case 't': builder.Append('\t'); break;
                case 'r': builder.Append('\r'); break;
                case 'b': builder.Append('\b'); break;
                case 'f': builder.Append('\f'); break;
                case 'u' or 'U':
                    var digits = escape == 'u' ? 4 : 6;
                    if (i + digits <= text.Length && int.TryParse(text.AsSpan(i, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code) && code <= 0x10FFFF)
                    {
                        builder.Append(char.ConvertFromUtf32(code is >= 0xD800 and <= 0xDFFF ? 0xFFFD : code));
                        i += digits;
                    }
                    else
                    {
                        builder.Append(escape);
                    }

                    break;
                default: builder.Append(escape); break;
            }
        }

        return (builder.ToString(), Math.Min(i + 1, text.Length));
    }

    /// <summary>Index after the value starting at <paramref name="start"/>: brackets balanced, strings skipped, up to the line's end.</summary>
    private static int SkipValue(string text, int start)
    {
        var depth = 0;
        var i = start;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"')
            {
                i = ReadString(text, i).End;
                continue;
            }

            if (c is '[' or '{' or '(')
            {
                depth++;
            }
            else if (c is ']' or '}' or ')')
            {
                depth--;
                if (depth <= 0 && text[start] == '[')
                {
                    return i + 1;
                }
            }
            else if (c == '\n' && depth <= 0)
            {
                return i;
            }

            i++;
        }

        return i;
    }

    private static int NextLine(string text, int at)
    {
        var lineEnd = text.IndexOf('\n', at);
        return lineEnd < 0 ? text.Length : lineEnd + 1;
    }

    [GeneratedRegex(@"\G([A-Za-z_][A-Za-z0-9_/]*) = ")]
    private static partial Regex PropertyPattern();

    [GeneratedRegex(@"^\[([a-z_]+)")]
    private static partial Regex SectionKindPattern();

    [GeneratedRegex(@"\b([a-z_]+)=""")]
    private static partial Regex AttributePattern();
}

/// <summary>A text of a <see cref="GodotTextScene"/>.</summary>
/// <param name="Node">Node path from the scene's root (<c>.</c> = the root, <c>Menu/Play</c>).</param>
/// <param name="Type">The node's class, or <c>null</c> for an instanced scene.</param>
/// <param name="Start">Index of the literal's opening quote in the file's text.</param>
/// <param name="Length">Length of the literal, quotes included.</param>
public sealed record SceneText(string Node, string? Type, string Property, string Value, int Start, int Length);
