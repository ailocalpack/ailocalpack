using System.Text.Json.Nodes;
using AiLocalPack.Core.Packs;
using AiLocalPack.Toolkit.Recipes.Unreal;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;

namespace AiLocalPack.Cli.Tests;

/// <summary><c>toolkit extract</c>, <c>toolkit rebuild</c> and <c>dev fixture rpgmaker</c> / <c>renpy</c> / <c>godot</c> / <c>unity</c> / <c>unreal</c> (tasks 28, 30, 35, 36, 37).</summary>
public sealed class ToolkitRecipeTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Game => _dir.Combine("game");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unreal_fixture_extracts_with_the_unreal_tool(bool mac)
    {
        // Arrange
        await CreateUnrealFixtureAsync(mac);
        var tablePath = _dir.Combine("unreal-strings.json");

        // Act
        var extract = await CliRunner.RunAsync("toolkit", "extract", Game, "--out", tablePath, "--lang", "ru");

        // Assert
        (extract.ExitCode, JsonNode.Parse(File.ReadAllText(tablePath))!["tool"]!.GetValue<string>()).Should().Be((0, "unreal"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unreal_rebuild_writes_a_mod_pak(bool mac)
    {
        // Arrange
        await CreateUnrealFixtureAsync(mac);
        var tablePath = await TranslatedUnrealTableAsync();
        var output = _dir.Combine("unreal-output");

        // Act
        var rebuild = await CliRunner.RunAsync("toolkit", "rebuild", Game, "--strings", tablePath, "--out", output);

        // Assert
        var mod = (mac ? "Sample.app/Contents/UE4/" : "") + UnrealFixture.Paks + "/~mods/" + UnrealRecipe.ModName;
        (rebuild.ExitCode, rebuild.StdOut.Contains("Rebuilt 1 files (1 strings translated)", StringComparison.Ordinal), File.Exists(Path.Combine(output, mod)))
            .Should().Be((0, true, true));
    }

    [Fact]
    public async Task Unreal_rebuild_over_the_native_culture_says_which_language_to_choose_in_game()
    {
        // Arrange
        await CreateUnrealFixtureAsync(mac: false);
        var tablePath = await TranslatedUnrealTableAsync();

        // Act
        var rebuild = await CliRunner.RunAsync("toolkit", "rebuild", Game, "--strings", tablePath, "--out", _dir.Combine("unreal-output"));

        // Assert
        rebuild.StdOut.Should().Contain("in game: choose en; the game has no ru of its own");
    }

    [Fact]
    public async Task Unreal_rebuild_refuses_localization_inside_IoStore()
    {
        // Arrange
        UnrealFixture.Create(Game);
        var paks = Path.Combine(Game, UnrealFixture.Paks);
        Dictionary<string, byte[]> files;
        using (var provider = new DefaultFileProvider(paks, SearchOption.TopDirectoryOnly, new VersionContainer(EGame.GAME_UE4_27)))
        {
            provider.Initialize();
            provider.Mount();
            files = provider.Files.ToDictionary(p => p.Key, p => p.Value.Read(), StringComparer.Ordinal);
        }

        File.Delete(Path.Combine(Game, UnrealFixture.Pak));
        IoStoreFixture.Write(Path.Combine(paks, "pakchunk1.utoc"), files, partitioned: true, compression: "Zlib");
        var tablePath = await TranslatedUnrealTableAsync();

        // Act
        var rebuild = await CliRunner.RunAsync("toolkit", "rebuild", Game, "--strings", tablePath, "--out", _dir.Combine("output"));

        // Assert
        (rebuild.ExitCode, rebuild.StdErr.Contains("inside IoStore containers", StringComparison.Ordinal), Directory.Exists(_dir.Combine("output")))
            .Should().Be((1, true, false));
    }

    [Fact]
    public async Task Unreal_fixture_refuses_a_future_locres_version()
    {
        // Act
        var result = await CliRunner.RunAsync("dev", "fixture", "unreal", Game, "--locres-version", "4");

        // Assert
        (result.ExitCode, result.StdErr.Contains("must be 0, 1, 2 or 3", StringComparison.Ordinal), Directory.Exists(Game)).Should().Be((1, true, false));
    }

    [Fact]
    public async Task Unreal_fixture_refuses_a_folder_that_is_not_empty()
    {
        // Arrange
        await CreateUnrealFixtureAsync(mac: false);

        // Act
        var result = await CliRunner.RunAsync("dev", "fixture", "unreal", Game);

        // Assert
        (result.ExitCode, result.StdErr.Contains("isn't an empty folder", StringComparison.Ordinal)).Should().Be((1, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unity_fixture_extracts_with_the_unity_tool(bool bundled)
    {
        // Arrange
        await CreateUnityFixtureAsync(bundled);
        var tablePath = _dir.Combine("unity-strings.json");

        // Act
        var extract = await CliRunner.RunAsync("toolkit", "extract", Game, "--out", tablePath, "--lang", "ru");

        // Assert
        (extract.ExitCode, JsonNode.Parse(File.ReadAllText(tablePath))!["tool"]!.GetValue<string>()).Should().Be((0, "unity"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unity_rebuild_reads_back_as_the_translation(bool bundled)
    {
        // Arrange
        await CreateUnityFixtureAsync(bundled);
        var tablePath = _dir.Combine("unity-strings.json");
        await CliRunner.RunAsync("toolkit", "extract", Game, "--out", tablePath, "--lang", "ru");
        var json = JsonNode.Parse(File.ReadAllText(tablePath))!;
        var strings = json["strings"]!.AsObject();
        var key = strings.First().Key;
        strings[key] = "Привет!";
        File.WriteAllText(tablePath, json.ToJsonString());
        var output = _dir.Combine("unity-output");
        await CliRunner.RunAsync("toolkit", "rebuild", Game, "--strings", tablePath, "--out", output);
        _dir.CopyFolder(Game, "unity-rebuilt").CopyFolder(output, "unity-rebuilt");

        // Act
        var reread = await CliRunner.RunAsync("toolkit", "extract", _dir.Combine("unity-rebuilt"), "--out", tablePath);

        // Assert
        (reread.ExitCode, StringTableSerializer.Read(File.ReadAllBytes(tablePath)).Value!.Strings[key]).Should().Be((0, "Привет!"));
    }

    [Fact]
    public async Task Unity_rebuild_reports_the_changed_file()
    {
        // Arrange
        await CreateUnityFixtureAsync(bundled: false);
        var tablePath = _dir.Combine("unity-strings.json");
        await CliRunner.RunAsync("toolkit", "extract", Game, "--out", tablePath, "--lang", "ru");
        var json = JsonNode.Parse(File.ReadAllText(tablePath))!;
        var strings = json["strings"]!.AsObject();
        strings[strings.First().Key] = "Привет!";
        File.WriteAllText(tablePath, json.ToJsonString());

        // Act
        var rebuild = await CliRunner.RunAsync("toolkit", "rebuild", Game, "--strings", tablePath, "--out", _dir.Combine("unity-output"));

        // Assert
        (rebuild.ExitCode, rebuild.StdOut).Should().Match<(int Code, string Out)>(r => r.Code == 0 && r.Out.Contains("Rebuilt 1 files (1 strings translated)"));
    }

    [Fact]
    public async Task Extract_writes_a_string_table_of_the_originals()
    {
        // Arrange
        _dir.RpgMakerGame("game", mv: true);
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
        _dir.RpgMakerGame("game", mv: true);
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
    public async Task Extract_explains_when_a_Unity_game_has_no_supported_texts()
    {
        // Arrange
        _dir.UnityWindows("game");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "extract", Game, "--out", _dir.Combine("strings.json"));

        // Assert
        (result.ExitCode, result.StdErr.Contains("No supported Unity texts found", StringComparison.Ordinal)).Should().Be((1, true));
    }

    [Fact]
    public async Task Extract_refuses_a_game_of_an_unknown_engine()
    {
        // Arrange
        Directory.CreateDirectory(Game);
        File.WriteAllText(Path.Combine(Game, "readme.txt"), "not a game");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "extract", Game, "--out", _dir.Combine("strings.json"));

        // Assert
        (result.ExitCode, result.StdErr.Contains("the engine is unknown", StringComparison.Ordinal)).Should().Be((1, true));
    }

    private async Task CreateUnrealFixtureAsync(bool mac)
    {
        var create = mac
            ? await CliRunner.RunAsync("dev", "fixture", "unreal", Game, "--mac-bundle", "--locres-version", "0")
            : await CliRunner.RunAsync("dev", "fixture", "unreal", Game);
        create.ExitCode.Should().Be(0);
    }

    /// <summary>The game's strings with «Play» translated, as a string table file.</summary>
    private async Task<string> TranslatedUnrealTableAsync()
    {
        var tablePath = _dir.Combine("unreal-strings.json");
        (await CliRunner.RunAsync("toolkit", "extract", Game, "--out", tablePath, "--lang", "ru")).ExitCode.Should().Be(0);
        var json = JsonNode.Parse(File.ReadAllText(tablePath))!;
        var strings = json["strings"]!.AsObject();
        strings[strings.Single(p => p.Value!.GetValue<string>() == "Play").Key] = "Играть";
        File.WriteAllText(tablePath, json.ToJsonString());
        return tablePath;
    }

    private async Task CreateUnityFixtureAsync(bool bundled)
    {
        var create = bundled
            ? await CliRunner.RunAsync("dev", "fixture", "unity", Game, "--il2cpp", "--mac-bundle", "--bundled")
            : await CliRunner.RunAsync("dev", "fixture", "unity", Game);
        create.ExitCode.Should().Be(0);
    }

    public static bool IsMacOS => OperatingSystem.IsMacOS();

    [Fact(Skip = "RPG Maker is switched off on macOS only", SkipUnless = nameof(IsMacOS))]
    public async Task Extract_leaves_an_rpg_maker_game_alone_on_macos()
    {
        // Arrange
        _dir.RpgMakerGame("game", mv: true);

        // Act
        var result = await CliRunner.RunAsync(
            new Dictionary<string, string> { ["AILOCALPACK_TOOLKIT_ALL_ENGINES"] = "0" }, "toolkit", "extract", Game, "--out", _dir.Combine("strings.json"));

        // Assert
        (result.ExitCode, result.StdErr.Trim(), File.Exists(_dir.Combine("strings.json"))).Should().Be((
            1, $"{Game}: the toolkit doesn't handle RPG Maker MV games on this system for now; a translation has to be made like for an unknown engine.", false));
    }

    [Fact]
    public async Task Extract_refuses_an_unknown_language_tag()
    {
        // Arrange
        _dir.RpgMakerGame("game", mv: true);

        // Act
        var result = await CliRunner.RunAsync("toolkit", "extract", Game, "--out", _dir.Combine("strings.json"), "--lang", "xx");

        // Assert
        (result.ExitCode, File.Exists(_dir.Combine("strings.json"))).Should().Be((1, false));
    }

    [Fact]
    public async Task An_edited_extract_rebuilds_the_changed_files_only()
    {
        // Arrange
        _dir.RpgMakerGame("game", mv: true);
        var strings = _dir.Combine("strings.json");
        await CliRunner.RunAsync("toolkit", "extract", Game, "--out", strings);
        var json = JsonNode.Parse(File.ReadAllText(strings))!;
        json["strings"]!["data/Items.json:/1/name"] = "Зелье";
        File.WriteAllText(strings, json.ToJsonString());

        // Act
        var result = await CliRunner.RunAsync("toolkit", "rebuild", Game, "--strings", strings, "--out", _dir.Combine("out"), "--no-fonts");

        // Assert
        (result.ExitCode, result.StdOut.ReplaceLineEndings("\n"), Directory.GetFiles(_dir.Combine("out"), "*", SearchOption.AllDirectories).Length)
            .Should().Be((0, $"Rebuilt 1 files (1 strings translated) into {_dir.Combine("out")}.\n  www/data/Items.json\n", 1));
    }

    [Fact]
    public async Task Rebuild_swaps_in_an_open_font_when_the_game_font_lacks_the_letters()
    {
        // Arrange
        _dir.RpgMakerGame("game", mv: true);
        var strings = WriteTable("ru", ("data/Items.json:/1/name", "Зелье"));

        // Act
        var result = await CliRunner.RunAsync("toolkit", "rebuild", Game, "--strings", strings, "--out", _dir.Combine("out"));

        // Assert
        (result.ExitCode, result.StdOut.ReplaceLineEndings("\n")).Should().Be((0, $"""
            Rebuilt 3 files (1 strings translated) into {_dir.Combine("out")}.
              www/data/Items.json
              www/fonts/ailocalpack-NotoSans-Regular.ttf
              www/fonts/gamefont.css
            fonts: fixture-sans.ttf swapped for Noto Sans (noto-sans).

            """));
    }

    [Fact]
    public async Task Rebuild_says_when_the_game_fonts_have_every_letter()
    {
        // Arrange
        _dir.RpgMakerGame("game", mv: true);
        var strings = WriteTable("fr", ("data/Items.json:/1/name", "Potion de soin"));

        // Act
        var result = await CliRunner.RunAsync("toolkit", "rebuild", Game, "--strings", strings, "--out", _dir.Combine("out"));

        // Assert
        result.StdOut.ReplaceLineEndings("\n").Should().EndWith("  www/data/Items.json\nfonts: the game's fonts have every letter of the translations.\n");
    }

    [Fact]
    public async Task Fonts_json_names_the_font_a_pack_needs()
    {
        // Arrange
        _dir.RenPyGame("game");
        var strings = WriteTable("ru", ("string:0000000000000000", "Привет"));

        // Act
        var result = await CliRunner.RunAsync("toolkit", "fonts", Game, "--strings", strings, "--json");

        // Assert
        var json = JsonNode.Parse(result.StdOut)!;
        (result.ExitCode,
            json["fonts"]!.AsArray().Select(f => (f!["name"]!.GetValue<string>(), f["needsSwap"]!.GetValue<bool>())).ToList(),
            json["manifestFonts"]!.AsArray().Select(f => f!.GetValue<string>()).ToList(),
            json["replacement"]!.GetValue<string>())
            .Should().BeEquivalentTo((0, new List<(string, bool)> { ("DejaVuSans.ttf", false), ("gui/font/FixtureSans.ttf", true) }, new List<string> { "noto-sans" }, "noto-sans"));
    }

    [Fact]
    public async Task Fonts_prints_each_game_font_and_what_it_lacks()
    {
        // Arrange
        _dir.RpgMakerGame("game", mv: true);
        var strings = WriteTable("ru", ("data/Items.json:/1/name", "Зелье"));

        // Act
        var result = await CliRunner.RunAsync("toolkit", "fonts", Game, "--strings", strings);

        // Assert
        result.StdOut.ReplaceLineEndings("\n").Should().Be("""
            The ru translations add 4 letters and 0 symbols to the game's text; 1 game fonts.
              fixture-sans.ttf "Fixture Sans" (www/fonts/fixture-sans.ttf): lacks 4 letters (З, е, л, ь)
            fonts: fixture-sans.ttf swapped for Noto Sans (noto-sans).
            pack fonts: noto-sans

            """);
    }

    private string WriteTable(string language, params (string Key, string Text)[] strings)
    {
        var path = _dir.Combine("strings.json.gz");
        using var stream = File.Create(path);
        StringTableSerializer.Write(new StringTable(language, strings.ToDictionary(s => s.Key, s => s.Text)), stream);
        return path;
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
        _dir.RpgMakerGame("game", mv: true).File("strings.json", "{\"schemaVersion\": 1}");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "rebuild", Game, "--strings", _dir.Combine("strings.json"), "--out", _dir.Combine("out"));

        // Assert
        (result.ExitCode, result.StdErr).Should().Match<(int, string)>(r => r.Item1 == 1 && r.Item2.Contains("is not a valid string table", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rebuild_refuses_to_write_into_the_game_folder()
    {
        // Arrange
        _dir.RpgMakerGame("game", mv: true).File("strings.json", "{\"schemaVersion\": 1, \"language\": \"ru\", \"strings\": {\"data/System.json:/gameTitle\": \"Квест\"}}");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "rebuild", Game, "--strings", _dir.Combine("strings.json"), "--out", Game);

        // Assert
        (result.ExitCode, File.ReadAllText(_dir.Combine("game/www/data/System.json")).Contains("Fixture Quest", StringComparison.Ordinal)).Should().Be((1, true));
    }

    [Fact]
    public async Task Rebuild_reports_a_broken_data_file()
    {
        // Arrange
        _dir.RpgMakerGame("game", mv: true)
            .File("game/www/data/System.json", "{")
            .File("strings.json", "{\"schemaVersion\": 1, \"language\": \"ru\", \"strings\": {\"data/System.json:/gameTitle\": \"Квест\"}}");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "rebuild", Game, "--strings", _dir.Combine("strings.json"), "--out", _dir.Combine("out"));

        // Assert
        (result.ExitCode, result.StdErr.StartsWith("www/data/System.json is not valid JSON", StringComparison.Ordinal)).Should().Be((1, true));
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

    [Fact]
    public async Task Dev_fixture_godot_writes_a_game_whose_strings_extract()
    {
        // Arrange
        await CliRunner.RunAsync("dev", "fixture", "godot", Game, "--major", "4", "--embedded");
        var target = _dir.Combine("strings.json");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "extract", Game, "--out", target);

        // Assert
        (result.StdOut.Trim(), StringTableSerializer.Read(File.ReadAllBytes(target)).Value!.Strings["tr:MENU_PLAY"]).Should().Be((
            $"Extracted 8 strings from 1 files of Godot 4 (godot 1.0.0) into {target}.",
            "Play"));
    }

    [Fact]
    public async Task Dev_fixture_godot_refuses_another_major_version()
    {
        // Act
        var result = await CliRunner.RunAsync("dev", "fixture", "godot", Game, "--major", "2");

        // Assert
        (result.ExitCode, result.StdErr.Trim()).Should().Be((1, "--major must be 3 or 4, not 2."));
    }

    [Fact]
    public async Task Rebuild_of_a_Godot_game_writes_the_whole_pack()
    {
        // Arrange
        _dir.GodotGame("game").File("strings.json", "{\"schemaVersion\": 1, \"language\": \"ru\", \"strings\": {\"tr:MENU_PLAY\": \"Играть\"}}");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "rebuild", Game, "--strings", _dir.Combine("strings.json"), "--out", _dir.Combine("out"));

        // Assert
        (result.ExitCode, result.StdOut.Contains("Rebuilt 1 files (1 strings translated)", StringComparison.Ordinal), File.Exists(_dir.Combine("out/Game.pck")))
            .Should().Be((0, true, true));
    }
}
