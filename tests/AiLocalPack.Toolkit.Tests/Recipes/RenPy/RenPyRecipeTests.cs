using System.Text;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Recipes.RenPy;

namespace AiLocalPack.Toolkit.Tests.Recipes.RenPy;

/// <summary>The <c>renpy</c> recipe tool on the synthetic Ren'Py game (task 30).</summary>
public sealed class RenPyRecipeTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly RenPyRecipe _recipe = new();

    public void Dispose() => _dir.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Game => _dir.Combine("game");

    private string Out => _dir.Combine("out");

    private static string Key(string text) =>
        "string:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    private Dictionary<string, string> Originals() => _recipe.Extract(Game, Ct).Strings.ToDictionary(s => s.Key, s => s.Original);

    private string Written(string path) => File.ReadAllText(Path.Combine(Out, path));

    [Fact]
    public void Extract_gives_dialogue_the_identifiers_Renpy_generates()
    {
        // Arrange
        _dir.RenPyGame("game");

        // Act
        var originals = Originals();

        // Assert
        originals.Should().Contain(RenPyFixture.KnownIdentifiers.Select(p => new KeyValuePair<string, string>("say:" + p.Key, p.Value)));
    }

    [Fact]
    public void Extract_reads_scripts_packed_in_an_archive_with_the_same_keys()
    {
        // Arrange
        _dir.RenPyGame("game").RenPyGame("archived", archived: true);
        var loose = Originals();

        // Act
        var archived = _recipe.Extract(_dir.Combine("archived"), Ct);

        // Assert
        (archived.Strings.ToDictionary(s => s.Key, s => s.Original), archived.Files).Should().BeEquivalentTo((loose, new[] { "game/scripts.rpa" }));
    }

    [Fact]
    public void Extract_counts_every_string_of_the_fixture()
    {
        // Arrange
        _dir.RenPyGame("game");

        // Act
        var extraction = _recipe.Extract(Game, Ct);

        // Assert
        (extraction.Strings.Count, extraction.Files).Should().BeEquivalentTo((21, new[] { "game/screens.rpyc", "game/script.rpyc" }));
    }

    [Fact]
    public void Repeated_line_under_one_label_gets_the_next_suffix()
    {
        // Arrange
        _dir.RenPyGame("game");

        // Act
        var originals = Originals();

        // Assert
        (originals["say:rightaway_f5f51c33"], originals["say:rightaway_f5f51c33_1"]).Should().Be(("Good...", "Good..."));
    }

    [Fact]
    public void Line_under_a_hidden_label_keeps_the_label_before_it()
    {
        // Arrange
        _dir.RenPyGame("game");

        // Act
        var originals = Originals();

        // Assert
        originals.Should().ContainKey("say:rightaway_ab6a00ce").WhoseValue.Should().Be("A line under a hidden label.");
    }

    [Fact]
    public void Voice_statement_before_a_line_is_part_of_its_identifier()
    {
        // Arrange
        _dir.RenPyGame("game");
        var digest = RenPyTranslation.Digest(["voice \"sylvie_01.ogg\"", "s green smile \"Welcome back, [player]! 100%% sure.\""]);

        // Act
        var originals = Originals();

        // Assert
        originals.Should().ContainKey($"say:rightaway_{digest}");
    }

    [Fact]
    public void Extract_reads_menu_choices_and_translatable_calls_of_code_and_screens()
    {
        // Arrange
        _dir.RenPyGame("game");

        // Act
        var strings = _recipe.Extract(Game, Ct).Strings.Where(s => s.Key.StartsWith("string:", StringComparison.Ordinal)).ToDictionary(s => s.Original, s => s.Context);

        // Assert
        strings.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["Sylvie"] = "Interface text",
            ["Me"] = "Interface text",
            ["Saved!"] = "Interface text",
            ["Start"] = "Interface text",
            ["Quit"] = "Interface text",
            ["As soon as she catches my eye, I decide..."] = "Menu choice",
            ["To ask her right away."] = "Menu choice",
            ["To ask her later."] = "Menu choice",
        });
    }

    [Fact]
    public void Extract_leaves_out_the_games_own_translations()
    {
        // Arrange
        _dir.RenPyGame("game");

        // Act
        var originals = Originals();

        // Assert
        originals.Values.Should().NotContain(v => v.Contains("Плохая", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_notes_the_speaker_and_label_and_lists_placeholders()
    {
        // Arrange
        _dir.RenPyGame("game");

        // Act
        var line = _recipe.Extract(Game, Ct).Strings.Single(s => s.Original.StartsWith("Welcome back", StringComparison.Ordinal));

        // Assert
        (line.Context, line.Placeholders).Should().BeEquivalentTo(("Line said by s (label rightaway)", new[] { "[player]", "%%" }));
    }

    [Fact]
    public void Rebuild_with_the_original_texts_writes_nothing()
    {
        // Arrange
        _dir.RenPyGame("game");
        var identity = Originals();

        // Act
        var result = _recipe.Rebuild(Game, "ru", identity, new DirectoryRebuildOutput(Out), Ct);

        // Assert
        (result.ChangedFiles.Count, result.Applied, result.UnknownKeys.Count, Directory.Exists(Out)).Should().Be((0, 0, 0, false));
    }

    [Fact]
    public void Rebuild_adds_translation_files_and_the_language_switch_without_changing_game_files()
    {
        // Arrange
        _dir.RenPyGame("game");
        var translations = new Dictionary<string, string> { ["say:start_915cb944"] = "Только когда…", [Key("Start")] = "Начать" };

        // Act
        var result = _recipe.Rebuild(Game, "pt-BR", translations, new DirectoryRebuildOutput(Out), Ct);

        // Assert
        (result.ChangedFiles, result.GeneratedFiles, result.Applied).Should().BeEquivalentTo((
            new[] { "game/tl/None/ailocalpack_language.rpy", "game/tl/ailocalpack_pt_BR/script.rpy", "game/tl/ailocalpack_pt_BR/strings.rpy" },
            new[] { "game/tl/None/ailocalpack_language.rpyc", "game/tl/ailocalpack_pt_BR/script.rpyc", "game/tl/ailocalpack_pt_BR/strings.rpyc" },
            2));
    }

    [Fact]
    public void Rebuild_writes_a_dialogue_block_with_the_speaker_and_clauses_of_the_original()
    {
        // Arrange
        _dir.RenPyGame("game");
        var key = Originals().Single(p => p.Value.StartsWith("Welcome back", StringComparison.Ordinal)).Key;
        var translations = new Dictionary<string, string> { [key] = "С возвращением, [player]! На 100%% \"точно\"." };

        // Act
        _recipe.Rebuild(Game, "ru", translations, new DirectoryRebuildOutput(Out), Ct);

        // Assert
        Written("game/tl/ailocalpack_ru/script.rpy").Should().Contain($"""
            translate ailocalpack_ru {key["say:".Length..]}:

                voice "sylvie_01.ogg"
                # s green smile "Welcome back, [player]! 100%% sure."
                s green smile "С возвращением, [player]! На 100%% \"точно\"."

            """.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Rebuild_keeps_nointeract_and_with_clauses_and_escapes_line_breaks()
    {
        // Arrange
        _dir.RenPyGame("game");
        var translations = new Dictionary<string, string>
        {
            ["say:rightaway_8cb5deeb"] = "Да, но что такое «визуальная новелла»?",
            ["say:start_0918116c"] = "Зал пустеет.",
            ["say:rightaway_1c714fe0"] = "Строка один.\nСтрока  два.",
        };

        // Act
        _recipe.Rebuild(Game, "ru", translations, new DirectoryRebuildOutput(Out), Ct);

        // Assert
        Written("game/tl/ailocalpack_ru/script.rpy").Should()
            .Contain("    s \"Да, но что такое «визуальная новелла»?\" nointeract\n")
            .And.Contain("    \"Зал пустеет.\" with fade\n")
            .And.Contain("    s \"Строка один.\\nСтрока \\ два.\"\n");
    }

    [Fact]
    public void Rebuild_writes_string_translations_by_original_text()
    {
        // Arrange
        _dir.RenPyGame("game");
        var translations = new Dictionary<string, string> { [Key("To ask her later.")] = "Спросить позже.", [Key("Sylvie")] = "Сильви" };

        // Act
        _recipe.Rebuild(Game, "ru", translations, new DirectoryRebuildOutput(Out), Ct);

        // Assert
        Written("game/tl/ailocalpack_ru/strings.rpy").Should().EndWith("""
            translate ailocalpack_ru strings:

                old "Sylvie"
                new "Сильви"

                old "To ask her later."
                new "Спросить позже."


            """.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Rebuild_language_file_starts_the_game_in_the_packs_language()
    {
        // Arrange
        _dir.RenPyGame("game");

        // Act
        _recipe.Rebuild(Game, "ru", new Dictionary<string, string> { [Key("Start")] = "Начать" }, new DirectoryRebuildOutput(Out), Ct);

        // Assert
        Written("game/tl/None/ailocalpack_language.rpy").Should().Contain("init 999 python:\n    config.language = \"ailocalpack_ru\"\n");
    }

    [Fact]
    public void Rebuild_reports_keys_the_game_does_not_have()
    {
        // Arrange
        _dir.RenPyGame("game");
        var translations = new Dictionary<string, string> { ["say:nowhere_00000000"] = "x", ["string:0000000000000000"] = "y", ["say:start_915cb944"] = "z" };

        // Act
        var result = _recipe.Rebuild(Game, "ru", translations, new DirectoryRebuildOutput(Out), Ct);

        // Assert
        (result.UnknownKeys, result.Applied).Should().BeEquivalentTo((new[] { "say:nowhere_00000000", "string:0000000000000000" }, 1));
    }

    [Fact]
    public void Installed_translation_files_do_not_add_keys_on_the_next_extract()
    {
        // Arrange
        _dir.RenPyGame("game");
        var before = Originals();
        _dir.File("game/game/tl/ailocalpack_ru/script.rpyc", "compiled by Ren'Py, not the game's text");

        // Act
        var after = Originals();

        // Assert
        after.Should().BeEquivalentTo(before);
    }

    [Fact]
    public void Script_restructured_by_a_newer_Renpy_keeps_its_identifiers()
    {
        // Arrange
        _dir.RenPyGame("game");
        File.WriteAllBytes(_dir.Combine("game/game/extra.rpyc"), Compiled(slot: 2,
            Node("Label", ("name", "extra"), ("hide", false), ("block", new List<object?>
            {
                Node("TranslateSay", ("who", "e"), ("what", "Given id"), ("interact", true), ("identifier", "custom_id"), ("language", null)),
                Node("Translate", ("identifier", "extra_12345678"), ("language", null), ("block", new List<object?> { Node("Say", ("who", null), ("what", "In a block"), ("interact", true)) })),
            }))));

        // Act
        var originals = Originals();

        // Assert
        (originals["say:custom_id"], originals["say:extra_12345678"]).Should().Be(("Given id", "In a block"));
    }

    [Fact]
    public void Touched_files_are_the_compiled_scripts_holding_the_keys()
    {
        // Arrange
        _dir.RenPyGame("game", archived: true);

        // Act
        var files = _recipe.TouchedFiles(Game, ["say:start_915cb944", Key("Start"), "say:missing"]);

        // Assert
        files.Should().Equal("game/scripts.rpa");
    }

    [Fact]
    public void Game_of_another_engine_is_refused()
    {
        // Arrange
        _dir.RpgMakerGame("game");

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().WithMessage("*is not a Ren'Py game*");
    }

    [Fact]
    public void Game_without_compiled_scripts_asks_to_launch_it_once()
    {
        // Arrange
        _dir.RenPy("game");
        File.Delete(_dir.Combine("game/game/archive.rpa"));
        _dir.File("game/game/script.rpy", "label start:\n    \"Not compiled yet.\"\n");

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().WithMessage("*Launch the game once*");
    }

    [Fact]
    public void Damaged_compiled_script_names_its_file()
    {
        // Arrange
        _dir.RenPyGame("game");
        File.WriteAllBytes(_dir.Combine("game/game/script.rpyc"), Encoding.ASCII.GetBytes("RENPY RPC2 broken"));

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().Which.File.Should().Be("game/script.rpyc");
    }

    [Fact]
    public void Archive_in_an_unknown_format_names_its_file()
    {
        // Arrange
        _dir.RenPyGame("game").File("game/game/images.rpa", "ALT-1.0 custom obfuscation\n");

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().Which.File.Should().Be("game/images.rpa");
    }

    [Fact]
    public void Recipe_tools_offer_the_tool_for_Renpy_games()
    {
        // Arrange
        var tools = RecipeTools.Builtin;

        // Act
        var tool = tools.For(AiLocalPack.Toolkit.Engines.GameEngine.RenPy);

        // Assert
        tool.Should().BeOfType<RenPyRecipe>().Which.Id.Should().Be("renpy");
    }

    private static PyObject Node(string type, params (string Name, object? Value)[] fields)
    {
        var state = new PyDict();
        foreach (var (name, value) in fields)
        {
            state.Set(name, value);
        }

        return new PyObject(new PyGlobal("renpy.ast", type), new PyTuple([])) { State = state };
    }

    private static byte[] Compiled(int slot, params object?[] statements)
    {
        var data = new PyDict();
        data.Set("version", 7005000L);
        return RenPyScriptFile.Write(new Dictionary<int, byte[]> { [slot] = PickleWriter.Write(new PyTuple([data, statements.ToList()])) });
    }
}
