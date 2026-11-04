using AiLocalPack.Core.Games;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Core.Tests.Registry;

public sealed class RegistryInputsTests
{
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
