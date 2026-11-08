using System.Text;

namespace AiLocalPack.Toolkit.Recipes.Unity;

/// <summary>NGUI-style KEY,English,... tables. Edits only English cells, preserving the rest byte for byte.</summary>
internal static class UnityCsv
{
    internal sealed record Cell(string Text, int Start, int Length);

    public static Dictionary<string, Cell>? Read(string text)
    {
        var rows = Parse(text);
        if (rows is null || rows.Count == 0 || rows[0].Count < 2 || rows[0][0].Text.Trim('\uFEFF').ToUpperInvariant() is not ("KEY" or "ID"))
        {
            return null;
        }

        var header = rows[0];
        var column = header.FindIndex(c => c.Text.Equals("English", StringComparison.OrdinalIgnoreCase) || c.Text.Equals("en", StringComparison.OrdinalIgnoreCase));
        var found = new Dictionary<string, Cell>(StringComparer.Ordinal);
        if (column < 1)
        {
            return found;
        }

        foreach (var row in rows.Skip(1))
        {
            if (row.Count != header.Count || string.IsNullOrWhiteSpace(row[0].Text) || string.IsNullOrWhiteSpace(row[column].Text))
            {
                continue;
            }

            var key = "/csv/" + JsonStrings.Escape(row[0].Text);
            if (!found.TryAdd(key, row[column]))
            {
                throw new InvalidDataException($"Duplicate Unity CSV key: {row[0].Text}");
            }
        }

        return found;
    }

    public static string Replace(string text, IReadOnlyDictionary<string, string> edits, Dictionary<string, Cell> cells)
    {
        var output = new StringBuilder(text);
        foreach (var (key, cell) in cells.OrderByDescending(p => p.Value.Start))
        {
            if (edits.TryGetValue(key, out var value))
            {
                var escaped = "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
                output.Remove(cell.Start, cell.Length).Insert(cell.Start, escaped);
            }
        }

        return output.ToString();
    }

    private static List<List<Cell>>? Parse(string text)
    {
        var rows = new List<List<Cell>>();
        var row = new List<Cell>();
        var offset = text.StartsWith('\uFEFF') ? 1 : 0;
        while (offset < text.Length)
        {
            var start = offset;
            var value = new StringBuilder();
            if (text[offset] == '"')
            {
                offset++;
                var closed = false;
                while (offset < text.Length)
                {
                    var c = text[offset++];
                    if (c != '"')
                    {
                        value.Append(c);
                    }
                    else if (offset < text.Length && text[offset] == '"')
                    {
                        value.Append('"');
                        offset++;
                    }
                    else
                    {
                        closed = true;
                        break;
                    }
                }

                if (!closed || offset < text.Length && text[offset] is not (',' or '\r' or '\n'))
                {
                    return null;
                }
            }
            else
            {
                while (offset < text.Length && text[offset] is not (',' or '\r' or '\n'))
                {
                    value.Append(text[offset++]);
                }
            }

            row.Add(new Cell(value.ToString(), start, offset - start));
            if (offset < text.Length && text[offset] == ',')
            {
                offset++;
                if (offset == text.Length)
                {
                    row.Add(new Cell("", offset, 0));
                }

                continue;
            }

            rows.Add(row);
            row = [];
            if (offset < text.Length && text[offset++] == '\r' && offset < text.Length && text[offset] == '\n')
            {
                offset++;
            }
        }

        if (row.Count > 0)
        {
            rows.Add(row);
        }

        return rows;
    }
}
