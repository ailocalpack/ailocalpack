using System.Globalization;
using System.Text;

namespace AiLocalPack.Toolkit.Recipes.RenPy;

/// <summary>Python string literals, decoded the way Python reads them (never evaluated).</summary>
internal static class PythonLiteral
{
    /// <summary>
    /// A quoted literal (<c>"…"</c>, <c>'…'</c>, triple-quoted, optional <c>u</c> / <c>b</c> prefix) to its text.
    /// Escapes: <c>\\ \' \" \a \b \f \n \r \t \v</c>, octal, <c>\x</c>, <c>\u</c>, <c>\U</c>, line continuation; an
    /// unknown escape keeps its backslash, as in Python.
    /// </summary>
    public static string DecodeRepr(string literal)
    {
        var s = literal.Trim();
        var raw = false;
        while (s.Length > 0 && s[0] is 'u' or 'U' or 'b' or 'B' or 'r' or 'R')
        {
            raw |= s[0] is 'r' or 'R';
            s = s[1..];
        }

        var quote = s.StartsWith("\"\"\"", StringComparison.Ordinal) || s.StartsWith("'''", StringComparison.Ordinal) ? 3 : 1;
        if (s.Length < quote * 2)
        {
            return s;
        }

        var body = s[quote..^quote];
        return raw ? body : Unescape(body);
    }

    public static string Unescape(string body)
    {
        var result = new StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c != '\\' || i + 1 >= body.Length)
            {
                result.Append(c);
                continue;
            }

            var next = body[++i];
            switch (next)
            {
                case '\n':
                    break;
                case '\\' or '\'' or '"':
                    result.Append(next);
                    break;
                case 'a':
                    result.Append('\a');
                    break;
                case 'b':
                    result.Append('\b');
                    break;
                case 'f':
                    result.Append('\f');
                    break;
                case 'n':
                    result.Append('\n');
                    break;
                case 'r':
                    result.Append('\r');
                    break;
                case 't':
                    result.Append('\t');
                    break;
                case 'v':
                    result.Append('\v');
                    break;
                case >= '0' and <= '7':
                {
                    var end = i;
                    while (end < body.Length && end - i < 3 && body[end] is >= '0' and <= '7')
                    {
                        end++;
                    }

                    result.Append((char)Convert.ToInt32(body[i..end], 8));
                    i = end - 1;
                    break;
                }

                case 'x' when Hex(body, i + 1, 2) is { } x:
                    result.Append((char)x);
                    i += 2;
                    break;
                case 'u' when Hex(body, i + 1, 4) is { } u:
                    result.Append((char)u);
                    i += 4;
                    break;
                case 'U' when Hex(body, i + 1, 8) is { } big && big <= 0x10FFFF:
                    result.Append(char.ConvertFromUtf32(big));
                    i += 8;
                    break;
                default:
                    result.Append('\\').Append(next);
                    break;
            }
        }

        return result.ToString();
    }

    /// <summary>Python's <c>raw-unicode-escape</c> (pickle protocol 0 <c>UNICODE</c>): only <c>\uXXXX</c> and <c>\UXXXXXXXX</c> are escapes.</summary>
    public static string DecodeRawUnicodeEscape(string text)
    {
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == 'u' && Hex(text, i + 2, 4) is { } u)
            {
                result.Append((char)u);
                i += 5;
            }
            else if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == 'U' && Hex(text, i + 2, 8) is { } big && big <= 0x10FFFF)
            {
                result.Append(char.ConvertFromUtf32(big));
                i += 9;
            }
            else
            {
                result.Append(text[i]);
            }
        }

        return result.ToString();
    }

    private static int? Hex(string text, int start, int length) =>
        start + length <= text.Length && int.TryParse(text.AsSpan(start, length), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
