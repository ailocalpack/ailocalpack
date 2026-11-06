using System.Text.Json.Nodes;
using AiLocalPack.Core.Packs;

namespace AiLocalPack.Cli.Tests;

/// <summary><c>toolkit extract</c>, <c>toolkit rebuild</c> and <c>dev fixture rpgmaker</c> / <c>renpy</c> (tasks 28, 30).</summary>
public sealed class ToolkitRecipeTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Game => _dir.Combine("game");

    [Fact]
    public async Task Extract_writes_a_string_table_of_the_originals()
    {
        // Arrange
        _dir.RpgMakerGame("game");
        var target = _dir.Combine("strings.json");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "extract", Game, "--out", target);

        // Assert
        var table = StringTableSerializer.Read(File.ReadAllBytes(target)).Value!;
        (result.ExitCode, table.Language, table.Strings["data/Map001.json:/displayName"], table.Context["data/Map001.json:/displayName"])
            .Should().Be((0, "en", "Village of Ash", "Map name shown on entering the map"));
    }

    [Fact]
    public async Task Extract_writes_tool_files_and_placeholders()
    {
        // Arrange
        _dir.RpgMakerGame("game");
        var target = _dir.Combine("strings.json");

        // Act
        await CliRunner.RunAsync("toolkit", "extract", Game, "--out", target, "--lang", "ja");

        // Assert
        var json = JsonNode.Parse(File.ReadAllText(target))!;
        (json["language"]!.GetValue<string>(), json["tool"]!.GetValue<string>(), json["toolVersion"]!.GetValue<string>(),
            json["files"]!.AsArray().Count, json["details"]!["data/Items.json:/2/description"]!["placeholders"]!.ToJsonString())
            .Should().Be(("ja", "rpgmaker", "1.0.0", 12, """["\\C[2]","\\C[0]"]"""));
    }

    [Fact]
    public async Task Extract_prints_a_summary()
    {
        // Arrange
        _dir.RpgMakerGame("game", mv: true);
        var target = _dir.Combine("strings.json");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "extract", Game, "--out", target);

        // Assert
        result.StdOut.Trim().Should().Be($"Extracted 64 strings from 12 files of RPG Maker MV (rpgmaker 1.0.0) into {target}.");
    }

    [Fact]
    public async Task Extract_refuses_a_game_the_toolkit_cannot_read_yet()
    {
        // Arrange
        _dir.UnityWindows("game");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "extract", Game, "--out", _dir.Combine("strings.json"));

        // Assert
        (result.ExitCode, result.StdErr.Trim()).Should().Be((1, $"{Game}: the toolkit can't read Unity (Mono) games yet."));
    }

    [Fact]
    public async Task Extract_refuses_an_unknown_language_tag()
    {
        // Arrange
        _dir.RpgMakerGame("game");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "extract", Game, "--out", _dir.Combine("strings.json"), "--lang", "xx");

        // Assert
        (result.ExitCode, File.Exists(_dir.Combine("strings.json"))).Should().Be((1, false));
    }

    [Fact]
    public async Task An_edited_extract_rebuilds_the_changed_files_only()
    {
        // Arrange
        _dir.RpgMakerGame("game");
        var strings = _dir.Combine("strings.json");
        await CliRunner.RunAsync("toolkit", "extract", Game, "--out", strings);
        var json = JsonNode.Parse(File.ReadAllText(strings))!;
        json["strings"]!["data/Items.json:/1/name"] = "Зелье";
        File.WriteAllText(strings, json.ToJsonString());

        // Act
        var result = await CliRunner.RunAsync("toolkit", "rebuild", Game, "--strings", strings, "--out", _dir.Combine("out"));

        // Assert
        (result.ExitCode, result.StdOut.ReplaceLineEndings("\n"), Directory.GetFiles(_dir.Combine("out"), "*", SearchOption.AllDirectories).Length)
            .Should().Be((0, $"Rebuilt 1 files (1 strings translated) into {_dir.Combine("out")}.\n  data/Items.json\n", 1));
    }

    [Fact]
    public async Task Rebuild_reads_a_gzip_pack_string_table_and_warns_about_unknown_keys()
    {
        // Arrange
        _dir.RpgMakerGame("game", mv: true);
        var strings = _dir.Combine("strings.json.gz");
        using (var stream = File.Create(strings))
        {
            StringTableSerializer.Write(new StringTable("ru", new Dictionary<string, string>
            {
                ["data/System.json:/gameTitle"] = "Квест",
                ["data/Gone.json:/1/name"] = "x",
            }), stream);
        }

        // Act
        var result = await CliRunner.RunAsync("toolkit", "rebuild", Game, "--strings", strings, "--out", _dir.Combine("out"));

        // Assert
        (result.ExitCode, result.StdErr.ReplaceLineEndings("\n"), File.ReadAllText(_dir.Combine("out/www/data/System.json")).Contains("\"Квест\"", StringComparison.Ordinal))
            .Should().Be((0, "warning: 1 keys are not in this game (left out):\n  data/Gone.json:/1/name\n", true));
    }

    [Fact]
    public async Task Rebuild_refuses_a_file_that_is_not_a_string_table()
    {
        // Arrange
        _dir.RpgMakerGame("game").File("strings.json", "{\"schemaVersion\": 1}");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "rebuild", Game, "--strings", _dir.Combine("strings.json"), "--out", _dir.Combine("out"));

        // Assert
        (result.ExitCode, result.StdErr).Should().Match<(int, string)>(r => r.Item1 == 1 && r.Item2.Contains("is not a valid string table", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rebuild_refuses_to_write_into_the_game_folder()
    {
        // Arrange
        _dir.RpgMakerGame("game").File("strings.json", "{\"schemaVersion\": 1, \"language\": \"ru\", \"strings\": {\"data/System.json:/gameTitle\": \"Квест\"}}");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "rebuild", Game, "--strings", _dir.Combine("strings.json"), "--out", Game);

        // Assert
        (result.ExitCode, File.ReadAllText(_dir.Combine("game/data/System.json")).Contains("Fixture Quest", StringComparison.Ordinal)).Should().Be((1, true));
    }

    [Fact]
    public async Task Rebuild_reports_a_broken_data_file()
    {
        // Arrange
        _dir.RpgMakerGame("game")
            .File("game/data/System.json", "{")
            .File("strings.json", "{\"schemaVersion\": 1, \"language\": \"ru\", \"strings\": {\"data/System.json:/gameTitle\": \"Квест\"}}");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "rebuild", Game, "--strings", _dir.Combine("strings.json"), "--out", _dir.Combine("out"));

        // Assert
        (result.ExitCode, result.StdErr.StartsWith("data/System.json is not valid JSON", StringComparison.Ordinal)).Should().Be((1, true));
    }

    [Fact]
    public async Task Dev_fixture_writes_a_game_the_toolkit_detects()
    {
        // Arrange
        await CliRunner.RunAsync("dev", "fixture", "rpgmaker", Game, "--mv");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "detect", Game);

        // Assert
        result.StdOut.Should().StartWith("engine: RPG Maker MV");
    }

    [Fact]
    public async Task Dev_fixture_refuses_a_folder_with_files()
    {
        // Arrange
        _dir.File("game/keep.txt", "mine");

        // Act
        var result = await CliRunner.RunAsync("dev", "fixture", "rpgmaker", Game);

        // Assert
        (result.ExitCode, Directory.GetFiles(Game).Length).Should().Be((1, 1));
    }

    [Fact]
    public async Task Dev_fixture_renpy_writes_a_game_whose_strings_extract()
    {
        // Arrange
        await CliRunner.RunAsync("dev", "fixture", "renpy", Game, "--archived");
        var target = _dir.Combine("strings.json");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "extract", Game, "--out", target);

        // Assert
        (result.StdOut.Trim(), StringTableSerializer.Read(File.ReadAllBytes(target)).Value!.Strings["say:start_915cb944"]).Should().Be((
            $"Extracted 21 strings from 1 files of Ren'Py (renpy 1.0.0) into {target}.",
            "It's only when I hear the sounds of shuffling feet and supplies being put away that I realize that the lecture's over."));
    }

    [Fact]
    public async Task Rebuild_of_a_Renpy_game_writes_translation_files_in_the_tables_language()
    {
        // Arrange
        _dir.RenPyGame("game").File("strings.json", "{\"schemaVersion\": 1, \"language\": \"ru\", \"strings\": {\"say:rightaway_cf214f74\": \"Привет! Как занятия?\"}}");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "rebuild", Game, "--strings", _dir.Combine("strings.json"), "--out", _dir.Combine("out"));

        // Assert
        (result.ExitCode, File.ReadAllText(_dir.Combine("out/game/tl/ailocalpack_ru/script.rpy")).Contains("    s \"Привет! Как занятия?\"\n", StringComparison.Ordinal))
            .Should().Be((0, true));
    }
}
