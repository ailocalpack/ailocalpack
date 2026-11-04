using System.Text.Json;
using Json.Schema;

namespace AiLocalPack.Core.Tests.Packs;

/// <summary>Sample manifests (<c>tests/fixtures/packs</c>) and the JSON Schemas (<c>schemas/</c>), copied next to the tests.</summary>
internal static class PackFixtures
{
    private static readonly Lazy<JsonSchema> Manifest = new(() => Load("pack-manifest.schema.json"));
    private static readonly Lazy<JsonSchema> Table = new(() => Load("string-table.schema.json"));

    public static JsonSchema ManifestSchema => Manifest.Value;

    public static JsonSchema StringTableSchema => Table.Value;

    public static string ReadValid(string name) => File.ReadAllText(Path.Combine(Folder, "valid", name + ".json"));

    public static string ReadInvalid(string name) => File.ReadAllText(Path.Combine(Folder, "invalid", name + ".json"));

    public static IEnumerable<string> ValidNames() =>
        Directory.EnumerateFiles(Path.Combine(Folder, "valid"), "*.json").Select(Path.GetFileNameWithoutExtension).Order()!;

    public static IEnumerable<string> InvalidNames() =>
        Directory.EnumerateFiles(Path.Combine(Folder, "invalid"), "*.json").Select(Path.GetFileNameWithoutExtension).Order()!;

    /// <summary>Schema errors of <paramref name="json"/>, empty when it is valid.</summary>
    public static IReadOnlyList<string> SchemaErrors(JsonSchema schema, string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = schema.Evaluate(
            document.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        return result.IsValid
            ? []
            : [.. (result.Details ?? []).Where(d => d.Errors is not null)
                .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}")).DefaultIfEmpty("invalid")];
    }

    private static string Folder => Path.Combine(AppContext.BaseDirectory, "fixtures", "packs");

    private static JsonSchema Load(string name) =>
        JsonSchema.FromText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schemas", name)));
}
