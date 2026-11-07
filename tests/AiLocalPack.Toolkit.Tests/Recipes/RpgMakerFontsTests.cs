using System.Text;
using AiLocalPack.Core.Fonts;
using AiLocalPack.Toolkit.Fonts;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Recipes.RpgMaker;

namespace AiLocalPack.Toolkit.Tests.Recipes;

public sealed class RpgMakerFontsTests : IDisposable
{
    private static readonly byte[] Replacement = [.. "replacement font"u8];

    private readonly TempDir _dir = new();
    private readonly RpgMakerRecipe _recipe = new();

    public void Dispose() => _dir.Dispose();

    private string Game => _dir.Combine("game");

    private string Out => _dir.Combine("out");

    private static WhitelistFont NotoSans => FontWhitelist.Bundled.Find("noto-sans")!;

    private FontSwap Swap() => new(_recipe.Fonts(Game)[0], NotoSans, Replacement, null);

    private static List<string> Files(string folder) =>
        [.. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).Order(StringComparer.Ordinal)];

    [Fact]
    public void Mz_text_font_is_the_main_font_named_in_system_json()
    {
        // Arrange
        _dir.RpgMakerGame("game");

        // Act
        var fonts = _recipe.Fonts(Game);

        // Assert
        fonts.Should().Equal(new GameFont("fixture-sans.woff", "fonts/fixture-sans.woff"));
    }

    [Fact]
    public void Mv_text_font_is_the_game_font_face_of_gamefont_css()
    {
        // Arrange
        _dir.RpgMakerGame("game", mv: true);

        // Act
        var fonts = _recipe.Fonts(Game);

        // Assert
        fonts.Should().Equal(new GameFont("fixture-sans.ttf", "www/fonts/fixture-sans.ttf"));
    }

    [Fact]
    public void A_named_font_file_that_is_missing_gives_no_font()
    {
        // Arrange
        _dir.RpgMakerGame("game");
        File.Delete(_dir.Combine("game/fonts/fixture-sans.woff"));

        // Act
        var fonts = _recipe.Fonts(Game);

        // Assert
        fonts.Should().BeEmpty();
    }

    [Fact]
    public void Mv_without_gamefont_css_gives_no_font()
    {
        // Arrange
        _dir.RpgMakerGame("game", mv: true);
        File.Delete(_dir.Combine("game/www/fonts/gamefont.css"));

        // Act
        var fonts = _recipe.Fonts(Game);

        // Assert
        fonts.Should().BeEmpty();
    }

    [Fact]
    public void Mz_swap_adds_the_font_and_changes_only_the_main_font_in_system_json()
    {
        // Arrange
        _dir.RpgMakerGame("game");
        var original = File.ReadAllText(_dir.Combine("game/data/System.json"));

        // Act
        var files = _recipe.Swap(Game, "ru", [Swap()], new DirectoryRebuildOutput(Out));

        // Assert
        (files.Files, Files(Out), File.ReadAllText(_dir.Combine("out/data/System.json")), File.ReadAllBytes(_dir.Combine("out/fonts/ailocalpack-NotoSans-Regular.ttf")))
            .Should().BeEquivalentTo((
                new[] { "data/System.json", "fonts/ailocalpack-NotoSans-Regular.ttf" },
                new[] { "data/System.json", "fonts/ailocalpack-NotoSans-Regular.ttf" },
                original.Replace("\"mainFontFilename\":\"fixture-sans.woff\"", "\"mainFontFilename\":\"ailocalpack-NotoSans-Regular.ttf\"", StringComparison.Ordinal),
                Replacement));
    }

    [Fact]
    public void Mz_swap_edits_the_rebuilt_system_json()
    {
        // Arrange
        _dir.RpgMakerGame("game");
        var output = new DirectoryRebuildOutput(Out);
        _recipe.Rebuild(Game, "ru", new Dictionary<string, string> { ["data/System.json:/gameTitle"] = "Квест" }, output, TestContext.Current.CancellationToken);

        // Act
        _recipe.Swap(Game, "ru", [Swap()], output);

        // Assert
        var system = File.ReadAllText(_dir.Combine("out/data/System.json"));
        (system.Contains("\"gameTitle\":\"Квест\"", StringComparison.Ordinal), system.Contains("\"mainFontFilename\":\"ailocalpack-NotoSans-Regular.ttf\"", StringComparison.Ordinal))
            .Should().Be((true, true));
    }

    [Fact]
    public void Mv_swap_points_the_game_font_face_at_the_new_file()
    {
        // Arrange
        _dir.RpgMakerGame("game", mv: true);

        // Act
        var files = _recipe.Swap(Game, "ru", [Swap()], new DirectoryRebuildOutput(Out));

        // Assert
        (files.Files, File.ReadAllText(_dir.Combine("out/www/fonts/gamefont.css")).ReplaceLineEndings("\n")).Should().BeEquivalentTo((
            new[] { "www/fonts/ailocalpack-NotoSans-Regular.ttf", "www/fonts/gamefont.css" },
            "@font-face {\n    font-family: GameFont;\n    src: url(\"ailocalpack-NotoSans-Regular.ttf\");\n}\n"));
    }

    [Fact]
    public void Mv_swap_without_a_game_font_face_is_refused()
    {
        // Arrange
        _dir.RpgMakerGame("game", mv: true);
        var swap = Swap();
        File.WriteAllText(_dir.Combine("game/www/fonts/gamefont.css"), "body { color: red; }", Encoding.UTF8);

        // Act
        var act = () => _recipe.Swap(Game, "ru", [swap], new DirectoryRebuildOutput(Out));

        // Assert
        act.Should().Throw<RecipeException>().Which.File.Should().Be("www/fonts/gamefont.css");
    }
}
