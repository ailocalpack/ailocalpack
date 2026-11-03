using System.Text.Json;
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
                new JsonObject { ["path"] = _steam.Root, ["available"] = true, ["addedByHand"] = false },
                new JsonObject { ["path"] = missing, ["available"] = false, ["addedByHand"] = false }),
            ["games"] = new JsonArray(new JsonObject
            {
                ["id"] = "steam:413150",
                ["source"] = "steam",
                ["appId"] = 413150,
                ["name"] = "Stardew Valley",
                ["buildId"] = "16826371",
                ["engine"] = "unknown",
                ["antiCheat"] = "none",
                ["blocked"] = false,
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
    public async Task Lists_games_of_a_library_folder_added_in_settings()
    {
        // Arrange
        var extra = _dir.Dir("Extra/steamapps").Combine("Extra");
        _steam.AddGame(413150, "Stardew Valley", library: extra);
        _dir.File("home/config/settings.json", JsonSerializer.Serialize(new { schemaVersion = 1, steamLibraryFolders = new[] { extra } }));

        // Act
        var result = await RunAsync("games", "list", "--json");

        // Assert
        Games(result).Should().ContainSingle().Which!["libraryPath"]!.GetValue<string>().Should().Be(extra);
        JsonNode.Parse(result.StdOut)!["libraries"]!.AsArray()
            .Should().ContainSingle(l => (bool)l!["addedByHand"]!);
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

    [Fact]
    public async Task Json_shows_the_detected_engine()
    {
        // Arrange
        _steam.AddGame(413150, "Stardew Valley");
        _dir.GodotWindows("Steam/steamapps/common/Stardew Valley", major: 4);

        // Act
        var result = await RunAsync("games", "list", "--json");

        // Assert
        Games(result).Should().ContainSingle().Which!["engine"]!.GetValue<string>().Should().Be("godot:4");
    }

    [Fact]
    public async Task Table_shows_the_engine_name()
    {
        // Arrange
        _steam.AddGame(413150, "Stardew Valley");
        _dir.RpgMakerMv("Steam/steamapps/common/Stardew Valley");

        // Act
        var result = await RunAsync("games", "list");

        // Assert
        result.StdOut.Should().Contain("RPG Maker MV");
    }

    [Fact]
    public async Task Engine_is_saved_in_the_catalog()
    {
        // Arrange
        _steam.AddGame(413150, "Stardew Valley");
        _dir.RenPy("Steam/steamapps/common/Stardew Valley");

        // Act
        await RunAsync("games", "list");

        // Assert
        File.ReadAllText(_dir.Combine("home/data/games.json")).Should().Contain("\"engine\": \"renpy\"");
    }

    [Fact]
    public async Task Json_marks_a_game_with_anti_cheat_as_blocked()
    {
        // Arrange
        _steam.AddGame(1172470, "Shooter");
        _dir.Dir("Steam/steamapps/common/Shooter/EasyAntiCheat");

        // Act
        var result = await RunAsync("games", "list", "--json");

        // Assert
        var game = Games(result).Should().ContainSingle().Which!;
        (game["antiCheat"]!.GetValue<string>(), game["blocked"]!.GetValue<bool>()).Should().Be(("eac", true));
    }

    [Fact]
    public async Task Table_shows_the_anti_cheat_name()
    {
        // Arrange
        _steam.AddGame(1172470, "Shooter");
        _dir.Dir("Steam/steamapps/common/Shooter/BattlEye");

        // Act
        var result = await RunAsync("games", "list");

        // Assert
        result.StdOut.Should().ContainAll("ANTI-CHEAT", "BattlEye");
    }
}
