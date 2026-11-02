using System.Text;

namespace AiLocalPack.Core.Steam;

/// <summary>
/// Reads text Valve KeyValues (<c>.vdf</c>, <c>.acf</c>): quoted or bare keys and values, nested <c>{ }</c> blocks,
/// <c>//</c> comments, escapes <c>\"</c> <c>\\</c> <c>\n</c> <c>\t</c> <c>\r</c> (any other backslash is kept as is,
/// so unescaped Windows paths survive). Conditionals such as <c>[$WIN32]</c> are skipped and their properties kept.
/// A document is a list of top-level properties (Steam files have one).
/// </summary>
public static class VdfParser
{
    /// <summary>Deeper nesting is rejected instead of overflowing the stack on a hostile file.</summary>
    public const int MaxDepth = 128;

    /// <exception cref="VdfParseException">The text is malformed.</exception>
    public static VdfObject Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new Reader(text).ReadDocument();
    }

    /// <summary>Reads and parses a UTF-8 file (a BOM is allowed).</summary>
    /// <exception cref="VdfParseException">The file is malformed.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static VdfObject ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    private enum TokenKind
    {
        String,
        Open,
        Close,
        Conditional,
        End,
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Line, int Column);

    private sealed class Reader(string text)
    {
        private readonly StringBuilder _buffer = new();
        private int _pos = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
        private int _line = 1;
        private int _column = 1;
        private Token? _peeked;

        public VdfObject ReadDocument()
        {
            var properties = ReadProperties(depth: 0, closing: null);
            return new VdfObject(properties);
        }

        /// <summary>Reads properties up to <c>}</c> (<paramref name="closing"/> = the matching <c>{</c>) or end of text (top level).</summary>
        private List<VdfProperty> ReadProperties(int depth, Token? closing)
        {
            var properties = new List<VdfProperty>();
            while (true)
            {
                var token = Next();
                switch (token.Kind)
                {
                    case TokenKind.End when closing is null:
                        return properties;
                    case TokenKind.End:
                        throw Error($"'{{' at line {closing.Value.Line} is never closed", token);
                    case TokenKind.Close when closing is not null:
                        SkipConditional();
                        return properties;
                    case TokenKind.Close:
                        throw Error("unexpected '}' with no matching '{'", token);
                    case TokenKind.Open:
                        throw Error("expected a key, found '{'", token);
                    case TokenKind.Conditional:
                        throw Error($"expected a key, found conditional '{token.Text}'", token);
                    case TokenKind.String:
                        properties.Add(new VdfProperty(token.Text, ReadValue(token, depth)));
                        break;
                }
            }
        }

        private VdfValue ReadValue(Token key, int depth)
        {
            SkipConditional();
            var token = Next();
            switch (token.Kind)
            {
                case TokenKind.String:
                    SkipConditional();
                    return new VdfString(token.Text);
                case TokenKind.Open:
                    if (depth + 1 > MaxDepth)
                    {
                        throw Error($"nesting deeper than {MaxDepth} levels", token);
                    }

                    return new VdfObject(ReadProperties(depth + 1, token));
                default:
                    throw Error($"key '{key.Text}' has no value", token);
            }
        }

        private void SkipConditional()
        {
            if (Peek().Kind == TokenKind.Conditional)
            {
                Next();
            }
        }

        private Token Peek() => _peeked ??= Read();

        private Token Next()
        {
            if (_peeked is { } token)
            {
                _peeked = null;
                return token;
            }

            return Read();
        }

        private Token Read()
        {
            SkipWhitespaceAndComments();
            var line = _line;
            var column = _column;
            if (_pos >= text.Length)
            {
                return new Token(TokenKind.End, "", line, column);
            }

            switch (text[_pos])
            {
                case '{':
                    Advance();
                    return new Token(TokenKind.Open, "{", line, column);
                case '}':
                    Advance();
                    return new Token(TokenKind.Close, "}", line, column);
                case '"':
                    return new Token(TokenKind.String, ReadQuoted(line, column), line, column);
                case '[':
                    return new Token(TokenKind.Conditional, ReadConditional(line, column), line, column);
                default:
                    return new Token(TokenKind.String, ReadBare(), line, column);
            }
        }

        private string ReadQuoted(int line, int column)
        {
            Advance(); // opening quote
            _buffer.Clear();
            while (_pos < text.Length)
            {
                var c = text[_pos];
                if (c == '"')
                {
                    Advance();
                    return _buffer.ToString();
                }

                if (c == '\\' && _pos + 1 < text.Length)
                {
                    var escaped = Unescape(text[_pos + 1]);
                    if (escaped is { } e)
                    {
                        _buffer.Append(e);
                        Advance();
                        Advance();
                        continue;
                    }
                }

                _buffer.Append(c);
                Advance();
            }

            throw new VdfParseException("string is not closed with '\"'", line, column);
        }

        private static char? Unescape(char c) => c switch
        {
            '"' => '"',
            '\\' => '\\',
            'n' => '\n',
            't' => '\t',
            'r' => '\r',
            _ => null,
        };

        private string ReadConditional(int line, int column)
        {
            var start = _pos;
            while (_pos < text.Length && text[_pos] != ']' && text[_pos] != '\n')
            {
                Advance();
            }

            if (_pos >= text.Length || text[_pos] != ']')
            {
                throw new VdfParseException("conditional is not closed with ']'", line, column);
            }

            Advance();
            return text[start.._pos];
        }

        private string ReadBare()
        {
            var start = _pos;
            while (_pos < text.Length && !char.IsWhiteSpace(text[_pos]) && text[_pos] is not ('{' or '}' or '"') && !AtComment())
            {
                Advance();
            }

            return text[start.._pos];
        }

        private void SkipWhitespaceAndComments()
        {
            while (_pos < text.Length)
            {
                if (char.IsWhiteSpace(text[_pos]))
                {
                    Advance();
                }
                else if (AtComment())
                {
                    while (_pos < text.Length && text[_pos] != '\n')
                    {
                        Advance();
                    }
                }
                else
                {
                    return;
                }
            }
        }

        private bool AtComment() => text[_pos] == '/' && _pos + 1 < text.Length && text[_pos + 1] == '/';

        private void Advance()
        {
            if (text[_pos] == '\n')
            {
                _line++;
                _column = 1;
            }
            else
            {
                _column++;
            }

            _pos++;
        }

        private static VdfParseException Error(string reason, Token at) => new(reason, at.Line, at.Column);
    }
}
