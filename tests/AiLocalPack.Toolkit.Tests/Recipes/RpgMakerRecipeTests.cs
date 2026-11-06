using System.Text;
using System.Text.Json.Nodes;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Recipes.RpgMaker;

namespace AiLocalPack.Toolkit.Tests.Recipes;

public sealed class RpgMakerRecipeTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly RpgMakerRecipe _recipe = new();

    public void Dispose() => _dir.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Game => _dir.Combine("game");

    private string Out => _dir.Combine("out");

    [Fact]
    public void Extract_reads_database_system_and_event_text()
    {
        // Arrange
        _dir.RpgMakerGame("game");

        // Act
        var extraction = _recipe.Extract(Game, Ct);

        // Assert
        extraction.Strings.Select(s => s.Key).Should().Contain(
        [
            "data/Actors.json:/1/profile",
            "data/Armors.json:/1/name",
            "data/Classes.json:/1/name",
            "data/CommonEvents.json:/1/list/4/parameters/0",
            "data/CommonEvents.json:/1/list/6/parameters/1",
            "data/Enemies.json:/1/name",
            "data/Items.json:/2/description",
            "data/Map001.json:/displayName",
            "data/Map001.json:/events/1/pages/0/list/0/parameters/4",
            "data/Map001.json:/events/1/pages/0/list/1/parameters/0",
            "data/Map001.json:/events/1/pages/0/list/3/parameters/0/1",
            "data/Map001.json:/events/1/pages/0/list/4/parameters/1",
            "data/Skills.json:/2/message1",
            "data/States.json:/1/message4",
            "data/System.json:/gameTitle",
            "data/System.json:/equipTypes/3",
            "data/System.json:/terms/commands/7",
            "data/System.json:/terms/messages/obtainGold",
            "data/Troops.json:/1/pages/0/list/1/parameters/0",
            "data/Weapons.json:/1/description",
        ]);
    }

    [Fact]
    public void Extract_counts_every_string_of_the_fixture()
    {
        // Arrange
        _dir.RpgMakerGame("game");

        // Act
        var extraction = _recipe.Extract(Game, Ct);

        // Assert
        (extraction.Strings.Count, extraction.Files.Count).Should().Be((64, 12));
    }

    [Fact]
    public void Extract_leaves_out_plugin_data_scripts_comments_notes_and_editor_names()
    {
        // Arrange
        _dir.RpgMakerGame("game");

        // Act
        var originals = _recipe.Extract(Game, Ct).Strings.Select(s => s.Original);

        // Assert
        originals.Should().NotContain(o => o.Contains("plugin", StringComparison.OrdinalIgnoreCase)
            || o.Contains("script", StringComparison.Ordinal)
            || o.Contains("Comment", StringComparison.Ordinal)
            || o.Contains("Popup", StringComparison.Ordinal)
            || o.Contains("Drink", StringComparison.Ordinal)
            || o.Contains("editor name", StringComparison.Ordinal)
            || o == "Slime*2"
            || o == "Actor1");
    }

    [Fact]
    public void Extract_skips_empty_and_null_strings()
    {
        // Arrange
        _dir.RpgMakerGame("game");

        // Act
        var originals = _recipe.Extract(Game, Ct).Strings.Select(s => s.Original);

        // Assert
        originals.Should().NotContain(o => string.IsNullOrWhiteSpace(o));
    }

    [Fact]
    public void Extract_lists_the_files_relative_to_the_game_folder()
    {
        // Arrange
        _dir.RpgMakerGame("game", mv: true);

        // Act
        var files = _recipe.Extract(Game, Ct).Files;

        // Assert
        files.Should().Contain(["www/data/Map001.json", "www/data/System.json"]).And.NotContain(f => f.EndsWith("MapInfos.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_notes_the_speaker_of_message_lines()
    {
        // Arrange
        _dir.RpgMakerGame("game");

        // Act
        var line = _recipe.Extract(Game, Ct).Strings.Single(s => s.Key == "data/Map001.json:/events/1/pages/0/list/1/parameters/0");

        // Assert
        line.Context.Should().Be("Message line, speaker: Elder");
    }

    [Fact]
    public void Extract_marks_escape_codes_as_placeholders()
    {
        // Arrange
        _dir.RpgMakerGame("game");

        // Act
        var line = _recipe.Extract(Game, Ct).Strings.Single(s => s.Key == "data/Map001.json:/events/1/pages/0/list/1/parameters/0");

        // Assert
        line.Placeholders.Should().Equal(@"\N[1]", @"\V[3]");
    }

    [Fact]
    public void Keys_are_the_same_on_every_run()
    {
        // Arrange
        _dir.RpgMakerGame("game");
        var first = _recipe.Extract(Game, Ct).Strings.Select(s => s.Key).ToList();

        // Act
        var second = _recipe.Extract(Game, Ct).Strings.Select(s => s.Key).ToList();

        // Assert
        second.Should().Equal(first);
    }

    [Fact]
    public void Keys_do_not_depend_on_the_order_files_were_written_in()
    {
        // Arrange
        _dir.RpgMakerGame("game");
        var forward = _recipe.Extract(Game, Ct).Strings.Select(s => s.Key).ToList();
        var source = Path.Combine(Game, "data");
        foreach (var file in Directory.GetFiles(source).OrderDescending(StringComparer.Ordinal))
        {
            _dir.File($"reversed/data/{Path.GetFileName(file)}", File.ReadAllBytes(file));
        }

        _dir.File("reversed/js/rmmz_core.js", "//").File("reversed/Game.exe", "MZ");

        // Act
        var reversed = _recipe.Extract(_dir.Combine("reversed"), Ct).Strings.Select(s => s.Key).ToList();

        // Assert
        reversed.Should().Equal(forward);
    }

    [Fact]
    public void Keys_are_ordered_ordinally()
    {
        // Arrange
        _dir.RpgMakerGame("game");

        // Act
        var keys = _recipe.Extract(Game, Ct).Strings.Select(s => s.Key);

        // Assert
        keys.Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public void Mv_and_mz_layouts_of_the_same_game_have_the_same_keys()
    {
        // Arrange
        _dir.RpgMakerGame("game").RpgMakerGame("mv", mv: true);
        var mz = _recipe.Extract(Game, Ct).Strings.Select(s => s.Key).ToList();

        // Act
        var mv = _recipe.Extract(_dir.Combine("mv"), Ct).Strings.Select(s => s.Key).ToList();

        // Assert
        mv.Should().Equal(mz);
    }

    [Fact]
    public void A_macos_bundle_has_the_same_keys_as_the_windows_build()
    {
        // Arrange
        _dir.RpgMakerGame("game")
            .RpgMakerGame("mac/Game.app/Contents/Resources/app.nw")
            .File("mac/Game.app/Contents/MacOS/nwjs", "macho")
            .File("mac/Game.app/Contents/Info.plist", "<plist/>");
        var windows = _recipe.Extract(Game, Ct).Strings.Select(s => s.Key).ToList();

        // Act
        var mac = _recipe.Extract(_dir.Combine("mac"), Ct);

        // Assert
        (mac.Strings.Select(s => s.Key).ToList(), mac.Files[0]).Should().BeEquivalentTo(
            (windows, "Game.app/Contents/Resources/app.nw/data/Actors.json"), o => o.WithStrictOrdering());
    }

    [Fact]
    public void Identity_translations_change_no_file()
    {
        // Arrange
        _dir.RpgMakerGame("game");
        var identity = _recipe.Extract(Game, Ct).Strings.ToDictionary(s => s.Key, s => s.Original);

        // Act
        var result = _recipe.Rebuild(Game, identity, new DirectoryRebuildOutput(Out), Ct);

        // Assert
        (result.ChangedFiles.Count, result.Applied, Directory.Exists(Out)).Should().Be((0, 0, false));
    }

    [Fact]
    public void Writing_every_string_back_unchanged_gives_semantically_equal_json()
    {
        // Arrange
        _dir.RpgMakerGame("game");
        var extraction = _recipe.Extract(Game, Ct);
        var byFile = extraction.Strings.GroupBy(s => s.Key[..s.Key.IndexOf(":/", StringComparison.Ordinal)]);
        var inputs = new Dictionary<string, JsonNode?>();
        var outputs = new Dictionary<string, JsonNode?>();

        // Act
        foreach (var group in byFile)
        {
            var bytes = File.ReadAllBytes(Path.Combine(Game, group.Key));
            var identity = group.ToDictionary(s => s.Key[(group.Key.Length + 1)..], s => s.Original);
            inputs[group.Key] = JsonNode.Parse(bytes);
            outputs[group.Key] = JsonNode.Parse(JsonStrings.Replace(bytes, identity));
        }

        // Assert
        outputs.Should().HaveCount(12).And.AllSatisfy(o => JsonNode.DeepEquals(o.Value, inputs[o.Key]).Should().BeTrue(o.Key));
    }

    [Fact]
    public void Rebuild_puts_translations_in_place_and_keeps_the_rest_of_the_file_byte_for_byte()
    {
        // Arrange
        _dir.RpgMakerGame("game");
        var original = File.ReadAllText(Path.Combine(Game, "data/Map001.json"));
        var translations = new Dictionary<string, string>
        {
            ["data/Map001.json:/displayName"] = "Пепельная деревня",
            ["data/Map001.json:/events/1/pages/0/list/2/parameters/0"] = "Дорога на \"север\" закрыта.\\|",
        };

        // Act
        _recipe.Rebuild(Game, translations, new DirectoryRebuildOutput(Out), Ct);

        // Assert
        File.ReadAllText(Path.Combine(Out, "data/Map001.json")).Should().Be(original
            .Replace("\"Village of Ash\"", "\"Пепельная деревня\"", StringComparison.Ordinal)
            .Replace("\"The road north is closed.\\\\|\"", "\"Дорога на \\\"север\\\" закрыта.\\\\|\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Rebuilt_files_read_back_with_every_translation()
    {
        // Arrange
        _dir.RpgMakerGame("game");
        var translations = _recipe.Extract(Game, Ct).Strings.ToDictionary(s => s.Key, s => $"[ru] {s.Original}\n«ok»");
        _recipe.Rebuild(Game, translations, new DirectoryRebuildOutput(Out), Ct);
        CopyInto(Out, Game);

        // Act
        var reread = _recipe.Extract(Game, Ct).Strings.ToDictionary(s => s.Key, s => s.Original);

        // Assert
        reread.Should().Equal(translations);
    }

    [Fact]
    public void Rebuild_reports_the_changed_files_and_the_applied_count()
    {
        // Arrange
        _dir.RpgMakerGame("game", mv: true);
        var translations = new Dictionary<string, string>
        {
            ["data/System.json:/gameTitle"] = "Квест",
            ["data/System.json:/currencyUnit"] = "G",
            ["data/Items.json:/1/name"] = "Зелье",
            ["data/Items.json:/1/description"] = "",
        };

        // Act
        var result = _recipe.Rebuild(Game, translations, new DirectoryRebuildOutput(Out), Ct);

        // Assert
        (result.ChangedFiles, result.Applied, result.UnknownKeys).Should().BeEquivalentTo(
            (new[] { "www/data/Items.json", "www/data/System.json" }, 2, Array.Empty<string>()), o => o.WithStrictOrdering());
    }

    [Fact]
    public void Rebuild_refuses_keys_the_extractor_does_not_give()
    {
        // Arrange
        _dir.RpgMakerGame("game");
        var translations = new Dictionary<string, string>
        {
            ["data/CommonEvents.json:/1/list/1/parameters/0"] = "alert('script from a pack');",
            ["data/CommonEvents.json:/1/list/2/parameters/0"] = "PluginCommand",
            ["data/Actors.json:/1/note"] = "<Plugin: changed>",
            ["data/Missing.json:/1/name"] = "x",
            ["data/Map001.json:/events/9/name"] = "x",
            ["no pointer"] = "x",
        };

        // Act
        var result = _recipe.Rebuild(Game, translations, new DirectoryRebuildOutput(Out), Ct);

        // Assert
        (result.ChangedFiles.Count, result.UnknownKeys.Count, Directory.Exists(Out)).Should().Be((0, 6, false));
    }

    [Fact]
    public void Rebuild_keeps_a_byte_order_mark()
    {
        // Arrange
        _dir.RpgMakerGame("game");
        var path = Path.Combine(Game, "data/Enemies.json");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. File.ReadAllBytes(path)]);

        // Act
        _recipe.Rebuild(Game, new Dictionary<string, string> { ["data/Enemies.json:/1/name"] = "Слизень" }, new DirectoryRebuildOutput(Out), Ct);

        // Assert
        var rebuilt = File.ReadAllBytes(Path.Combine(Out, "data/Enemies.json"));
        (rebuilt[..3], Encoding.UTF8.GetString(rebuilt.AsSpan(3)).Contains("\"Слизень\"", StringComparison.Ordinal))
            .Should().BeEquivalentTo((new byte[] { 0xEF, 0xBB, 0xBF }, true));
    }

    [Fact]
    public void Rebuild_never_writes_into_the_game_folder()
    {
        // Arrange
        _dir.RpgMakerGame("game");
        var before = File.ReadAllBytes(Path.Combine(Game, "data/System.json"));

        // Act
        _recipe.Rebuild(Game, new Dictionary<string, string> { ["data/System.json:/gameTitle"] = "Квест" }, new DirectoryRebuildOutput(Out), Ct);

        // Assert
        File.ReadAllBytes(Path.Combine(Game, "data/System.json")).Should().Equal(before);
    }

    [Fact]
    public void Touched_files_are_the_game_files_holding_the_keys()
    {
        // Arrange
        _dir.RpgMakerGame("game", mv: true);

        // Act
        var files = _recipe.TouchedFiles(Game, ["data/Map001.json:/displayName", "data/Map001.json:/events/2/pages/0/list/0/parameters/0", "data/System.json:/gameTitle", "data/Map999.json:/displayName"]);

        // Assert
        files.Should().Equal("www/data/Map001.json", "www/data/System.json");
    }

    [Fact]
    public void A_folder_of_another_engine_is_refused()
    {
        // Arrange
        _dir.RenPy("game");

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().WithMessage("*is not an RPG Maker MV / MZ game (found: Ren'Py)*");
    }

    [Fact]
    public void A_missing_folder_is_refused()
    {
        // Arrange
        var missing = _dir.Combine("nothing");

        // Act
        var act = () => _recipe.Extract(missing, Ct);

        // Assert
        act.Should().Throw<RecipeException>().WithMessage("Folder not found: *");
    }

    [Fact]
    public void A_broken_data_file_is_reported_with_its_path()
    {
        // Arrange
        _dir.RpgMakerGame("game", mv: true).File("game/www/data/Map001.json", "{\"displayName\": ");

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().Which.File.Should().Be("www/data/Map001.json");
    }

    [Fact]
    public void A_data_file_of_unexpected_shape_gives_no_strings()
    {
        // Arrange
        _dir.RpgMakerGame("game").File("game/data/Items.json", "{\"name\": \"not an array\"}").File("game/data/System.json", "[1, 2]");

        // Act
        var keys = _recipe.Extract(Game, Ct).Strings.Select(s => s.Key);

        // Assert
        keys.Should().NotContain(k => k.StartsWith("data/Items.json", StringComparison.Ordinal) || k.StartsWith("data/System.json", StringComparison.Ordinal));
    }

    private static void CopyInto(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
        }
    }
}
