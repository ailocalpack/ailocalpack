using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Recipes.Agent;
using AiLocalPack.Toolkit.Validation;

namespace AiLocalPack.Pipeline;

/// <summary>
/// What the Extract stage found (<c>source.json</c> in the job folder, <see cref="GenerationFiles.SourcePath"/>): the
/// recipe tool that read the game, the files the strings come from and every string with its original, context, length
/// limit and placeholders. The translate stage (task 50) cuts it into chunks; the original texts stay in the job folder
/// on the player's machine and never go into a pack.
/// </summary>
public sealed record SourceStrings(EngineKind Engine, string Tool, Version ToolVersion, IReadOnlyList<string> Files, IReadOnlyList<ExtractedString> Strings)
{
    public const int CurrentSchemaVersion = 1;

    public static SourceStrings Of(EngineKind engine, RecipeExtraction extraction)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(extraction);
        return new SourceStrings(engine, extraction.Tool, extraction.Version, extraction.Files, extraction.Strings);
    }
}

/// <summary>The markup the validator checks the source's translations with: the recipe tool's, or for strings the agent extracted (task 57) the placeholders its extractor listed.</summary>
public static class SourceMarkup
{
    public static TextMarkup Of(RecipeTools tools, SourceStrings source)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(source);
        return source.Tool == AgentScriptTool.ToolId
            ? AgentScriptTool.MarkupOf(source.Strings)
            : tools.Find(source.Tool)?.Markup ?? TextMarkup.None;
    }
}

/// <summary>
/// <c>source.json</c>: <c>{schemaVersion: 1, engine, tool, toolVersion, files: [path], strings: [{key, original, context?,
/// maxLength?, maxLengthUnit? ("displayWidth"), placeholders?}]}</c>, strings in the extractor's (ordinal) order, written
/// atomically.
/// </summary>
public static class SourceStringsFile
{
    private static readonly JsonSerializerOptions WriteOptions = new(SourceJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <exception cref="IOException">The file can't be written.</exception>
    public static void Write(GenerationFiles files, SourceStrings source)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(source);
        var json = new SourceFileJson(
            SourceStrings.CurrentSchemaVersion,
            source.Engine.ToString(),
            source.Tool,
            source.ToolVersion.ToString(3),
            source.Files,
            [.. source.Strings.Select(s => new SourceStringJson(
                s.Key,
                s.Original,
                s.Context,
                s.MaxLength,
                s.MaxLength is null || s.MaxLengthUnit == LengthUnit.Characters ? null : JsonNamingPolicy.CamelCase.ConvertName(s.MaxLengthUnit.ToString()),
                s.Placeholders.Count > 0 ? s.Placeholders : null))]);
        AtomicFile.WriteAllBytes(files.SourcePath, JsonSerializer.SerializeToUtf8Bytes(json, WriteOptions.GetTypeInfo(typeof(SourceFileJson))));
    }

    /// <summary>The stored strings, or <c>null</c> when the Extract stage hasn't written them.</summary>
    /// <exception cref="GenerationFileException">The file can't be read or isn't valid.</exception>
    public static SourceStrings? Read(GenerationFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var path = files.SourcePath;
        byte[] data;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            data = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new GenerationFileException(path, e.Message, e);
        }

        SourceFileJson? json;
        try
        {
            json = JsonSerializer.Deserialize(data, SourceJsonContext.Default.SourceFileJson);
        }
        catch (JsonException e)
        {
            throw new GenerationFileException(path, $"not valid JSON ({e.Message})", e);
        }

        if (json is null || json.SchemaVersion != SourceStrings.CurrentSchemaVersion)
        {
            throw new GenerationFileException(path, $"schemaVersion {json?.SchemaVersion} is not supported");
        }

        if (!EngineKind.TryParse(json.Engine, out var engine) || string.IsNullOrEmpty(json.Tool) || !System.Version.TryParse(json.ToolVersion, out var version))
        {
            throw new GenerationFileException(path, "engine, tool or toolVersion is missing or invalid");
        }

        var strings = new List<ExtractedString>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in json.Strings ?? [])
        {
            if (item is null || string.IsNullOrEmpty(item.Key) || item.Original is null || !keys.Add(item.Key))
            {
                throw new GenerationFileException(path, $"a string has no key or original, or its key '{item?.Key}' is listed twice");
            }

            var unit = item.MaxLengthUnit switch
            {
                null => LengthUnit.Characters,
                "displayWidth" => LengthUnit.DisplayWidth,
                "characters" => LengthUnit.Characters,
                _ => throw new GenerationFileException(path, $"'{item.Key}': unknown maxLengthUnit '{item.MaxLengthUnit}'"),
            };
            strings.Add(new ExtractedString(item.Key, item.Original, item.Context, item.MaxLength, item.Placeholders ?? []) { MaxLengthUnit = unit });
        }

        return new SourceStrings(engine, json.Tool, version, json.Files ?? [], strings);
    }
}

internal sealed record SourceFileJson(
    int SchemaVersion,
    string? Engine,
    string? Tool,
    string? ToolVersion,
    IReadOnlyList<string>? Files,
    IReadOnlyList<SourceStringJson?>? Strings);

internal sealed record SourceStringJson(
    string? Key,
    string? Original,
    string? Context,
    int? MaxLength,
    string? MaxLengthUnit,
    IReadOnlyList<string>? Placeholders);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SourceFileJson))]
internal sealed partial class SourceJsonContext : JsonSerializerContext;
