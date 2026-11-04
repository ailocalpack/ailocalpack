using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;

namespace AiLocalPack.Core.Registry;

/// <summary>
/// <c>index.json</c> of the registry: every pack by game and language, with the community numbers the registry's
/// Actions aggregate. Read and written by <see cref="RegistryIndexSerializer"/>; the JSON Schema is
/// <c>schemas/registry-index.schema.json</c>. The app browses from this alone and fetches a manifest only when needed.
/// </summary>
/// <param name="Generated">When the registry built the index.</param>
public sealed record RegistryIndex(DateTimeOffset Generated, IReadOnlyDictionary<GameId, RegistryGame> Games)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Every pack, game by game.</summary>
    public IEnumerable<RegistryPack> Packs => Games.Values.SelectMany(g => g.AllPacks);

    public int PackCount => Games.Values.Sum(g => g.AllPacks.Count());

    public RegistryGame? Find(GameId game) => Games.GetValueOrDefault(game);

    /// <summary>The packs for <paramref name="game"/> into <paramref name="language"/> (an exact tag), or none.</summary>
    public IReadOnlyList<RegistryPack> PacksFor(GameId game, string language) => Find(game)?.PacksIn(language) ?? [];
}

/// <summary>A game the registry knows: its packs per language and the flags set for it.</summary>
/// <param name="OptOut">The publisher asked not to distribute fan translations: install and generation are blocked.</param>
/// <param name="AntiCheatAllowed">The game has an anti-cheat but officially allows mods, so it is not blocked.</param>
/// <param name="Packs">Language tag → packs into that language.</param>
public sealed record RegistryGame(
    GameId Id,
    bool OptOut,
    bool AntiCheatAllowed,
    IReadOnlyDictionary<string, IReadOnlyList<RegistryPack>> Packs)
{
    public IEnumerable<RegistryPack> AllPacks => Packs.Values.SelectMany(p => p);

    public IReadOnlyList<RegistryPack> PacksIn(string language) => Packs.GetValueOrDefault(language) ?? [];
}

/// <summary>One pack as the index lists it: enough to browse, sort and pick; the rest is in its manifest.</summary>
/// <param name="Author">The original author (the manifest's first author).</param>
/// <param name="BuildIds">Store build IDs the pack supports; empty for a manually added game.</param>
/// <param name="BuildCount">How many game builds the manifest lists.</param>
/// <param name="ManifestPath">The manifest, relative to <c>index.json</c> (<see cref="RegistryLocation.Resolve"/>).</param>
/// <param name="Size">Download size in bytes (string table or binary patch).</param>
/// <param name="Discussion">The pack's GitHub Discussion, once it exists.</param>
public sealed record RegistryPack(
    string Id,
    GameId Game,
    string Language,
    string Title,
    PackAuthor Author,
    PackStatus Status,
    int Revision,
    DateTimeOffset Updated,
    PackCoverage Coverage,
    IReadOnlyList<string> BuildIds,
    int BuildCount,
    string ManifestPath,
    long Size,
    RegistryRating Rating,
    Uri? Discussion = null);

/// <summary>Community numbers of a pack, from its GitHub Discussion.</summary>
/// <param name="Likes">👍 reactions.</param>
/// <param name="Works">Players (not the author) who confirmed it works.</param>
/// <param name="DoesNotWork">Players who reported it doesn't work.</param>
public sealed record RegistryRating(int Likes, int Works, int DoesNotWork)
{
    public static RegistryRating None { get; } = new(0, 0, 0);
}
