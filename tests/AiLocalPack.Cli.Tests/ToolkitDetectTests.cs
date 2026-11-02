using System.Text.Json.Nodes;

namespace AiLocalPack.Cli.Tests;

public sealed class ToolkitDetectTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Text_shows_engine_confidence_marker_and_executable()
    {
        // Arrange
        _dir.UnityWindows(il2Cpp: true);

        // Act
        var result = await CliRunner.RunAsync("toolkit", "detect", _dir.Path);

        // Assert
        (result.ExitCode, result.StdOut.ReplaceLineEndings("\n")).Should().Be((0,
            "engine: Unity (IL2CPP)\nconfidence: high\nmarker: Game_Data/globalgamemanagers\nexecutable: Game.exe\nanti-cheat: none\n"));
    }

    [Fact]
    public async Task Json_has_engine_id_and_candidates()
    {
        // Arrange
        _dir.GodotWindows(major: 4);
        var expected = new JsonObject
        {
            ["engine"] = "godot:4",
            ["name"] = "Godot 4",
            ["confidence"] = "high",
            ["marker"] = "Game.pck",
            ["executable"] = "Game.exe",
            ["ambiguous"] = false,
            ["candidates"] = new JsonArray(new JsonObject
            {
                ["engine"] = "godot:4",
                ["name"] = "Godot 4",
                ["confidence"] = "high",
                ["marker"] = "Game.pck",
                ["executable"] = "Game.exe",
            }),
            ["antiCheats"] = new JsonArray(),
        };

        // Act
        var result = await CliRunner.RunAsync("toolkit", "detect", _dir.Path, "--json");

        // Assert
        JsonNode.DeepEquals(JsonNode.Parse(result.StdOut), expected).Should().BeTrue(result.StdOut);
    }

    [Fact]
    public async Task Empty_folder_prints_unknown()
    {
        // Arrange
        var folder = _dir.Dir("Empty").Combine("Empty");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "detect", folder);

        // Assert
        (result.ExitCode, result.StdOut.ReplaceLineEndings("\n")).Should().Be((0, "engine: unknown\nanti-cheat: none\n"));
    }

    [Fact]
    public async Task Ambiguous_folder_lists_the_candidates()
    {
        // Arrange
        _dir.UnityWindows().GodotWindows(name: "Other");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "detect", _dir.Path);

        // Assert
        result.StdOut.Should().ContainAll("engine: unknown (ambiguous)", "Unity (Mono)", "Godot 4");
    }

    [Fact]
    public async Task Text_shows_the_anti_cheat_and_its_marker()
    {
        // Arrange
        _dir.UnrealWindows().File("EasyAntiCheat/EasyAntiCheat_x64.dll", "MZ");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "detect", _dir.Path);

        // Assert
        result.StdOut.Should().Contain("anti-cheat: Easy Anti-Cheat (EasyAntiCheat/)");
    }

    [Fact]
    public async Task Json_lists_every_anti_cheat()
    {
        // Arrange
        _dir.Dir("EasyAntiCheat").File("Bin/BEClient_x64.dll", "MZ");
        var expected = new JsonArray(
            new JsonObject { ["id"] = "eac", ["name"] = "Easy Anti-Cheat", ["marker"] = "EasyAntiCheat/" },
            new JsonObject { ["id"] = "battleye", ["name"] = "BattlEye", ["marker"] = "Bin/BEClient_x64.dll" });

        // Act
        var result = await CliRunner.RunAsync("toolkit", "detect", _dir.Path, "--json");

        // Assert
        JsonNode.DeepEquals(JsonNode.Parse(result.StdOut)!["antiCheats"], expected).Should().BeTrue(result.StdOut);
    }

    [Fact]
    public async Task Missing_folder_fails_with_a_message()
    {
        // Arrange
        var missing = _dir.Combine("not-there");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "detect", missing);

        // Assert
        (result.ExitCode, result.StdErr.Contains("Folder not found", StringComparison.Ordinal)).Should().Be((1, true));
    }
}
