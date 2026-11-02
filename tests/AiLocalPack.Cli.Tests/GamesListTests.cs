using System.Text.Json.Nodes;

namespace AiLocalPack.Cli.Tests;

public sealed class GamesListTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly SteamFixture _steam;

    public GamesListTests() => _steam = new SteamFixture(_dir);

    public void Dispose() => _dir.Dispose();

    private Task<CliResult> RunAsync(params string[] args) =>
        CliRunner.RunAsync(new Dictionary<string, string> { ["AILOCALPACK_STEAM_ROOT"] = _steam.Root }, args);

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
                ["appId"] = 413150,
                ["name"] = "Stardew Valley",
                ["buildId"] = "16826371",
                ["installPath"] = Path.Combine(_steam.Root, "steamapps", "common", "Stardew Valley"),
                ["libraryPath"] = _steam.Root,
                ["lastPlayed"] = "2027-01-15T08:00:00+00:00",
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
}
