using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Core.Tests.Registry;

public sealed class RegistryInputsTests
{
    [Fact]
    public void Written_ratings_read_back()
    {
        // Arrange
        var written = new Dictionary<RegistryPackKey, RegistryPackRating>
        {
            [new RegistryPackKey(GameId.Steam(620), "ru", "full")] = new(new RegistryRating(4, 3, 1), new Uri("https://github.com/ailocalpack/registry/discussions/7"), 2),
            [new RegistryPackKey(GameId.Steam(620), "de", "komplett")] = new(new RegistryRating(0, 0, 0), null, 1),
        };

        // Act
        var (read, _) = RegistryRatings.Read(RegistryRatings.Write(written));

        // Assert
        read.Should().BeEquivalentTo(written);
    }

    [Fact]
    public void Ratings_are_written_in_pack_order()
    {
        // Arrange
        var written = new Dictionary<RegistryPackKey, RegistryPackRating>
        {
            [new RegistryPackKey(GameId.Steam(620), "ru", "full")] = new(RegistryRating.None, null, 1),
            [new RegistryPackKey(GameId.Steam(620), "de", "komplett")] = new(RegistryRating.None, null, 1),
        };

        // Act
        var json = RegistryRatings.Write(written);

        // Assert
        json.IndexOf("steam:620/de/komplett", StringComparison.Ordinal).Should().BeLessThan(json.IndexOf("steam:620/ru/full", StringComparison.Ordinal));
    }

    [Fact]
    public void Verified_policy_reads_overrides_by_revision()
    {
        // Act
        var (overrides, _) = VerifiedPolicy.Read("""
            {"schemaVersion": 1, "packs": [
              {"pack": "steam:620/ru/full", "revision": 3, "status": "verified", "note": "checked"},
              {"pack": "steam:620/ru/full", "revision": 4, "status": "alpha"}]}
            """);

        // Assert
        overrides.Should().BeEquivalentTo(new Dictionary<(RegistryPackKey, int), PackStatus>
        {
            [(new RegistryPackKey(GameId.Steam(620), "ru", "full"), 3)] = PackStatus.Verified,
            [(new RegistryPackKey(GameId.Steam(620), "ru", "full"), 4)] = PackStatus.Alpha,
        });
    }

    [Theory]
    [InlineData("""{"schemaVersion": 1, "packs": [{"pack": "steam:620", "revision": 1, "status": "alpha"}]}""", "packs[0].pack")]
    [InlineData("""{"schemaVersion": 1, "packs": [{"pack": "steam:620/ru/full", "status": "alpha"}]}""", "packs[0].revision")]
    [InlineData("""{"schemaVersion": 1, "packs": [{"pack": "steam:620/ru/full", "revision": 1, "status": "gold"}]}""", "packs[0].status")]
    [InlineData("""{"schemaVersion": 1, "packs": [{"pack": "steam:620/ru/full", "revision": 1, "status": "alpha"}, {"pack": "steam:620/ru/full", "revision": 1, "status": "verified"}]}""", "packs[1]")]
    [InlineData("""{"schemaVersion": 2, "packs": []}""", "schemaVersion")]
    public void Bad_verified_policy_refuses_the_file(string json, string field)
    {
        // Act
        var (overrides, errors) = VerifiedPolicy.Read(json);

        // Assert
        (overrides, errors.Single().Field).Should().Be((null, field));
    }

    [Fact]
    public void Ratings_read_counts_and_discussion()
    {
        // Arrange
        const string json = """
            {"schemaVersion": 1, "packs": {"steam:620/ru/full": {"likes": 3, "works": 2, "doesNotWork": 1,
              "discussion": "https://github.com/ailocalpack/registry/discussions/7"}}}
            """;

        // Act
        var (ratings, _) = RegistryRatings.Read(json);

        // Assert
        ratings!.Single().Should().Be(new KeyValuePair<RegistryPackKey, RegistryPackRating>(
            new RegistryPackKey(GameId.Steam(620), "ru", "full"),
            new RegistryPackRating(new RegistryRating(3, 2, 1), new Uri("https://github.com/ailocalpack/registry/discussions/7"))));
    }

    [Theory]
    [InlineData("""{"schemaVersion": 1, "packs": {"steam:620/xx/full": {}}}""", "packs[\"steam:620/xx/full\"]")]
    [InlineData("""{"schemaVersion": 1, "packs": {"steam:620/ru/full": {"likes": -1}}}""", "packs[\"steam:620/ru/full\"].likes")]
    [InlineData("""{"schemaVersion": 1, "packs": {"steam:620/ru/full": {"discussion": "http://x"}}}""", "packs[\"steam:620/ru/full\"].discussion")]
    [InlineData("""{"schemaVersion": 1, "packs": {"steam:620/ru/full": {"revision": 0}}}""", "packs[\"steam:620/ru/full\"].revision")]
    [InlineData("""{"schemaVersion": 2, "packs": {}}""", "schemaVersion")]
    public void Bad_ratings_refuse_the_file(string json, string field)
    {
        // Act
        var (ratings, errors) = RegistryRatings.Read(json);

        // Assert
        (ratings, errors.Single().Field).Should().Be((null, field));
    }

    [Fact]
    public void Opt_out_list_reads_its_games()
    {
        // Act
        var (games, _) = RegistryOptOuts.Read("""{"schemaVersion": 1, "games": [{"game": "steam:400", "note": "asked"}]}""");

        // Assert
        games.Should().Equal(GameId.Steam(400));
    }

    [Fact]
    public void Opt_out_listed_twice_refuses_the_file()
    {
        // Act
        var (games, errors) = RegistryOptOuts.Read("""{"schemaVersion": 1, "games": [{"game": "steam:400"}, {"game": "steam:400"}]}""");

        // Assert
        (games, errors.Single().Field).Should().Be((null, "games[1].game"));
    }

    [Theory]
    [InlineData("steam:620/ru/full", true)]
    [InlineData("steam:620/ru", false)]
    [InlineData("steam:620/ru/Full", false)]
    [InlineData("game/ru/full", false)]
    public void Pack_key_parses_game_language_and_id(string text, bool valid)
    {
        // Act
        var parsed = RegistryPackKey.TryParse(text, out _);

        // Assert
        parsed.Should().Be(valid);
    }
}
