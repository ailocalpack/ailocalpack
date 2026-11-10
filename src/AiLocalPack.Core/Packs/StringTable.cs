using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiLocalPack.Core.Languages;

namespace AiLocalPack.Core.Packs;

/// <summary>
/// The translations of a pack: key (given by the recipe's extractor) → translated text, plus optional translator notes
/// per key. Holds no original text. Stored as gzip-compressed JSON (<see cref="PackStringsFile.DefaultPath"/>); the
/// JSON Schema is <c>schemas/string-table.schema.json</c>.
/// </summary>
/// <param name="Language">One of <see cref="GameLanguages.Tags"/>, the same as the manifest's.</param>
/// <param name="Strings">Key → translation, keys in ordinal order. Keys are not empty.</param>
/// <param name="Context">Key (present in <see cref="Strings"/>) → note.</param>
public sealed record StringTable(string Language, IReadOnlyDictionary<string, string> Strings, IReadOnlyDictionary<string, string> Context)
{
    public const int CurrentSchemaVersion = 1;

    public StringTable(string language, IReadOnlyDictionary<string, string> strings)
        : this(language, strings, new Dictionary<string, string>())
    {
    }

    /// <summary>
    /// Key (present in <see cref="Strings"/>) → <see cref="OriginalHash"/> of the original text the translation was made
    /// from (task 56), so a later revision can tell which strings a game update changed. Optional: tables made before
    /// task 56 have none (their strings count as unchanged).
    /// </summary>
    public IReadOnlyDictionary<string, string> Originals { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// The hash of an original text a string table keeps per key (<see cref="StringTable.Originals"/>, task 56): the first
/// 16 lowercase hex digits (64 bits) of the SHA-256 of its UTF-8 bytes. Enough to tell a changed text, and it doesn't
/// hold the text itself.
/// </summary>
public static class OriginalHash
{
    public const int Length = 16;

    public static string Of(string original)
    {
        ArgumentNullException.ThrowIfNull(original);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(original)))[..Length];
    }

    public static bool IsValid(string? hash) =>
        hash is { Length: Length } && hash.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}

/// <summary>Reads and writes <see cref="StringTable"/> files, gzip-compressed or plain JSON.</summary>
public static class StringTableSerializer
{
    /// <summary>A decompressed table larger than this is refused (a gzip bomb, or not a string table).</summary>
    public const long MaxJsonSize = 256L * 1024 * 1024;

    private static ReadOnlySpan<byte> GzipMagic => [0x1F, 0x8B];

    /// <summary>Reads a table; gzip is detected by its magic bytes, anything else is read as UTF-8 JSON.</summary>
    public static PackReadResult<StringTable> Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        return CopyLimited(stream, buffer) ? Read(buffer.ToArray()) : TooLarge();
    }

    /// <summary>Reads gzip bytes or UTF-8 JSON.</summary>
    public static PackReadResult<StringTable> Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.AsSpan().StartsWith(GzipMagic))
        {
            using var json = new MemoryStream();
            try
            {
                using var gzip = new GZipStream(new MemoryStream(data, writable: false), CompressionMode.Decompress);
                if (!CopyLimited(gzip, json))
                {
                    return TooLarge();
                }
            }
            catch (InvalidDataException)
            {
                return Fail("The file is not a valid gzip archive.");
            }

            // A truncated archive inflates to nothing rather than throwing.
            if (json.Length == 0)
            {
                return Fail("The file is not a valid gzip archive.");
            }

            data = json.ToArray();
        }

        StringTableJson? file;
        try
        {
            file = JsonSerializer.Deserialize(data, PackJsonContext.Default.StringTableJson);
        }
        catch (JsonException e)
        {
            return new PackReadResult<StringTable>(null, [PackFormat.FromJsonException(e)]);
        }

        if (file is null)
        {
            return Fail("The file holds no string table (null).");
        }

        if (PackFormat.CheckSchemaVersion(file.SchemaVersion, StringTable.CurrentSchemaVersion) is { } versionError)
        {
            return new PackReadResult<StringTable>(null, [versionError]);
        }

        var errors = new PackFormatErrors();
        if (file.Language is null)
        {
            errors.Add("language", "is missing.");
        }
        else if (!GameLanguages.Tags.Contains(file.Language, StringComparer.Ordinal))
        {
            errors.Add("language", $"'{file.Language}' is not a supported language.");
        }

        var strings = Entries("strings", file.Strings, errors);
        if (file.Strings is null)
        {
            errors.Add("strings", "is missing.");
        }

        var context = Entries("context", file.Context, errors);
        foreach (var key in context.Keys.Where(k => !strings.ContainsKey(k)))
        {
            errors.Add(PackFormat.Key("context", key), "has no translation in strings.");
        }

        var originals = Entries("originals", file.Originals, errors);
        foreach (var (key, hash) in originals)
        {
            if (!strings.ContainsKey(key))
            {
                errors.Add(PackFormat.Key("originals", key), "has no translation in strings.");
            }
            else if (!OriginalHash.IsValid(hash))
            {
                errors.Add(PackFormat.Key("originals", key), $"'{hash}' is not {OriginalHash.Length} lowercase hex digits.");
            }
        }

        return errors.Any
            ? new PackReadResult<StringTable>(null, errors.List)
            : new PackReadResult<StringTable>(new StringTable(file.Language!, strings, context) { Originals = originals }, []);
    }

    /// <summary>Writes the table as indented JSON with keys in ordinal order, gzip-compressed unless <paramref name="compress"/> is false.</summary>
    public static void Write(StringTable table, Stream stream, bool compress = true)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(stream);
        var file = new StringTableJson(
            StringTable.CurrentSchemaVersion,
            table.Language,
            new SortedDictionary<string, string?>(table.Strings.ToDictionary(e => e.Key, string? (e) => e.Value), StringComparer.Ordinal),
            Sorted(table.Context),
            Sorted(table.Originals));
        if (!compress)
        {
            JsonSerializer.Serialize(stream, file, PackJsonWriter.Context.StringTableJson);
            return;
        }

        using var gzip = new GZipStream(stream, CompressionLevel.SmallestSize, leaveOpen: true);
        JsonSerializer.Serialize(gzip, file, PackJsonWriter.Context.StringTableJson);
    }

    private static SortedDictionary<string, string?>? Sorted(IReadOnlyDictionary<string, string> entries) =>
        entries.Count > 0 ? new SortedDictionary<string, string?>(entries.ToDictionary(e => e.Key, string? (e) => e.Value), StringComparer.Ordinal) : null;

    private static SortedDictionary<string, string> Entries(string field, IReadOnlyDictionary<string, string?>? entries, PackFormatErrors errors)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in entries ?? new Dictionary<string, string?>())
        {
            if (key.Length == 0)
            {
                errors.Add(field, "has an empty key.");
            }
            else if (value is null)
            {
                errors.Add(PackFormat.Key(field, key), "must be text, not null.");
            }
            else
            {
                result[key] = value;
            }
        }

        return result;
    }

    /// <summary>Copies at most <see cref="MaxJsonSize"/> bytes; <c>false</c> when there is more.</summary>
    private static bool CopyLimited(Stream source, MemoryStream target)
    {
        var chunk = new byte[81920];
        int n;
        while ((n = source.Read(chunk)) > 0)
        {
            target.Write(chunk, 0, n);
            if (target.Length > MaxJsonSize)
            {
                return false;
            }
        }

        return true;
    }

    private static PackReadResult<StringTable> TooLarge() => Fail($"The string table is larger than {MaxJsonSize / 1024 / 1024} MB.");

    private static PackReadResult<StringTable> Fail(string message) =>
        new(null, [new PackFormatError("", message)]);
}
