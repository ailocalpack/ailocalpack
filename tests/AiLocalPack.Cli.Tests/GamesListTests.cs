using System.Text.Json.Nodes;

namespace AiLocalPack.Cli.Tests;

public sealed class GamesListTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly SteamFixture _steam;

    public GamesListTests() => _steam = new SteamFixture(_dir);

    public void Dispose() => _dir.Dispose();

    private Task<CliResult> RunAsync(params string[] args) =>
        CliRunner.RunAsync(
            new Dictionary<string, string>
            {
                ["AILOCALPACK_STEAM_ROOT"] = _steam.Root,
                ["AILOCALPACK_HOME"] = _dir.Combine("home"),
            },
            args);

    private static JsonArray Games(CliResult result) => JsonNode.Parse(result.StdOut)!["games"]!.AsArray();

    [Fact]
    public async Task Json_lists_games_and_libraries_of_the_fixture_steam()
    {
        // Arrange
        var missing = _steam.AddLibrary("SdCard/SteamLibrary", create: false);
        _steam.AddGame(413150, "Stardew Valley", buildId: "16826371")
            .AddGame(228980, "Steamworks Common Redistributables")
            .AddGame(20, "Downloading", stateFlags: SteamFixture.Downloading)
            .SetLastPlayed("111", (413150, 1_800_000_000));
        var expected = new JsonObject
        {
            ["libraries"] = new JsonArray(
                new JsonObject { ["path"] = _steam.Root, ["available"] = true },
                new JsonObject { ["path"] = missing, ["available"] = false }),
            ["games"] = new JsonArray(new JsonObject
            {
                ["id"] = "steam:413150",
                ["source"] = "steam",
                ["appId"] = 413150,
                ["name"] = "Stardew Valley",
                ["buildId"] = "16826371",
                ["installPath"] = Path.Combine(_steam.Root, "steamapps", "common", "Stardew Valley"),
                ["libraryPath"] = _steam.Root,
                ["lastPlayed"] = "2027-01-15T08:00:00+00:00",
                ["available"] = true,
            }),
        };

        // Act
        var result = await RunAsync("games", "list", "--json");

        // Assert
        result.ExitCode.Should().Be(0);
        JsonNode.DeepEquals(JsonNode.Parse(result.StdOut), expected).Should().BeTrue(result.StdOut);
    }

    [Fact]
    public async Task Table_shows_game_build_and_the_not_available_library()
    {
        // Arrange
        _steam.AddLibrary("SdCard/SteamLibrary", create: false);
        _steam.AddGame(413150, "Stardew Valley", buildId: "16826371");

        // Act
        var result = await RunAsync("games", "list");

        // Assert
        result.ExitCode.Should().Be(0);
        result.StdOut.Should().ContainAll("413150", "16826371", "Stardew Valley", "(not available)");
    }

    [Fact]
    public async Task Missing_steam_root_prints_steam_not_found()
    {
        // Arrange
        var nowhere = _dir.Combine("no-steam");

        // Act
        var result = await CliRunner.RunAsync(
            new Dictionary<string, string> { ["AILOCALPACK_STEAM_ROOT"] = nowhere },
            "games",
            "list");

        // Assert
        result.StdOut.Should().Contain("Steam not found.");
    }

    [Fact]
    public async Task Second_run_shows_the_same_ids_from_the_catalog()
    {
        // Arrange
        _steam.AddGame(413150, "Stardew Valley").AddGame(1091500, "Cyberpunk 2077");
        var first = await RunAsync("games", "list", "--json");

        // Act
        var second = await RunAsync("games", "list", "--json");

        // Assert
        Games(second).Select(g => (string?)g!["id"]).Should()
            .Equal(Games(first).Select(g => (string?)g!["id"]))
            .And.Equal("steam:1091500", "steam:413150");
    }

    [Fact]
    public async Task Game_on_a_library_that_went_missing_stays_listed_as_not_available()
    {
        // Arrange
        var sdCard = _steam.AddLibrary("SdCard/SteamLibrary");
        _steam.AddGame(413150, "Stardew Valley", library: sdCard);
        await RunAsync("games", "list", "--json");
        Directory.Delete(sdCard, recursive: true);

        // Act
        var result = await RunAsync("games", "list", "--json");

        // Assert
        Games(result).Should().ContainSingle()
            .Which!["available"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Catalog_is_saved_in_the_app_data_folder()
    {
        // Arrange
        _steam.AddGame(413150, "Stardew Valley");

        // Act
        await RunAsync("games", "list");

        // Assert
        File.ReadAllText(_dir.Combine("home/data/games.json")).Should().Contain("steam:413150");
    }
}
