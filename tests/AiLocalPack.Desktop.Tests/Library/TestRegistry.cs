using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Desktop.Tests.Library;

/// <summary>Builds a registry index for library tests and writes it as <c>index.json</c> (a local folder, or the body of a fake https answer).</summary>
internal sealed class TestRegistry : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly Dictionary<GameId, (bool OptOut, bool AntiCheatAllowed, List<RegistryPack> Packs)> _games = [];

    /// <summary>The registry folder (pass as <c>registry:</c> to <see cref="TestServices.Build"/>) once <see cref="Write"/> ran.</summary>
    public string Path => _dir.Path;

    public TestRegistry Pack(
        uint appId,
        string language,
        string id,
        PackStatus status = PackStatus.Alpha,
        int likes = 0,
        string buildId = "1000",
        DateTimeOffset? updated = null,
        int translated = 10,
        Uri? discussion = null)
    {
        var game = GameId.Steam(appId);
        Game(game).Packs.Add(new RegistryPack(
            id,
            game,
            language,
            id,
            new PackAuthor("alice"),
            status,
            1,
            updated ?? new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            new PackCoverage(translated, 10),
            [buildId],
            1,
            $"games/steam-{appId}/{language}/{id}/manifest.json",
            100,
            new RegistryRating(likes, 0, 0),
            discussion));
        return this;
    }

    /// <summary>
    /// Writes the manifest of a pack added with <see cref="Pack"/>: a recipe pack made for <paramref name="buildId"/>
    /// touching <paramref name="files"/> (relative path → SHA-256), by alice with Claude Code, revisions 1…<paramref name="revisions"/>.
    /// </summary>
    public TestRegistry Manifest(
        uint appId,
        string language,
        string id,
        string buildId,
        IReadOnlyDictionary<string, string> files,
        int revisions = 1,
        IReadOnlyList<string>? fonts = null)
    {
        WriteFile($"games/steam-{appId}/{language}/{id}/manifest.json", PackManifestSerializer.Write(ManifestModel(appId, language, id, buildId, files, revisions, fonts)));
        return this;
    }

    /// <summary>The manifest <see cref="Manifest"/> writes.</summary>
    public static PackManifest ManifestModel(
        uint appId,
        string language,
        string id,
        string buildId,
        IReadOnlyDictionary<string, string> files,
        int revisions = 1,
        IReadOnlyList<string>? fonts = null)
    {
        var date = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        return new PackManifest(
            id,
            GameId.Steam(appId),
            language,
            id,
            [new PackAuthor("alice")],
            [.. Enumerable.Range(1, revisions).Select(n => new PackRevision(n, date.AddDays(n), "alice", n == 1 ? "first version" : null))],
            PackStatus.Alpha,
            new PackAgent("claude-code"),
            [new PackBuild(buildId, files)],
            new PackRecipe("rpgmaker", "1.0.0"),
            new PackStringsFile(PackStringsFile.DefaultPath, new string('0', 64), 10),
            null,
            [],
            fonts ?? [],
            new PackCoverage(10, 10),
            date,
            date.AddDays(revisions));
    }

    /// <summary>Writes any file of the registry (a damaged manifest).</summary>
    public TestRegistry WriteFile(string relativePath, string content)
    {
        _dir.File(relativePath, content);
        return this;
    }

    public TestRegistry OptOut(uint appId)
    {
        var game = GameId.Steam(appId);
        _games[game] = Game(game) with { OptOut = true };
        return this;
    }

    public TestRegistry AllowAntiCheat(uint appId)
    {
        var game = GameId.Steam(appId);
        _games[game] = Game(game) with { AntiCheatAllowed = true };
        return this;
    }

    public string Json() => RegistryIndexSerializer.Write(new RegistryIndex(
        new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero),
        _games.ToDictionary(
            g => g.Key,
            g => new RegistryGame(
                g.Key,
                g.Value.OptOut,
                g.Value.AntiCheatAllowed,
                g.Value.Packs.GroupBy(p => p.Language).ToDictionary(l => l.Key, l => (IReadOnlyList<RegistryPack>)[.. l])))));

    public TestRegistry Write()
    {
        File.WriteAllText(_dir.Combine("index.json"), Json());
        return this;
    }

    public void Dispose() => _dir.Dispose();

    private (bool OptOut, bool AntiCheatAllowed, List<RegistryPack> Packs) Game(GameId id)
    {
        if (!_games.TryGetValue(id, out var game))
        {
            game = (false, false, []);
            _games[id] = game;
        }

        return game;
    }
}
