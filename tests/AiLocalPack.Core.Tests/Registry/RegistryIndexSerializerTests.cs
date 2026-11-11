using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Core.Tests.Registry;

public sealed class RegistryIndexSerializerTests
{
    /// <summary>The sample index with one pack entry replaced by <paramref name="pack"/> (a JSON object).</summary>
    private static string WithPack(string pack) => $$"""
        {
          "schemaVersion": 1,
          "generated": "2026-11-04T12:00:00Z",
          "games": {
            "steam:620": { "packs": { "ru": [ {{pack}}, {{GoodPack("other")}} ] } }
          }
        }
        """;

    private static string GoodPack(string id) => $$"""
        {
          "id": "{{id}}", "title": "Full", "author": { "github": "alice" }, "status": "alpha", "revision": 1,
          "updated": "2026-11-01T00:00:00Z", "coverage": { "translated": 1, "total": 2 }, "buildIds": ["1"], "buildCount": 1,
          "manifest": "games/steam-620/ru/{{id}}/manifest.json", "size": 10
        }
        """;

    [Fact]
    public void Sample_index_reads_every_pack()
    {
        // Arrange
        var json = RegistryFixtures.ReadIndex();

        // Act
        var result = RegistryIndexSerializer.Read(json);

        // Assert
        (result.Errors.Count, result.Index!.PackCount).Should().Be((0, 4));
    }

    [Fact]
    public void Sample_index_passes_the_schema()
    {
        // Arrange
        var json = RegistryFixtures.ReadIndex();

        // Act
        var errors = RegistryFixtures.SchemaErrors(json);

        // Assert
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Sample_index_reads_the_game_flags()
    {
        // Arrange
        var json = RegistryFixtures.ReadIndex();

        // Act
        var index = RegistryIndexSerializer.Read(json).Index!;

        // Assert
        (index.Find(GameId.Steam(400))!.OptOut, index.Find(GameId.Steam(570))!.AntiCheatAllowed).Should().Be((true, true));
    }

    [Fact]
    public void Sample_index_reads_a_pack_in_full()
    {
        // Arrange
        var json = RegistryFixtures.ReadIndex();

        // Act
        var pack = RegistryIndexSerializer.Read(json).Index!.PacksFor(GameId.Steam(620), "ru").Single(p => p.Id == "full");

        // Assert
        pack.Should().BeEquivalentTo(new
        {
            Title = "Полный перевод",
            Author = new PackAuthor("alice", "Alice"),
            Status = PackStatus.Verified,
            Revision = 2,
            Coverage = new PackCoverage(1500, 1520),
            BuildIds = new[] { "1234567", "1250000" },
            BuildCount = 2,
            ManifestPath = "games/steam-620/ru/full/manifest.json",
            Rating = new RegistryRating(12, 5, 1),
            Discussion = new Uri("https://github.com/ailocalpack/registry/discussions/1"),
        });
    }

    [Fact]
    public void Writing_the_sample_index_gives_the_same_text()
    {
        // Arrange
        var json = RegistryFixtures.ReadIndex();
        var index = RegistryIndexSerializer.Read(json).Index!;

        // Act
        var written = RegistryIndexSerializer.Write(index);

        // Assert
        written.ReplaceLineEndings("\n").Should().Be(json.ReplaceLineEndings("\n").TrimEnd('\n'));
    }

    [Fact]
    public void Written_index_passes_the_schema()
    {
        // Arrange
        var index = RegistryFixtures.Index(
            RegistryFixtures.Pack("full"),
            RegistryFixtures.Pack("menus", "de", status: PackStatus.Verified),
            RegistryFixtures.Pack("full", game: GameId.Fingerprint(new string('a', 64))));

        // Act
        var json = RegistryIndexSerializer.Write(index);

        // Assert
        RegistryFixtures.SchemaErrors(json).Should().BeEmpty();
    }

    [Fact]
    public void Written_index_reads_back_the_same()
    {
        // Arrange
        var pack = RegistryFixtures.Pack();

        // Act
        var read = RegistryIndexSerializer.Read(RegistryFixtures.IndexJson(pack)).Index!;

        // Assert
        read.PacksFor(pack.Game, "ru").Should().ContainSingle().Which.Should().BeEquivalentTo(pack);
    }

    [Fact]
    public void Unknown_properties_are_ignored()
    {
        // Arrange
        var json = """
            { "schemaVersion": 1, "generated": "2026-11-04T12:00:00Z", "mirrors": ["x"],
              "games": { "steam:620": { "future": 1, "packs": { "ru": [ PACK ] } } } }
            """.Replace("PACK", GoodPack("full").Replace("\"size\": 10", "\"size\": 10, \"audio\": true"), StringComparison.Ordinal);

        // Act
        var result = RegistryIndexSerializer.Read(json);

        // Assert
        (result.Errors.Count, result.Index!.PackCount).Should().Be((0, 1));
    }

    [Fact]
    public void Text_that_is_not_json_is_refused()
    {
        // Act
        var result = RegistryIndexSerializer.Read("<html>Service unavailable</html>");

        // Assert
        (result.IsValid, result.Errors.Single().Message).Should().Be((false, "Not valid JSON (line 1, column 1)."));
    }

    [Fact]
    public void Newer_schema_version_asks_for_an_app_update()
    {
        // Act
        var result = RegistryIndexSerializer.Read("""{ "schemaVersion": 2, "generated": "2026-11-04T12:00:00Z", "games": {} }""");

        // Assert
        (result.IsValid, result.IsNewerFormat).Should().Be((false, true));
    }

    [Fact]
    public void Index_without_games_is_refused()
    {
        // Act
        var result = RegistryIndexSerializer.Read("""{ "schemaVersion": 1, "generated": "2026-11-04T12:00:00Z" }""");

        // Assert
        result.Errors.Should().ContainSingle().Which.Should().Be(new PackFormatError("games", "is missing."));
    }

    [Fact]
    public void Index_with_a_bad_generation_date_is_refused()
    {
        // Act
        var result = RegistryIndexSerializer.Read("""{ "schemaVersion": 1, "generated": "yesterday", "games": {} }""");

        // Assert
        (result.IsValid, result.Errors.Single().Field).Should().Be((false, "generated"));
    }

    [Fact]
    public void Damaged_pack_is_left_out_and_the_rest_kept()
    {
        // Arrange
        var json = WithPack(GoodPack("full").Replace("\"alpha\"", "\"beta\"", StringComparison.Ordinal));

        // Act
        var result = RegistryIndexSerializer.Read(json);

        // Assert
        (result.Index!.PacksFor(RegistryFixtures.Game, "ru").Single().Id, result.Errors.Single().ToString())
            .Should().Be(("other", "games[\"steam:620\"].packs.ru[0].status: 'beta' is not a status (alpha or verified)."));
    }

    [Fact]
    public void Pack_without_a_manifest_path_is_left_out()
    {
        // Arrange
        var json = WithPack(GoodPack("full").Replace("\"games/steam-620/ru/full/manifest.json\"", "\"../manifest.json\"", StringComparison.Ordinal));

        // Act
        var result = RegistryIndexSerializer.Read(json);

        // Assert
        result.Errors.Single().Field.Should().Be("games[\"steam:620\"].packs.ru[0].manifest");
    }

    [Fact]
    public void Pack_listed_twice_keeps_the_first()
    {
        // Arrange
        var json = WithPack(GoodPack("other"));

        // Act
        var result = RegistryIndexSerializer.Read(json);

        // Assert
        (result.Index!.PacksFor(RegistryFixtures.Game, "ru").Count, result.Errors.Single().Message)
            .Should().Be((1, "'other' is listed twice for this game and language."));
    }

    [Fact]
    public void Manual_game_pack_with_build_ids_is_left_out()
    {
        // Arrange
        var json = WithPack(GoodPack("full")).Replace("steam:620", "fp:" + new string('a', 64), StringComparison.Ordinal);

        // Act
        var result = RegistryIndexSerializer.Read(json);

        // Assert
        (result.Index!.PackCount, result.Errors.Count).Should().Be((0, 2));
    }

    [Fact]
    public void Pack_with_a_plain_http_discussion_is_left_out()
    {
        // Arrange
        var json = WithPack(GoodPack("full").Replace("\"size\": 10", "\"size\": 10, \"discussion\": \"http://example.com\"", StringComparison.Ordinal));

        // Act
        var result = RegistryIndexSerializer.Read(json);

        // Assert
        result.Errors.Single().Field.Should().EndWith("discussion");
    }

    [Fact]
    public void Game_with_a_bad_id_is_left_out()
    {
        // Act
        var result = RegistryIndexSerializer.Read("""{ "schemaVersion": 1, "generated": "2026-11-04T12:00:00Z", "games": { "steam:0620": {} } }""");

        // Assert
        (result.Index!.Games.Count, result.Errors.Single().Field).Should().Be((0, "games[\"steam:0620\"]"));
    }

    [Fact]
    public void Unknown_language_is_left_out()
    {
        // Act
        var result = RegistryIndexSerializer.Read(
            """{ "schemaVersion": 1, "generated": "2026-11-04T12:00:00Z", "games": { "steam:620": { "packs": { "russian": [] } } } }""");

        // Assert
        (result.Index!.Find(RegistryFixtures.Game)!.Packs.Count, result.Errors.Single().Field)
            .Should().Be((0, "games[\"steam:620\"].packs.russian"));
    }

    [Fact]
    public void Packs_for_another_language_are_none()
    {
        // Arrange
        var index = RegistryFixtures.Index(RegistryFixtures.Pack());

        // Act
        var packs = index.PacksFor(RegistryFixtures.Game, "de");

        // Assert
        packs.Should().BeEmpty();
    }

    /// <summary>An index with the registry's anti-cheat policy: one rule and a game listed with an anti-cheat.</summary>
    private static RegistryIndex WithAntiCheat() => RegistryFixtures.Index(RegistryFixtures.Pack()) with
    {
        Games = new Dictionary<GameId, RegistryGame>(RegistryFixtures.Index(RegistryFixtures.Pack()).Games)
        {
            [GameId.Steam(730)] = new(GameId.Steam(730), false, false, new Dictionary<string, IReadOnlyList<RegistryPack>>()) { AntiCheat = "acme-guard" },
        },
        AntiCheats = [new AntiCheatRule("acme-guard", "Acme Guard", ["AcmeGuard/", "acme*.sys"])],
    };

    [Fact]
    public void Anti_cheat_policy_reads_back_the_same()
    {
        // Arrange
        var json = RegistryIndexSerializer.Write(WithAntiCheat());

        // Act
        var index = RegistryIndexSerializer.Read(json).Index!;

        // Assert
        (index.AntiCheats.Single(), index.Find(GameId.Steam(730))!.AntiCheat).Should()
            .BeEquivalentTo((new AntiCheatRule("acme-guard", "Acme Guard", ["AcmeGuard/", "acme*.sys"]), "acme-guard"));
    }

    [Fact]
    public void Written_anti_cheat_policy_passes_the_schema()
    {
        // Arrange
        var json = RegistryIndexSerializer.Write(WithAntiCheat());

        // Act
        var errors = RegistryFixtures.SchemaErrors(json);

        // Assert
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Damaged_anti_cheat_rule_is_left_out_and_the_rest_kept()
    {
        // Arrange
        var json = """
            {
              "schemaVersion": 1, "generated": "2026-11-04T12:00:00Z", "games": {},
              "antiCheats": [
                { "id": "bad", "name": "Bad", "markers": ["../up"] },
                { "id": "acme-guard", "name": "Acme Guard", "markers": ["AcmeGuard/"] }
              ]
            }
            """;

        // Act
        var result = RegistryIndexSerializer.Read(json);

        // Assert
        (result.Index!.AntiCheats.Single().Id, result.Errors.Single().Field).Should().Be(("acme-guard", "antiCheats[0]"));
    }

    [Fact]
    public void Game_with_an_unreadable_anti_cheat_id_stays_blocked()
    {
        // Arrange
        var json = """{ "schemaVersion": 1, "generated": "2026-11-04T12:00:00Z", "games": { "steam:730": { "antiCheat": "Bad ID" } } }""";

        // Act
        var result = RegistryIndexSerializer.Read(json);

        // Assert
        (result.Index!.Find(GameId.Steam(730))!.AntiCheat, result.Errors.Single().Field).Should().Be(("Bad ID", "games[\"steam:730\"].antiCheat"));
    }
}
