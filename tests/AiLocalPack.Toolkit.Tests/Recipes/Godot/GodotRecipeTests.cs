using System.Text;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Recipes.Godot;

namespace AiLocalPack.Toolkit.Tests.Recipes.Godot;

/// <summary>The <c>godot</c> recipe tool on the synthetic Godot game (task 35).</summary>
public sealed class GodotRecipeTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly GodotRecipe _recipe = new();

    public void Dispose() => _dir.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Game => _dir.Combine("game");

    private string Out => _dir.Combine("out");

    private Dictionary<string, string> Originals(string? game = null) =>
        _recipe.Extract(game ?? Game, Ct).Strings.ToDictionary(s => s.Key, s => s.Original);

    private RebuildResult Rebuild(Dictionary<string, string> translations) =>
        _recipe.Rebuild(Game, "ru", translations, new DirectoryRebuildOutput(Out), Ct);

    /// <summary>The rebuilt game: the output's files over a copy of the game.</summary>
    private string Rebuilt()
    {
        _dir.CopyFolder(Game, "rebuilt").CopyFolder(Out, "rebuilt");
        return _dir.Combine("rebuilt");
    }

    private static string HashedKey(GodotTranslation translation, string text) => "tr" + translation.Messages.Single(m => m.Text == text).Id;

    private string UnnamedKey() => Originals().Single(p => p.Value == GodotFixture.UnnamedText).Key;

    [Theory]
    [InlineData(3u, false)]
    [InlineData(4u, false)]
    [InlineData(4u, true)]
    public void Extract_gives_the_English_messages_by_their_keys(uint major, bool embedded)
    {
        // Arrange
        _dir.GodotGame("game", major, embedded);

        // Act
        var originals = Originals();

        // Assert
        originals.Where(p => p.Key.StartsWith("tr:", StringComparison.Ordinal)).Should().BeEquivalentTo(
            GodotFixture.English.Where(m => m.Value != GodotFixture.UnnamedText).Concat(GodotFixture.Extra)
                .Select(m => new KeyValuePair<string, string>("tr:" + m.Key, m.Value)));
    }

    [Fact]
    public void Message_whose_key_no_file_names_is_keyed_by_its_hash()
    {
        // Arrange
        _dir.GodotGame("game");
        var english = GodotTranslation.Read(ReadEntry(GodotFixture.EnglishTranslation));

        // Act
        var key = UnnamedKey();

        // Assert
        key.Should().Be(HashedKey(english, GodotFixture.UnnamedText));
    }

    [Fact]
    public void Extract_leaves_other_languages_alone()
    {
        // Arrange
        _dir.GodotGame("game");

        // Act
        var originals = Originals();

        // Assert
        originals.Values.Should().NotContain(v => v.StartsWith("fr: ", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_names_the_pack_and_the_key_in_the_context()
    {
        // Arrange
        _dir.GodotGame("game");

        // Act
        var extraction = _recipe.Extract(Game, Ct);

        // Assert
        (extraction.Tool, extraction.Files, extraction.Strings.Single(s => s.Key == "tr:MENU_PLAY").Context)
            .Should().BeEquivalentTo(("godot", new[] { "Game.pck" }, "Game text (translation key MENU_PLAY)"));
    }

    [Fact]
    public void Extract_lists_placeholders()
    {
        // Arrange
        _dir.GodotGame("game");

        // Act
        var extraction = _recipe.Extract(Game, Ct);

        // Assert
        extraction.Strings.Single(s => s.Key == "tr:ITEM_SWORD_DESC").Placeholders.Should().Equal("[color=red]", "%d", "[/color]");
    }

    [Theory]
    [InlineData(3u)]
    [InlineData(4u)]
    public void Extract_of_a_game_without_translations_reads_its_scenes(uint major)
    {
        // Arrange
        _dir.GodotGame("game", major, translated: false);
        var tooltip = major == 3 ? "hint_tooltip" : "tooltip_text";
        var rich = major == 3 ? "bbcode_text" : "text";

        // Act
        var originals = Originals();

        // Assert
        originals.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["scene:ui/menu.tscn:Play:text"] = "Play",
            [$"scene:ui/menu.tscn:Play:{tooltip}"] = "Opens the credits",
            ["scene:ui/menu.tscn:Quit:text"] = "Quit \"now\"\\later",
            [$"scene:ui/menu.tscn:Play/Gold:{rich}"] = "[color=yellow]Gold[/color]: %d\nper wave",
        });
    }

    [Theory]
    [InlineData(3u, false)]
    [InlineData(4u, false)]
    [InlineData(3u, true)]
    [InlineData(4u, true)]
    public void Rebuilt_pack_holds_the_translations(uint major, bool embedded)
    {
        // Arrange
        _dir.GodotGame("game", major, embedded);
        var unnamed = UnnamedKey();
        Rebuild(new() { ["tr:MENU_PLAY"] = "Играть", ["tr:CREDITS_TITLE"] = "Титры", [unnamed] = "Конец?" });

        // Act
        var originals = Originals(Rebuilt());

        // Assert
        (originals["tr:MENU_PLAY"], originals["tr:CREDITS_TITLE"], originals[unnamed], originals["tr:MENU_QUIT"])
            .Should().Be(("Играть", "Титры", "Конец?", "Quit the game"));
    }

    [Fact]
    public void Rebuild_reports_the_pack_and_the_count()
    {
        // Arrange
        _dir.GodotGame("game");

        // Act
        var result = Rebuild(new() { ["tr:MENU_PLAY"] = "Играть", ["tr:HUD_GOLD"] = "Золото: {0}", ["tr:NOPE"] = "x" });

        // Assert
        (result.ChangedFiles, result.Applied, result.UnknownKeys, result.GeneratedFiles)
            .Should().BeEquivalentTo((new[] { "Game.pck" }, 2, new[] { "tr:NOPE" }, Array.Empty<string>()));
    }

    [Fact]
    public void Rebuild_into_the_translations_asks_for_their_source_language_in_game()
    {
        // Arrange
        _dir.GodotGame("game");

        // Act
        var result = Rebuild(new() { ["tr:MENU_PLAY"] = "Играть" });

        // Assert
        result.InGameLanguage.Should().Be("en");
    }

    [Fact]
    public void Rebuild_of_scene_texts_needs_no_language_change_in_game()
    {
        // Arrange
        _dir.GodotGame("game", translated: false);

        // Act
        var result = Rebuild(new() { ["scene:ui/menu.tscn:Play:text"] = "Играть" });

        // Assert
        result.InGameLanguage.Should().BeNull();
    }

    [Fact]
    public void Rebuild_keeps_every_other_file_of_the_pack_byte_for_byte()
    {
        // Arrange
        _dir.GodotGame("game");
        using var before = GodotPack.Open(_dir.Combine("game/Game.pck"));
        Rebuild(new() { ["tr:MENU_PLAY"] = "Играть" });

        // Act
        using var after = GodotPack.Open(Path.Combine(Out, "Game.pck"));

        // Assert
        after.Entries.Where(e => e.Path != GodotFixture.EnglishTranslation).ToDictionary(e => e.Path, e => after.Read(e))
            .Should().BeEquivalentTo(before.Entries.Where(e => e.Path != GodotFixture.EnglishTranslation).ToDictionary(e => e.Path, e => before.Read(e)));
    }

    [Fact]
    public void Rebuild_of_an_embedded_pack_writes_the_executable()
    {
        // Arrange
        _dir.GodotGame("game", 4, embedded: true);

        // Act
        var result = Rebuild(new() { ["tr:MENU_PLAY"] = "Играть" });

        // Assert
        (result.ChangedFiles, File.ReadAllText(Path.Combine(Out, "Game.exe"), Encoding.Latin1).StartsWith("MZ stub", StringComparison.Ordinal))
            .Should().BeEquivalentTo((new[] { "Game.exe" }, true));
    }

    [Fact]
    public void Rebuild_with_nothing_to_change_writes_nothing()
    {
        // Arrange
        _dir.GodotGame("game");

        // Act
        var result = Rebuild(new() { ["tr:MENU_PLAY"] = "Play", ["tr:MENU_QUIT"] = string.Empty });

        // Assert
        (result.ChangedFiles.Count, result.Applied, Directory.Exists(Out)).Should().Be((0, 0, false));
    }

    [Theory]
    [InlineData(3u)]
    [InlineData(4u)]
    public void Rebuilt_scenes_hold_the_translations(uint major)
    {
        // Arrange
        _dir.GodotGame("game", major, translated: false);
        Rebuild(new() { ["scene:ui/menu.tscn:Quit:text"] = "Выйти \"сейчас\"", ["scene:ui/menu.tscn:Play:text"] = "Играть" });

        // Act
        var originals = Originals(Rebuilt());

        // Assert
        (originals["scene:ui/menu.tscn:Quit:text"], originals["scene:ui/menu.tscn:Play:text"]).Should().Be(("Выйти \"сейчас\"", "Играть"));
    }

    [Fact]
    public void Encrypted_translation_is_refused_with_the_reason()
    {
        // Arrange
        var translation = GodotTranslation.Create(4, "en", GodotFixture.English, hashed: true);
        _dir.File("game/Game.exe", "MZ").File("game/Game.pck", GodotFixture.Pack(2, 4, [("l/s.en.translation", translation)], encrypted: new HashSet<string> { "l/s.en.translation" }));

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().Which.Message.Should().Contain("encrypted");
    }

    [Fact]
    public void Encrypted_pack_is_refused_with_the_file()
    {
        // Arrange
        _dir.File("game/Game.exe", "MZ").File("game/Game.pck", GodotFixture.Pack(2, 4, [("a.tscn", [1])], flags: 3));

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().Which.File.Should().Be("Game.pck");
    }

    [Fact]
    public void Damaged_translation_is_refused_with_the_pack()
    {
        // Arrange
        _dir.File("game/Game.exe", "MZ").File("game/Game.pck", GodotFixture.Pack(1, 3, [("l/s.en.translation", [.. "RSRC"u8, 0, 0])]));

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().Which.Message.Should().Contain("res://l/s.en.translation");
    }

    [Fact]
    public void A_game_of_another_engine_is_refused()
    {
        // Arrange
        _dir.RenPyGame("game");

        // Act
        var act = () => _recipe.Extract(Game, Ct);

        // Assert
        act.Should().Throw<RecipeException>().Which.Message.Should().Contain("not a Godot game");
    }

    [Fact]
    public void Source_language_is_the_only_one_when_the_game_has_no_English()
    {
        // Arrange
        var german = GodotTranslation.Create(3, "de", [new("GREETING", "Hallo")], hashed: false);
        _dir.File("game/Game.exe", "MZ").File("game/Game.pck", GodotFixture.Pack(1, 3, [("de.translation", german)]));

        // Act
        var originals = Originals();

        // Assert
        originals.Should().BeEquivalentTo(new Dictionary<string, string> { ["tr:GREETING"] = "Hallo" });
    }

    [Fact]
    public void Main_pack_is_the_one_named_like_the_executable()
    {
        // Arrange
        _dir.GodotGame("game");
        _dir.File("game/Aaa.pck", GodotFixture.Pack(1, 3, [("x.tscn", "[node name=\"X\" type=\"Label\"]\ntext = \"Other tool\"\n"u8.ToArray())]));

        // Act
        var extraction = _recipe.Extract(Game, Ct);

        // Assert
        extraction.Files.Should().Equal("Game.pck");
    }

    [Fact]
    public void TouchedFiles_is_the_pack_for_keys_of_the_game()
    {
        // Arrange
        _dir.GodotGame("game");

        // Act
        var touched = (_recipe.TouchedFiles(Game, ["tr:MENU_PLAY"]), _recipe.TouchedFiles(Game, ["tr:NOPE"]));

        // Assert
        touched.Should().BeEquivalentTo((new[] { "Game.pck" }, Array.Empty<string>()));
    }

    [Theory]
    [InlineData("Deal {0} damage", new[] { "{0}" })]
    [InlineData("[b]Bold[/b] and [color=#ff0000]red[/color]", new[] { "[b]", "[/b]", "[color=#ff0000]", "[/color]" })]
    [InlineData("%s has %d HP (%.1f%%)", new[] { "%s", "%d", "%.1f", "%%" })]
    [InlineData("{name} wins", new[] { "{name}" })]
    [InlineData("50% done [not closed", new string[0])]
    public void Placeholders_are_bbcode_and_format_fields(string text, string[] expected)
    {
        // Act
        var placeholders = GodotRecipe.Placeholders(text);

        // Assert
        placeholders.Should().Equal(expected);
    }

    private byte[] ReadEntry(string path)
    {
        using var pack = GodotPack.Open(_dir.Combine("game/Game.pck"));
        return pack.Read(pack.Entries.Single(e => e.Path == path));
    }
}
