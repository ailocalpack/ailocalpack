using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.Languages;

namespace AiLocalPack.Core.Generation;

/// <summary>
/// The names, terms and tone a generation keeps the same across every chunk (<c>glossary.json</c> in the job folder).
/// The agent writes it through the MCP tool <c>put_glossary</c>; the translate stage (task 50) feeds the relevant
/// entries back with each chunk.
/// </summary>
/// <param name="Language">The translation's language tag.</param>
/// <param name="Tone">Tone and form of address in the agent's words (<c>informal "ты", playful</c>), or <c>null</c>.</param>
/// <param name="Entries">Terms in the order they were first added; a term is unique ignoring case.</param>
public sealed record Glossary(string Language, string? Tone, IReadOnlyList<GlossaryEntry> Entries)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Longest term, translation, note or tone accepted (characters).</summary>
    public const int MaxTextLength = 2_000;

    /// <summary>Terms compare ignoring case (<c>Potion</c> and <c>potion</c> are one entry).</summary>
    public static StringComparer TermComparer { get; } = StringComparer.OrdinalIgnoreCase;

    public static Glossary Empty(string language) => new(language, null, []);
}

/// <param name="Term">The term as the game writes it.</param>
/// <param name="Translation">How the translation writes it; with <paramref name="KeepAsIs"/> the term itself.</param>
/// <param name="Note">Why, or how to use it (<c>the heroine; female</c>).</param>
/// <param name="PartOfSpeech">For inflected languages: <c>noun</c>, <c>verb</c>…</param>
/// <param name="Gender">For inflected languages: <c>masculine</c>, <c>feminine</c>, <c>neuter</c>…</param>
/// <param name="KeepAsIs">The term stays untranslated (a brand, a made-up name).</param>
public sealed record GlossaryEntry(
    string Term,
    string Translation,
    string? Note = null,
    string? PartOfSpeech = null,
    string? Gender = null,
    bool KeepAsIs = false);

/// <summary>Reads and writes <c>glossary.json</c>: <c>{schemaVersion: 1, language, tone?, entries: [{term, translation, note?, partOfSpeech?, gender?, keepAsIs?}]}</c>.</summary>
public static class GlossarySerializer
{
    private static readonly JsonSerializerOptions WriteOptions = new(GlossaryJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The glossary, or the reason it can't be read. Never throws for bad content.</summary>
    public static (Glossary? Glossary, string? Error) Read(ReadOnlySpan<byte> data)
    {
        GlossaryFileJson? file;
        try
        {
            file = JsonSerializer.Deserialize(data, GlossaryJsonContext.Default.GlossaryFileJson);
        }
        catch (JsonException e)
        {
            return (null, $"not valid JSON ({e.Message})");
        }

        if (file is null)
        {
            return (null, "is empty");
        }

        if (file.SchemaVersion != Glossary.CurrentSchemaVersion)
        {
            return (null, file.SchemaVersion > Glossary.CurrentSchemaVersion
                ? $"schemaVersion {file.SchemaVersion} is newer than this app reads; update the app"
                : $"schemaVersion {file.SchemaVersion} is not supported");
        }

        if (file.Language is not { } language || !GameLanguages.Tags.Contains(language, StringComparer.Ordinal))
        {
            return (null, $"language '{file.Language}' is not a supported language tag");
        }

        var entries = new List<GlossaryEntry>();
        var terms = new HashSet<string>(Glossary.TermComparer);
        foreach (var item in file.Entries ?? [])
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Term))
            {
                return (null, "an entry has no term");
            }

            if (!terms.Add(item.Term))
            {
                return (null, $"term '{item.Term}' is listed twice");
            }

            var translation = item.KeepAsIs ? item.Term : item.Translation;
            if (string.IsNullOrWhiteSpace(translation))
            {
                return (null, $"term '{item.Term}' has no translation");
            }

            entries.Add(new GlossaryEntry(item.Term, translation, Blank(item.Note), Blank(item.PartOfSpeech), Blank(item.Gender), item.KeepAsIs));
        }

        return (new Glossary(language, Blank(file.Tone), entries), null);
    }

    /// <summary>Indented JSON, entries in their order, non-ASCII unescaped.</summary>
    public static byte[] Write(Glossary glossary)
    {
        ArgumentNullException.ThrowIfNull(glossary);
        var file = new GlossaryFileJson(
            Glossary.CurrentSchemaVersion,
            glossary.Language,
            glossary.Tone,
            [.. glossary.Entries.Select(e => new GlossaryEntryJson(e.Term, e.Translation, e.Note, e.PartOfSpeech, e.Gender, e.KeepAsIs))]);
        return JsonSerializer.SerializeToUtf8Bytes(file, WriteOptions.GetTypeInfo(typeof(GlossaryFileJson)));
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}

internal sealed record GlossaryFileJson(int SchemaVersion, string? Language, string? Tone, IReadOnlyList<GlossaryEntryJson?>? Entries);

internal sealed record GlossaryEntryJson(
    string? Term,
    string? Translation,
    string? Note = null,
    string? PartOfSpeech = null,
    string? Gender = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool KeepAsIs = false);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GlossaryFileJson))]
internal sealed partial class GlossaryJsonContext : JsonSerializerContext;
