using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiLocalPack.Toolkit.Recipes.Unity;

internal static partial class UnityText
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static bool IsTextField(string name) => name is "m_Text" or "mText" or "text" or "Text" or "dialogue" or "Dialogue" or "description" or "Description" or "m_Localized";

    public static IReadOnlyList<string> Placeholders(string text) => [.. Tokens().Matches(text).Select(m => m.Value)];

    [GeneratedRegex(@"</?[A-Za-z][^<>\r\n]*>|\[(?:[0-9a-fA-F]{6,8}|-|/?[bius])\]|\{[A-Za-z0-9_]+(?:,-?\d+)?(?::[^{}]+)?\}|%%|%[-+0#]*\d*(?:\.\d+)?[sdif]")]
    private static partial Regex Tokens();

    public static Dictionary<string, string> Read(string name, byte[] bytes)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        string text;
        try
        {
            text = Utf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return found;
        }

        if (text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
        {
            return found;
        }

        if (UnityCsv.Read(text) is { } csv)
        {
            return csv.ToDictionary(p => p.Key, p => p.Value.Text, StringComparer.Ordinal);
        }

        // A malformed table must never fall back to translating its whole rows.
        if (text.TrimStart('\uFEFF').StartsWith("KEY,", StringComparison.OrdinalIgnoreCase) || text.TrimStart('\uFEFF').StartsWith("ID,", StringComparison.OrdinalIgnoreCase))
        {
            return found;
        }

        if (IsJson(name, text))
        {
            try
            {
                using var document = JsonDocument.Parse(JsonStrings.WithoutBom(bytes), JsonStrings.DocumentOptions);
                Walk(document.RootElement, "", null, found);
            }
            catch (JsonException)
            {
                // A TextAsset can be arbitrary data, including a script that starts with a brace.
            }

            return found;
        }

        // Code, CSV and markup need format-specific parsers; do not offer whole source lines as game dialogue.
        if (Path.GetExtension(name) is not ("" or ".txt") || text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith('<'))
        {
            return found;
        }

        foreach (var (start, length, index) in Lines(text))
        {
            var value = text.Substring(start, length);
            if (!string.IsNullOrWhiteSpace(value))
            {
                found.Add("/line/" + index.ToString(CultureInfo.InvariantCulture), value);
            }
        }

        return found;
    }

    public static byte[] Replace(string name, byte[] bytes, IReadOnlyDictionary<string, string> edits)
    {
        var text = Utf8.GetString(bytes);
        if (UnityCsv.Read(text) is { } csv)
        {
            return Utf8.GetBytes(UnityCsv.Replace(text, edits, csv));
        }
        if (IsJson(name, text))
        {
            return JsonStrings.Replace(bytes, edits);
        }

        var result = new StringBuilder(text);
        foreach (var (start, length, index) in Lines(text).AsEnumerable().Reverse())
        {
            if (edits.TryGetValue("/line/" + index.ToString(CultureInfo.InvariantCulture), out var value))
            {
                result.Remove(start, length).Insert(start, value);
            }
        }

        return Utf8.GetBytes(result.ToString());
    }

    private static bool IsJson(string name, string text) => name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith('{') || text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith('[');

    private static void Walk(JsonElement element, string pointer, string? field, Dictionary<string, string> found)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, pointer + "/" + JsonStrings.Escape(property.Name), property.Name, found);
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var child in element.EnumerateArray())
                {
                    Walk(child, pointer + "/" + (index++).ToString(CultureInfo.InvariantCulture), field, found);
                }

                break;
            case JsonValueKind.String when field is not null && IsTextField(field) && !string.IsNullOrWhiteSpace(element.GetString()):
                found.Add(pointer, element.GetString()!);
                break;
        }
    }

    private static List<(int Start, int Length, int Index)> Lines(string text)
    {
        var lines = new List<(int, int, int)>();
        var start = text.StartsWith('\uFEFF') ? 1 : 0;
        var index = 0;
        for (var i = start; i <= text.Length; i++)
        {
            if (i != text.Length && text[i] is not ('\r' or '\n'))
            {
                continue;
            }

            lines.Add((start, i - start, index++));
            if (i + 1 < text.Length && text[i] == '\r' && text[i + 1] == '\n')
            {
                i++;
            }

            start = i + 1;
        }

        return lines;
    }
}
