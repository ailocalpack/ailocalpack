using System.Text.Json.Serialization;

namespace AiLocalPack.Core.Registry;

// On-disk shapes of index.json and the client's cache record, apart from the domain types: everything nullable so a
// damaged entry can be skipped with a reason. Unknown properties are ignored (forward compatibility).

internal sealed record RegistryIndexJson(int? SchemaVersion, string? Generated, IReadOnlyDictionary<string, RegistryGameJson?>? Games);

internal sealed record RegistryGameJson(bool? OptOut, bool? AntiCheatAllowed, IReadOnlyDictionary<string, IReadOnlyList<RegistryPackJson?>?>? Packs);

internal sealed record RegistryPackJson(
    string? Id,
    string? Title,
    RegistryAuthorJson? Author,
    string? Status,
    int? Revision,
    string? Updated,
    RegistryCoverageJson? Coverage,
    IReadOnlyList<string?>? BuildIds,
    int? BuildCount,
    string? Manifest,
    long? Size,
    RegistryRatingJson? Rating,
    string? Discussion);

internal sealed record RegistryAuthorJson(string? Github, string? Name);

internal sealed record RegistryCoverageJson(int? Translated, int? Total);

internal sealed record RegistryRatingJson(int? Likes, int? Works, int? DoesNotWork);

/// <summary>What the client remembers about the cached <c>index.json</c> (<c>index.meta.json</c> next to it).</summary>
/// <param name="Source">The index URL the copy came from; a copy from another source is not used.</param>
/// <param name="ETag">Validator for <c>If-None-Match</c>, as the server sent it.</param>
/// <param name="LastModified">Validator for <c>If-Modified-Since</c>, as the server sent it (RFC 1123).</param>
/// <param name="FetchedAt">Last time the server confirmed this copy (200 or 304).</param>
/// <param name="Sha256">Of the cached <c>index.json</c>: a copy that doesn't match (half-written by a crash) is not used.</param>
internal sealed record RegistryCacheMetaJson(
    int? SchemaVersion,
    string? Source,
    string? ETag,
    string? LastModified,
    DateTimeOffset? FetchedAt,
    string? Sha256);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(RegistryIndexJson))]
[JsonSerializable(typeof(RegistryCacheMetaJson))]
internal sealed partial class RegistryJsonContext : JsonSerializerContext;
