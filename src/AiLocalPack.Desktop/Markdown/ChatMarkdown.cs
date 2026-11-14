using System.Text;
using System.Text.RegularExpressions;

namespace AiLocalPack.Desktop.Markdown;

/// <summary>A block of a chat message in Markdown.</summary>
public abstract record MdBlock;

public sealed record MdParagraph(IReadOnlyList<MdInline> Inlines) : MdBlock;

/// <summary>An ATX heading (<c>#</c> … <c>######</c>).</summary>
public sealed record MdHeading(int Level, IReadOnlyList<MdInline> Inlines) : MdBlock;

/// <summary>A fenced (or indented) code block; <see cref="Language"/> is the fence's info word, or empty.</summary>
public sealed record MdCodeBlock(string Language, string Text) : MdBlock;

public sealed record MdQuote(IReadOnlyList<MdBlock> Blocks) : MdBlock;

/// <summary>A bullet or numbered list; <see cref="Start"/> is the first number of a numbered one.</summary>
public sealed record MdList(bool Ordered, int Start, IReadOnlyList<IReadOnlyList<MdBlock>> Items) : MdBlock;

public sealed record MdRule : MdBlock;

public enum MdAlign
{
    Left,
    Center,
    Right,
}

/// <summary>A GitHub-style table: the header cells, each column's alignment and the body rows (as many cells as the header).</summary>
public sealed record MdTable(IReadOnlyList<IReadOnlyList<MdInline>> Header, IReadOnlyList<MdAlign> Align, IReadOnlyList<IReadOnlyList<IReadOnlyList<MdInline>>> Rows) : MdBlock;

/// <summary>A piece of text inside a block.</summary>
public abstract record MdInline;

public sealed record MdText(string Text) : MdInline;

public sealed record MdStrong(IReadOnlyList<MdInline> Inlines) : MdInline;

public sealed record MdEmphasis(IReadOnlyList<MdInline> Inlines) : MdInline;

public sealed record MdStrike(IReadOnlyList<MdInline> Inlines) : MdInline;

public sealed record MdCode(string Text) : MdInline;

/// <summary>A link; <see cref="Url"/> is <c>null</c> when the target isn't an http(s) or mailto address (shown as text, never opened).</summary>
public sealed record MdLink(IReadOnlyList<MdInline> Inlines, Uri? Url) : MdInline;

public sealed record MdLineBreak : MdInline;

/// <summary>
/// The Markdown the agents write in chat replies (CommonMark's blocks and GitHub's tables and strikethrough, simplified):
/// headings, paragraphs (every line break kept, as in a terminal), bullet and numbered lists (nested by indent), fenced code,
/// quotes, rules, tables; bold, italic, strikethrough, inline code, links and bare URLs. Raw HTML stays text. Pure and
/// lenient: any input parses; an unclosed fence (a reply cut short) runs to the end.
/// </summary>
public static partial class ChatMarkdown
{
    public static IReadOnlyList<MdBlock> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')
            .Select(l => l.Replace("\t", "    ", StringComparison.Ordinal)).ToList();
        return Blocks(lines);
    }

    /// <summary>The message without its Markdown: what a screen reader or a copy of the whole reply gets.</summary>
    public static string PlainText(IReadOnlyList<MdBlock> blocks)
    {
        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            Plain(block, sb, string.Empty);
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static void Plain(MdBlock block, StringBuilder sb, string indent)
    {
        switch (block)
        {
            case MdParagraph p:
                sb.Append(indent).Append(PlainText(p.Inlines).Replace("\n", "\n" + indent, StringComparison.Ordinal)).Append('\n');
                break;
            case MdHeading h:
                sb.Append(indent).Append(PlainText(h.Inlines)).Append('\n');
                break;
            case MdCodeBlock c:
                sb.Append(c.Text).Append('\n');
                break;
            case MdQuote q:
                foreach (var b in q.Blocks)
                {
                    Plain(b, sb, indent);
                }

                break;
            case MdList l:
                for (var i = 0; i < l.Items.Count; i++)
                {
                    var marker = l.Ordered ? $"{l.Start + i}. " : "• ";
                    var item = new StringBuilder();
                    foreach (var b in l.Items[i])
                    {
                        Plain(b, item, string.Empty);
                    }

                    var itemLines = item.ToString().TrimEnd('\n').Split('\n');
                    sb.Append(indent).Append(marker).Append(itemLines[0]).Append('\n');
                    foreach (var line in itemLines.Skip(1))
                    {
                        sb.Append(indent).Append(new string(' ', marker.Length)).Append(line).Append('\n');
                    }
                }

                break;
            case MdTable t:
                sb.Append(indent).AppendJoin(" | ", t.Header.Select(PlainText)).Append('\n');
                foreach (var row in t.Rows)
                {
                    sb.Append(indent).AppendJoin(" | ", row.Select(PlainText)).Append('\n');
                }

                break;
        }
    }

    public static string PlainText(IReadOnlyList<MdInline> inlines)
    {
        var sb = new StringBuilder();
        foreach (var inline in inlines)
        {
            sb.Append(inline switch
            {
                MdText t => t.Text,
                MdCode c => c.Text,
                MdLineBreak => "\n",
                MdStrong s => PlainText(s.Inlines),
                MdEmphasis e => PlainText(e.Inlines),
                MdStrike s => PlainText(s.Inlines),
                MdLink l => PlainText(l.Inlines),
                _ => string.Empty,
            });
        }

        return sb.ToString();
    }

    // ---- Blocks ----

    [GeneratedRegex(@"^ {0,3}(#{1,6})(?:[ ]+(.*?))?(?:[ ]+#+)?[ ]*$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^ {0,3}([-*_])(?:[ ]*\1){2,}[ ]*$")]
    private static partial Regex RuleRegex();

    [GeneratedRegex(@"^( {0,3})(`{3,}|~{3,})[ ]*([^`\s]*)")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"^( *)([-*+]|(\d{1,9})[.)])(?:( +)(.*)|$)")]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"^ *\|? *:?-+:? *(\| *:?-+:? *)*\|? *$")]
    private static partial Regex TableDelimiterRegex();

    private static List<MdBlock> Blocks(List<string> lines)
    {
        var blocks = new List<MdBlock>();
        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            if (IsBlank(line))
            {
                i++;
            }
            else if (FenceRegex().Match(line) is { Success: true } fence)
            {
                i = Fenced(lines, i, fence, blocks);
            }
            else if (HeadingRegex().Match(line) is { Success: true } heading)
            {
                blocks.Add(new MdHeading(heading.Groups[1].Length, Inlines(heading.Groups[2].Value.Trim())));
                i++;
            }
            else if (RuleRegex().IsMatch(line))
            {
                blocks.Add(new MdRule());
                i++;
            }
            else if (IsQuote(line))
            {
                var inner = new List<string>();
                while (i < lines.Count && IsQuote(lines[i]))
                {
                    var stripped = lines[i].TrimStart()[1..];
                    inner.Add(stripped.StartsWith(' ') ? stripped[1..] : stripped);
                    i++;
                }

                blocks.Add(new MdQuote(Blocks(inner)));
            }
            else if (ListItemRegex().Match(line) is { Success: true } item && Indent(line) <= 3)
            {
                i = List(lines, i, item, blocks);
            }
            else if (IsTableStart(lines, i))
            {
                i = Table(lines, i, blocks);
            }
            else if (Indent(line) >= 4)
            {
                var code = new List<string>();
                while (i < lines.Count && (Indent(lines[i]) >= 4 || IsBlank(lines[i])))
                {
                    code.Add(lines[i].Length >= 4 ? lines[i][4..] : string.Empty);
                    i++;
                }

                while (code.Count > 0 && IsBlank(code[^1]))
                {
                    code.RemoveAt(code.Count - 1);
                }

                blocks.Add(new MdCodeBlock(string.Empty, string.Join('\n', code)));
            }
            else
            {
                var paragraph = new List<string> { line.Trim() };
                i++;
                while (i < lines.Count && !IsBlank(lines[i]) && !Interrupts(lines, i))
                {
                    paragraph.Add(lines[i].Trim());
                    i++;
                }

                blocks.Add(new MdParagraph(Inlines(string.Join('\n', paragraph))));
            }
        }

        return blocks;
    }

    private static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);

    private static int Indent(string line) => line.Length - line.TrimStart(' ').Length;

    private static bool IsQuote(string line) => Indent(line) <= 3 && line.TrimStart().StartsWith('>');

    /// <summary>Whether line <paramref name="i"/> starts another block and so ends a paragraph (a list item does: agents write "Steps:" then "- a" with no blank line).</summary>
    private static bool Interrupts(List<string> lines, int i)
    {
        var line = lines[i];
        return FenceRegex().IsMatch(line) || HeadingRegex().IsMatch(line) || RuleRegex().IsMatch(line) || IsQuote(line)
            || (Indent(line) <= 3 && ListItemRegex().Match(line) is { Success: true } m && m.Groups[5].Value.Length > 0)
            || IsTableStart(lines, i);
    }

    private static int Fenced(List<string> lines, int i, Match fence, List<MdBlock> blocks)
    {
        var indent = fence.Groups[1].Length;
        var marker = fence.Groups[2].Value;
        var code = new List<string>();
        i++;
        while (i < lines.Count)
        {
            var trimmed = lines[i].TrimStart(' ');
            if (Indent(lines[i]) <= 3 && trimmed.StartsWith(marker, StringComparison.Ordinal) && trimmed.TrimEnd().All(c => c == marker[0]))
            {
                i++;
                break;
            }

            var strip = Math.Min(indent, Indent(lines[i]));
            code.Add(lines[i][strip..]);
            i++;
        }

        blocks.Add(new MdCodeBlock(fence.Groups[3].Value, string.Join('\n', code)));
        return i;
    }

    private static int List(List<string> lines, int i, Match first, List<MdBlock> blocks)
    {
        var ordered = first.Groups[3].Success;
        var start = ordered && int.TryParse(first.Groups[3].Value, out var n) ? n : 1;
        var markerIndent = first.Groups[1].Length;
        var items = new List<IReadOnlyList<MdBlock>>();
        var match = first;
        while (true)
        {
            var spaces = match.Groups[4].Length;
            var contentIndent = markerIndent + match.Groups[2].Length + (spaces is 0 or > 4 ? 1 : spaces);
            var content = new List<string> { spaces > 4 ? new string(' ', spaces - 1) + match.Groups[5].Value : match.Groups[5].Value };
            i++;
            var lastBlank = false;
            while (i < lines.Count)
            {
                var line = lines[i];
                if (IsBlank(line))
                {
                    lastBlank = true;
                    content.Add(string.Empty);
                    i++;
                    continue;
                }

                var indent = Indent(line);
                if (indent >= contentIndent)
                {
                    content.Add(line[contentIndent..]);
                }
                else if (ListItemRegex().Match(line) is { Success: true } && indent > markerIndent)
                {
                    // a nested list indented less than this item's text ("1. a" then "  - b")
                    content.Add(line[indent..]);
                }
                else if (!lastBlank && !Interrupts(lines, i) && ListItemRegex().Match(line) is not { Success: true })
                {
                    // lazy continuation of the item's paragraph
                    content.Add(line.Trim());
                }
                else
                {
                    break;
                }

                lastBlank = false;
                i++;
            }

            items.Add(Blocks(content));
            if (i >= lines.Count || ListItemRegex().Match(lines[i]) is not { Success: true } next
                || Indent(lines[i]) > 3 || next.Groups[3].Success != ordered)
            {
                break;
            }

            markerIndent = next.Groups[1].Length;
            match = next;
        }

        blocks.Add(new MdList(ordered, start, items));
        return i;
    }

    private static bool IsTableStart(List<string> lines, int i) =>
        i + 1 < lines.Count && Indent(lines[i]) <= 3 && lines[i].Contains('|', StringComparison.Ordinal)
        && TableDelimiterRegex().IsMatch(lines[i + 1]) && lines[i + 1].Contains('-', StringComparison.Ordinal)
        && (lines[i + 1].Contains('|', StringComparison.Ordinal) || Cells(lines[i]).Count == 1);

    private static int Table(List<string> lines, int i, List<MdBlock> blocks)
    {
        var header = Cells(lines[i]);
        var align = Cells(lines[i + 1]).Select(c => c.Trim()).Select(c => (c.StartsWith(':'), c.EndsWith(':')) switch
        {
            (true, true) => MdAlign.Center,
            (false, true) => MdAlign.Right,
            _ => MdAlign.Left,
        }).ToList();
        while (align.Count < header.Count)
        {
            align.Add(MdAlign.Left);
        }

        i += 2;
        var rows = new List<IReadOnlyList<IReadOnlyList<MdInline>>>();
        while (i < lines.Count && !IsBlank(lines[i]) && lines[i].Contains('|', StringComparison.Ordinal))
        {
            var cells = Cells(lines[i]);
            rows.Add([.. Enumerable.Range(0, header.Count).Select(c => Inlines(c < cells.Count ? cells[c].Trim() : string.Empty))]);
            i++;
        }

        blocks.Add(new MdTable([.. header.Select(h => Inlines(h.Trim()))], align[..header.Count], rows));
        return i;
    }

    /// <summary>A table row's cells: split at <c>|</c> outside code spans and not escaped, without the outer pipes.</summary>
    private static List<string> Cells(string line)
    {
        var text = line.Trim();
        if (text.StartsWith('|'))
        {
            text = text[1..];
        }

        if (text.EndsWith('|') && !text.EndsWith("\\|", StringComparison.Ordinal))
        {
            text = text[..^1];
        }

        var cells = new List<string>();
        var cell = new StringBuilder();
        var inCode = false;
        for (var k = 0; k < text.Length; k++)
        {
            var c = text[k];
            if (c == '\\' && k + 1 < text.Length && text[k + 1] == '|')
            {
                cell.Append('|');
                k++;
            }
            else if (c == '`')
            {
                inCode = !inCode;
                cell.Append(c);
            }
            else if (c == '|' && !inCode)
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }

        cells.Add(cell.ToString());
        return cells;
    }

    // ---- Inlines ----

    public static IReadOnlyList<MdInline> Inlines(string text)
    {
        var result = new List<MdInline>();
        InlinesInto(text, result);
        return Merge(result);
    }

    private static List<MdInline> Merge(List<MdInline> inlines)
    {
        var merged = new List<MdInline>();
        foreach (var inline in inlines)
        {
            if (inline is MdText t && merged.Count > 0 && merged[^1] is MdText last)
            {
                merged[^1] = new MdText(last.Text + t.Text);
            }
            else if (inline is not MdText { Text.Length: 0 })
            {
                merged.Add(inline);
            }
        }

        return merged;
    }

    private const string Punctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    private static void InlinesInto(string s, List<MdInline> output)
    {
        var text = new StringBuilder();
        void Flush()
        {
            if (text.Length > 0)
            {
                output.Add(new MdText(text.ToString()));
                text.Clear();
            }
        }

        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length && Punctuation.Contains(s[i + 1], StringComparison.Ordinal))
            {
                text.Append(s[i + 1]);
                i += 2;
            }
            else if (c == '\n')
            {
                Flush();
                output.Add(new MdLineBreak());
                i++;
            }
            else if (c == '`' && CodeSpan(s, i) is { } code)
            {
                Flush();
                output.Add(new MdCode(code.Text));
                i = code.End;
            }
            else if ((c == '*' || c == '_' || c == '~') && Delimited(s, i) is { } span)
            {
                Flush();
                output.Add(span.Inline);
                i = span.End;
            }
            else if ((c == '[' || (c == '!' && i + 1 < s.Length && s[i + 1] == '[')) && Link(s, c == '!' ? i + 1 : i) is { } link)
            {
                Flush();
                output.Add(link.Inline);
                i = link.End;
            }
            else if (c == '<' && AutoLink(s, i) is { } auto)
            {
                Flush();
                output.Add(auto.Inline);
                i = auto.End;
            }
            else if ((c == 'h' || c == 'H') && (i == 0 || !char.IsLetterOrDigit(s[i - 1])) && BareUrl(s, i) is { } bare)
            {
                Flush();
                output.Add(bare.Inline);
                i = bare.End;
            }
            else
            {
                text.Append(c);
                i++;
            }
        }

        Flush();
    }

    private static int RunLength(string s, int i)
    {
        var n = 0;
        while (i + n < s.Length && s[i + n] == s[i])
        {
            n++;
        }

        return n;
    }

    private static (string Text, int End)? CodeSpan(string s, int i)
    {
        var n = RunLength(s, i);
        var k = i + n;
        while (k < s.Length)
        {
            var at = s.IndexOf('`', k);
            if (at < 0)
            {
                return null;
            }

            var m = RunLength(s, at);
            if (m == n)
            {
                var code = s[(i + n)..at].Replace('\n', ' ');
                if (code.Length >= 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0)
                {
                    code = code[1..^1];
                }

                return (code, at + m);
            }

            k = at + m;
        }

        return null;
    }

    /// <summary>Bold (<c>**</c>, <c>__</c>), italic (<c>*</c>, <c>_</c>), both (<c>***</c>) or strikethrough (<c>~~</c>) starting at <paramref name="i"/>, closed by a run of the same length.</summary>
    private static (MdInline Inline, int End)? Delimited(string s, int i)
    {
        var c = s[i];
        var n = RunLength(s, i);
        if (c == '~' && n != 2)
        {
            return null;
        }

        if (n > 3 || i + n >= s.Length || char.IsWhiteSpace(s[i + n]))
        {
            return null;
        }

        // an underscore inside a word (snake_case) is text
        if (c == '_' && i > 0 && char.IsLetterOrDigit(s[i - 1]))
        {
            return null;
        }

        var k = i + n;
        while (k < s.Length)
        {
            var ch = s[k];
            if (ch == '\\')
            {
                k += 2;
                continue;
            }

            if (ch == '`' && CodeSpan(s, k) is { } code)
            {
                k = code.End;
                continue;
            }

            if (ch != c)
            {
                k++;
                continue;
            }

            var m = RunLength(s, k);
            var closes = m == n && !char.IsWhiteSpace(s[k - 1]) && (c != '_' || k + m >= s.Length || !char.IsLetterOrDigit(s[k + m]));
            if (closes && k > i + n)
            {
                var inner = new List<MdInline>();
                InlinesInto(s[(i + n)..k], inner);
                IReadOnlyList<MdInline> children = Merge(inner);
                MdInline inline = (c, n) switch
                {
                    ('~', _) => new MdStrike(children),
                    (_, 1) => new MdEmphasis(children),
                    (_, 2) => new MdStrong(children),
                    _ => new MdStrong([new MdEmphasis(children)]),
                };
                return (inline, k + m);
            }

            k += m;
        }

        return null;
    }

    private static (MdInline Inline, int End)? Link(string s, int i)
    {
        var depth = 0;
        var k = i;
        for (; k < s.Length; k++)
        {
            if (s[k] == '\\')
            {
                k++;
            }
            else if (s[k] == '[')
            {
                depth++;
            }
            else if (s[k] == ']' && --depth == 0)
            {
                break;
            }
        }

        if (k >= s.Length - 1 || s[k + 1] != '(')
        {
            return null;
        }

        var close = k + 2;
        var parens = 0;
        for (; close < s.Length; close++)
        {
            if (s[close] == '(')
            {
                parens++;
            }
            else if (s[close] == ')' && parens-- == 0)
            {
                break;
            }
            else if (s[close] == '\n')
            {
                return null;
            }
        }

        if (close >= s.Length)
        {
            return null;
        }

        var target = s[(k + 2)..close].Trim();
        var space = target.IndexOf(' ', StringComparison.Ordinal);
        if (space > 0)
        {
            target = target[..space];
        }

        target = target.TrimStart('<').TrimEnd('>');
        var label = new List<MdInline>();
        InlinesInto(s[(i + 1)..k], label);
        return (new MdLink(Merge(label), SafeUrl(target)), close + 1);
    }

    private static (MdInline Inline, int End)? AutoLink(string s, int i)
    {
        var end = s.IndexOf('>', i);
        if (end < 0)
        {
            return null;
        }

        var target = s[(i + 1)..end];
        return target.Contains(' ', StringComparison.Ordinal) || SafeUrl(target) is not { } url
            ? null
            : (new MdLink([new MdText(target)], url), end + 1);
    }

    private static (MdInline Inline, int End)? BareUrl(string s, int i)
    {
        if (!s.AsSpan(i).StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !s.AsSpan(i).StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var end = i;
        while (end < s.Length && !char.IsWhiteSpace(s[end]) && s[end] != '<' && s[end] != '`')
        {
            end++;
        }

        // trailing punctuation belongs to the sentence; a closing paren only when it has no opening one in the URL
        while (end > i)
        {
            var last = s[end - 1];
            if (".,;:!?*_~'\"".Contains(last, StringComparison.Ordinal)
                || (last == ')' && s[i..end].Count(ch => ch == '(') < s[i..end].Count(ch => ch == ')')))
            {
                end--;
            }
            else
            {
                break;
            }
        }

        var target = s[i..end];
        return SafeUrl(target) is { } url && url.Host.Length > 0 ? (new MdLink([new MdText(target)], url), end) : null;
    }

    /// <summary>Only web and mail links open; anything else (<c>file:</c>, <c>javascript:</c>, a relative path) stays text.</summary>
    private static Uri? SafeUrl(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto)
            ? uri
            : null;
}
