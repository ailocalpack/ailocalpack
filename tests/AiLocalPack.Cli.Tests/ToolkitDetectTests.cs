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
            "engine: Unity (IL2CPP)\nconfidence: high\nmarker: Game_Data/globalgamemanagers\nexecutable: Game.exe\n"));
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
        (result.ExitCode, result.StdOut.Trim()).Should().Be((0, "engine: unknown"));
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
