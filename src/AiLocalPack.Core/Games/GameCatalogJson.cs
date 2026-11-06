using System.Text.Json.Serialization;

namespace AiLocalPack.Core.Games;

/// <summary>On-disk shape of <c>games.json</c>; kept apart from the domain types so the file format changes deliberately.</summary>
internal sealed record GameCatalogFile(int SchemaVersion, IReadOnlyList<GameEntryJson?>? Games);

internal sealed record GameEntryJson(
    string? Id,
    string? Name,
    string? Source,
    string? InstallPath,
    string? LibraryPath,
    GameVersionJson? Version,
    string? Engine,
    string? AntiCheat,
    DateTimeOffset? LastPlayed,
    bool Available = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MainExecutable = null);

/// <summary>Exactly one of <see cref="BuildId"/> and <see cref="Files"/> is set.</summary>
internal sealed record GameVersionJson(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BuildId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string>? Files);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(GameCatalogFile))]
internal sealed partial class GameCatalogJsonContext : JsonSerializerContext;
