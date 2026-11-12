using System.Text.Json.Nodes;

namespace AiLocalPack.Cli.Tests;

public sealed class ToolkitScanFolderTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Text_lists_games_with_engine_executable_fingerprint_and_skipped_folders()
    {
        // Arrange
        _dir.UnityWindows("GOG/Moss and Mortar", name: "Moss").File("_CommonRedist/vcredist/vc_redist.x64.exe", "MZ");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "scan-folder", _dir.Path);

        // Assert
        result.StdOut.ReplaceLineEndings("\n").Should().MatchRegex(
            "^Found 1 game in .+:\nGOG/Moss and Mortar\n  name: Moss and Mortar\n  engine: Unity \\(Mono\\)\n  executable: Moss.exe\n"
            + "  fingerprint: fp:[0-9a-f]{64}\n  version: 3 files\nSkipped 1 folder:\n  _CommonRedist: redistributable\n$");
    }

    [Fact]
    public async Task Json_has_fingerprint_version_files_and_skip_reasons()
    {
        // Arrange
        _dir.GodotWindows("Wren", name: "Wren").File("Docs/manual.pdf", "x");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "scan-folder", _dir.Path, "--json");

        // Assert
        var json = JsonNode.Parse(result.StdOut)!;
        var game = json["games"]![0]!;
        (game["path"]!.GetValue<string>(), game["engine"]!.GetValue<string>(), game["executable"]!.GetValue<string>(),
                game["fingerprint"]!.GetValue<string>().StartsWith("fp:", StringComparison.Ordinal),
                game["version"]!.AsObject().Select(p => p.Key).ToArray(), game["inSteamLibrary"]!.GetValue<bool>(),
                json["skipped"]!.ToJsonString(), json["limitReached"]!.GetValue<bool>())
            .Should().BeEquivalentTo(("Wren", "godot:4", "Wren.exe", true, new[] { "Wren.exe", "Wren.pck" }, false,
                "[{\"path\":\"Docs\",\"reason\":\"no-game\"}]", false));
    }

    [Fact]
    public async Task Same_game_in_two_folders_prints_the_same_fingerprint()
    {
        // Arrange
        _dir.RpgMakerMz("A/Moss").RpgMakerMz("B/Moss");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "scan-folder", _dir.Path, "--json");

        // Assert
        var games = JsonNode.Parse(result.StdOut)!["games"]!.AsArray();
        games.Select(g => g!["fingerprint"]!.GetValue<string>()).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task Steam_library_games_are_marked()
    {
        // Arrange
        _dir.UnityWindows("steamapps/common/Moss");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "scan-folder", _dir.Path);

        // Assert
        result.StdOut.Should().Contain("steam: already in your library via Steam");
    }

    [Fact]
    public async Task A_folder_without_games_says_so()
    {
        // Arrange
        _dir.File("readme.txt", "x");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "scan-folder", _dir.Path);

        // Assert
        (result.ExitCode, result.StdOut).Should().Be((0, $"No games found in {_dir.Path}\n"));
    }

    [Fact]
    public async Task A_missing_folder_exits_with_1()
    {
        // Arrange
        var missing = _dir.Combine("missing");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "scan-folder", missing);

        // Assert
        (result.ExitCode, result.StdErr.Trim()).Should().Be((1, $"Folder not found: {missing}"));
    }
}
