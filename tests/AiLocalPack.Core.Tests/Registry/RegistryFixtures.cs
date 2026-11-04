using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Tests.Packs;
using Json.Schema;

namespace AiLocalPack.Core.Tests.Registry;

/// <summary>The sample registry (<c>tests/fixtures/registry</c>) and small indexes built in code.</summary>
internal static class RegistryFixtures
{
    private static readonly Lazy<JsonSchema> Schema = new(() =>
        JsonSchema.FromText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schemas", "registry-index.schema.json"))));

    public static JsonSchema IndexSchema => Schema.Value;

    public static string Folder => Path.Combine(AppContext.BaseDirectory, "fixtures", "registry");

    public static string ReadIndex() => File.ReadAllText(Path.Combine(Folder, "index.json"));

    public static IReadOnlyList<string> SchemaErrors(string json) => PackFixtures.SchemaErrors(IndexSchema, json);

    public static GameId Game { get; } = GameId.Steam(620);

    public static RegistryPack Pack(string id = "full", string language = "ru", GameId? game = null, PackStatus status = PackStatus.Alpha) =>
        new(
            id,
            game ?? Game,
            language,
            "Full translation",
            new PackAuthor("alice", "Alice"),
            status,
            2,
            new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero),
            new PackCoverage(1500, 1520),
            (game ?? Game).Kind == GameIdKind.Steam ? ["1234567"] : [],
            1,
            RegistryLayout.ManifestPath(game ?? Game, language, id),
            48213,
            new RegistryRating(12, 5, 1),
            new Uri("https://github.com/ailocalpack/registry/discussions/1"));

    public static RegistryIndex Index(params RegistryPack[] packs) => new(
        new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero),
        packs.GroupBy(p => p.Game).ToDictionary(
            g => g.Key,
            g => new RegistryGame(
                g.Key,
                false,
                false,
                g.GroupBy(p => p.Language).ToDictionary(l => l.Key, IReadOnlyList<RegistryPack> (l) => [.. l]))));

    public static string IndexJson(params RegistryPack[] packs) => RegistryIndexSerializer.Write(Index(packs));
}
