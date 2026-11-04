using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiLocalPack.Core.Packs;

// On-disk shapes of manifest.json and the string table, kept apart from the domain types: everything is nullable so a
// broken file reads far enough to list all its problems. Unknown properties are ignored (forward compatibility).

internal sealed record PackManifestJson(
    int? SchemaVersion,
    string? Id,
    string? Game,
    string? Language,
    string? Title,
    IReadOnlyList<PackAuthorJson?>? Authors,
    int? Revision,
    IReadOnlyList<PackRevisionJson?>? Revisions,
    string? Status,
    PackAgentJson? Agent,
    IReadOnlyList<PackBuildJson?>? Builds,
    PackRecipeJson? Recipe,
    PackStringsJson? Strings,
    PackPatchJson? Patch,
    IReadOnlyList<PackDependencyJson?>? Dependencies,
    IReadOnlyList<string?>? Fonts,
    PackCoverageJson? Coverage,
    string? License,
    string? Created,
    string? Updated);

internal sealed record PackAuthorJson(string? Github, string? Name);

internal sealed record PackRevisionJson(int? Revision, string? Date, string? By, string? Notes);

internal sealed record PackAgentJson(string? Id, string? Version, string? Model);

internal sealed record PackBuildJson(string? BuildId, IReadOnlyDictionary<string, string?>? Files);

internal sealed record PackRecipeJson(string? Tool, string? Version, JsonElement? Parameters);

internal sealed record PackStringsJson(string? Path, string? Sha256, long? Size);

internal sealed record PackPatchJson(string? Format, string? Url, string? Sha256, long? Size, IReadOnlyDictionary<string, string?>? Files);

internal sealed record PackDependencyJson(string? Id, string? Version);

internal sealed record PackCoverageJson(int? Translated, int? Total);

internal sealed record StringTableJson(
    int? SchemaVersion,
    string? Language,
    IReadOnlyDictionary<string, string?>? Strings,
    IReadOnlyDictionary<string, string?>? Context);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(PackManifestJson))]
[JsonSerializable(typeof(StringTableJson))]
[JsonSerializable(typeof(string))]
internal sealed partial class PackJsonContext : JsonSerializerContext;
