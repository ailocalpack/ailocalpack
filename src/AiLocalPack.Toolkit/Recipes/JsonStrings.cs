using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AiLocalPack.Toolkit.Recipes;

/// <summary>
/// JSON helpers for recipe tools: JSON Pointers (RFC 6901) and replacing string values in place, so a rebuilt file
/// differs from the original only in the replaced strings (formatting, number spelling and key order kept).
/// </summary>
public static class JsonStrings
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private static readonly JsonReaderOptions ReaderOptions = new() { MaxDepth = 512 };

    /// <summary>Options for <see cref="JsonDocument.Parse(ReadOnlyMemory{byte}, JsonDocumentOptions)"/> matching the rewriter.</summary>
    public static JsonDocumentOptions DocumentOptions { get; } = new() { MaxDepth = 512 };

    /// <summary>One pointer segment: <c>~</c> → <c>~0</c>, <c>/</c> → <c>~1</c>.</summary>
    public static string Escape(string segment) => segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    /// <summary>The bytes after a UTF-8 byte order mark, if there is one.</summary>
    public static ReadOnlyMemory<byte> WithoutBom(ReadOnlyMemory<byte> json) => json.Span.StartsWith(Bom) ? json[Bom.Length..] : json;

    /// <summary>
    /// Replaces the string values at the given pointers (pointer → new text). Pointers that don't name a string are
    /// ignored. A UTF-8 byte order mark is kept.
    /// </summary>
    /// <exception cref="JsonException">The bytes are not valid JSON.</exception>
    public static byte[] Replace(ReadOnlySpan<byte> json, IReadOnlyDictionary<string, string> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        var offset = json.StartsWith(Bom) ? Bom.Length : 0;
        var body = json[offset..];
        var edits = new List<(int Start, int Length, string Text)>();
        var reader = new Utf8JsonReader(body, ReaderOptions);
        var frames = new List<Frame>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    frames[^1] = frames[^1] with { Segment = Escape(reader.GetString()!) };
                    break;
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    BeforeValue(frames);
                    frames.Add(new Frame(reader.TokenType == JsonTokenType.StartArray, 0, ""));
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    frames.RemoveAt(frames.Count - 1);
                    AfterValue(frames);
                    break;
                case JsonTokenType.String:
                    BeforeValue(frames);
                    if (replacements.Count > edits.Count && replacements.TryGetValue(Pointer(frames), out var text))
                    {
                        // ValueSpan is the raw (still escaped) text between the quotes.
                        edits.Add(((int)reader.TokenStartIndex, reader.ValueSpan.Length + 2, text));
                    }

                    AfterValue(frames);
                    break;
                default:
                    BeforeValue(frames);
                    AfterValue(frames);
                    break;
            }
        }

        var output = new MemoryStream(json.Length + 256);
        output.Write(json[..offset]);
        var position = 0;
        foreach (var (start, length, text) in edits)
        {
            output.Write(body[position..start]);
            output.Write(Encode(text));
            position = start + length;
        }

        output.Write(body[position..]);
        return output.ToArray();
    }

    /// <summary>The text as a JSON string literal, quotes included; non-ASCII stays as UTF-8, like the editors write it.</summary>
    private static byte[] Encode(string text)
    {
        var encoded = JsonEncodedText.Encode(text, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).EncodedUtf8Bytes;
        var result = new byte[encoded.Length + 2];
        result[0] = (byte)'"';
        encoded.CopyTo(result.AsSpan(1));
        result[^1] = (byte)'"';
        return result;
    }

    private static void BeforeValue(List<Frame> frames)
    {
        if (frames.Count > 0 && frames[^1].IsArray)
        {
            frames[^1] = frames[^1] with { Segment = frames[^1].Index.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        }
    }

    private static void AfterValue(List<Frame> frames)
    {
        if (frames.Count > 0 && frames[^1].IsArray)
        {
            frames[^1] = frames[^1] with { Index = frames[^1].Index + 1 };
        }
    }

    private static string Pointer(List<Frame> frames)
    {
        var builder = new StringBuilder();
        foreach (var frame in frames)
        {
            builder.Append('/').Append(frame.Segment);
        }

        return builder.ToString();
    }

    private readonly record struct Frame(bool IsArray, int Index, string Segment);
}
