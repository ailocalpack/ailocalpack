using System.Text.Json.Nodes;

namespace AiLocalPack.Cli.Tests;

public sealed class GenEstimateTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly SteamFixture _steam;

    public GenEstimateTests()
    {
        _steam = new SteamFixture(_dir);
        _steam.AddGame(620, "Sample Quest");
        _dir.RpgMakerGame("Steam/steamapps/common/Sample Quest");
    }

    public void Dispose() => _dir.Dispose();

    private string Home => _dir.Combine("home");

    private Task<CliResult> RunAsync(params string[] args) =>
        CliRunner.RunAsync(
            new Dictionary<string, string>
            {
                ["AILOCALPACK_STEAM_ROOT"] = _steam.Root,
                ["AILOCALPACK_HOME"] = Home,
            },
            args);

    [Fact]
    public async Task Estimate_prints_strings_copy_disk_time_and_limits()
    {
        // Arrange
        // (an RPG Maker MZ game in the Steam fixture)

        // Act
        var result = await RunAsync("gen", "estimate", "steam:620");

        // Assert
        result.Should().BeEquivalentTo(new { ExitCode = 0, StdErr = "" });
        result.StdOut.Split(Environment.NewLine).Select(l => l.Split(':')[0]).Should().StartWith(
            ["Sample Quest (steam", "strings", "copy", "disk", "agent time", "subscription", "tokens"]);
    }

    [Fact]
    public async Task Estimate_names_the_toolkit_that_read_the_strings()
    {
        // Arrange
        // (an RPG Maker MZ game in the Steam fixture)

        // Act
        var result = await RunAsync("gen", "estimate", "steam:620");

        // Assert
        result.StdOut.Should().StartWith("Sample Quest (steam:620), RPG Maker MZ: strings read by the toolkit (rpgmaker 1.0.0).");
    }

    [Fact]
    public async Task Estimate_as_JSON_has_the_counts()
    {
        // Arrange
        // (an RPG Maker MZ game in the Steam fixture)

        // Act
        var result = await RunAsync("gen", "estimate", "steam:620", "--json");

        // Assert
        var json = JsonNode.Parse(result.StdOut)!;
        new
        {
            Game = (string?)json["game"],
            Source = (string?)json["source"],
            Tool = (string?)json["tool"],
            Scope = (string?)json["copy"]!["scope"],
            HasStrings = (int)json["strings"]! > 0,
            HasTime = (long)json["estimate"]!["timeHighSeconds"]! > 0,
            HasFree = json["disk"]!["freeBytes"] is not null,
        }.Should().BeEquivalentTo(new
        {
            Game = "steam:620",
            Source = "toolkit",
            Tool = "rpgmaker",
            Scope = "without-media",
            HasStrings = true,
            HasTime = true,
            HasFree = true,
        });
    }

    [Fact]
    public async Task A_game_not_in_the_catalog_exits_1()
    {
        // Arrange
        // (only steam:620 is installed)

        // Act
        var result = await RunAsync("gen", "estimate", "steam:999");

        // Assert
        result.Should().BeEquivalentTo(new
        {
            ExitCode = 1,
            StdErr = "steam:999 is not in the game catalog: install it in Steam or add its folder in the app, then run 'games list'." + Environment.NewLine,
        });
    }

    [Fact]
    public async Task Copy_makes_the_working_copy_in_the_job_folder()
    {
        // Arrange
        // (an RPG Maker MZ game in the Steam fixture)

        // Act
        var result = await RunAsync("gen", "copy", "steam:620", "--job", "job1");

        // Assert
        var copy = Path.Combine(Home, "data", "work", "steam-620", "job1", "game");
        new { result.ExitCode, Copied = File.Exists(Path.Combine(copy, "data", "System.json")) }
            .Should().BeEquivalentTo(new { ExitCode = 0, Copied = true });
    }
}
