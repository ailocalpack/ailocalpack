using System.Text.Json.Serialization;

namespace AiLocalPack.Core.Activity;

/// <summary>On-disk shape of <c>activity.json</c>, kept apart from the domain types.</summary>
internal sealed record ActivityFile(int SchemaVersion, IReadOnlyList<ActivityEventJson?>? Events);

/// <param name="Kind">The <see cref="ActivityEventKind"/> name, camelCase (<c>packInstalled</c>).</param>
internal sealed record ActivityEventJson(
    string? Id,
    string? Kind,
    DateTimeOffset Time,
    string? Game = null,
    string? GameName = null,
    string? Pack = null,
    string? PackTitle = null,
    int? Revision = null,
    string? Detail = null,
    IReadOnlyList<string>? Log = null,
    bool FilesRestored = true,
    bool CanRetry = false,
    string? Key = null,
    bool Decision = false,
    bool Read = false,
    bool Hidden = false);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ActivityFile))]
internal sealed partial class ActivityJsonContext : JsonSerializerContext;
