using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiLocalPack.Core.Generation;

/// <summary><c>refs/refs.json</c> of a job folder: <c>{schemaVersion: 1, packs: [{language, pack, sameBuild}]}</c> in priority order.</summary>
internal static class ReferenceIndexJson
{
    public const int CurrentSchemaVersion = 1;

    public static byte[] Write(IReadOnlyList<ReferencePack> packs) => JsonSerializer.SerializeToUtf8Bytes(
        new ReferenceIndexFileJson(CurrentSchemaVersion, [.. packs.Select(p => new ReferenceEntryJson(p.Manifest.Language, p.Manifest.Id, p.SameBuild ? true : null))]),
        ReferenceIndexJsonContext.Default.ReferenceIndexFileJson);

    /// <summary>The entries, or <c>null</c> for a file that isn't one (or is from a newer app).</summary>
    public static IReadOnlyList<(string Language, string Pack, bool SameBuild)>? Read(byte[] data)
    {
        try
        {
            var file = JsonSerializer.Deserialize(data, ReferenceIndexJsonContext.Default.ReferenceIndexFileJson);
            if (file is not { SchemaVersion: CurrentSchemaVersion, Packs: { } packs })
            {
                return null;
            }

            return [.. packs.Where(p => p is { Language: not null, Pack: not null }).Select(p => (p!.Language!, p.Pack!, p.SameBuild ?? false))];
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal sealed record ReferenceIndexFileJson(int SchemaVersion, IReadOnlyList<ReferenceEntryJson?>? Packs);

internal sealed record ReferenceEntryJson(string? Language, string? Pack, bool? SameBuild = null);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ReferenceIndexFileJson))]
internal sealed partial class ReferenceIndexJsonContext : JsonSerializerContext;
