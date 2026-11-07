using AiLocalPack.Core.Fonts;
using AiLocalPack.Toolkit.Fonts;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Recipes.RenPy;

namespace AiLocalPack.Toolkit.Tests.Recipes.RenPy;

public sealed class RenPyFontsTests : IDisposable
{
    private static readonly byte[] Regular = [.. "regular face"u8];
    private static readonly byte[] Bold = [.. "bold face"u8];

    private readonly TempDir _dir = new();
    private readonly RenPyRecipe _recipe = new();

    public void Dispose() => _dir.Dispose();

    private string Game => _dir.Combine("game");

    private string Out => _dir.Combine("out");

    private static WhitelistFont NotoSans => FontWhitelist.Bundled.Find("noto-sans")!;

    private static GameFont TextFont => new(RenPyFixture.TextFont, "game/" + RenPyFixture.TextFont);

    [Fact]
    public void Fonts_are_the_game_ones_and_those_of_renpy_common()
    {
        // Arrange
        _dir.RenPyGame("game");

        // Act
        var fonts = _recipe.Fonts(Game);

        // Assert
        fonts.Should().Equal(new GameFont("DejaVuSans.ttf", "renpy/common/DejaVuSans.ttf"), TextFont);
    }

    [Fact]
    public void An_archived_font_is_read_from_its_archive()
    {
        // Arrange
        _dir.RenPyGame("game", archived: true);
        var font = _recipe.Fonts(Game).Single(f => f.Name == RenPyFixture.TextFont);

        // Act
        var info = OpenTypeFont.Read(_recipe.ReadFont(Game, font));

        // Assert
        (font, info.FamilyName).Should().Be((new GameFont(RenPyFixture.TextFont, "game/scripts.rpa", RenPyFixture.TextFont), "Fixture Sans"));
    }

    [Fact]
    public void Fonts_of_translations_are_left_out()
    {
        // Arrange
        _dir.RenPyGame("game").File("game/game/tl/french/fonts/French.ttf", FontWriter.TrueType("French", FontWriter.Ascii));

        // Act
        var fonts = _recipe.Fonts(Game);

        // Assert
        fonts.Select(f => f.Name).Should().NotContain(n => n.StartsWith("tl/", StringComparison.Ordinal));
    }

    [Fact]
    public void Swap_maps_every_face_of_the_font_to_the_replacement_in_the_language_only()
    {
        // Arrange
        _dir.RenPyGame("game");

        // Act
        _recipe.Swap(Game, "ru", [new FontSwap(TextFont, NotoSans, Regular, Bold)], new DirectoryRebuildOutput(Out));

        // Assert
        File.ReadAllText(_dir.Combine("out/game/tl/ailocalpack_ru/ailocalpack_fonts.rpy")).ReplaceLineEndings("\n").Should().EndWith("""
            translate ailocalpack_ru python:
                config.font_replacement_map["gui/font/FixtureSans.ttf", False, False] = ("tl/ailocalpack_ru/ailocalpack_fonts/NotoSans-Regular.ttf", False, False)
                config.font_replacement_map["gui/font/FixtureSans.ttf", True, False] = ("tl/ailocalpack_ru/ailocalpack_fonts/NotoSans-Bold.ttf", False, False)
                config.font_replacement_map["gui/font/FixtureSans.ttf", False, True] = ("tl/ailocalpack_ru/ailocalpack_fonts/NotoSans-Regular.ttf", False, True)
                config.font_replacement_map["gui/font/FixtureSans.ttf", True, True] = ("tl/ailocalpack_ru/ailocalpack_fonts/NotoSans-Bold.ttf", False, True)

            """);
    }

    [Fact]
    public void Swap_writes_the_font_files_and_the_script_only()
    {
        // Arrange
        _dir.RenPyGame("game");

        // Act
        var files = _recipe.Swap(Game, "pt-BR", [new FontSwap(TextFont, NotoSans, Regular, Bold)], new DirectoryRebuildOutput(Out));

        // Assert
        (files.Files, files.GeneratedFiles, File.ReadAllBytes(_dir.Combine("out/game/tl/ailocalpack_pt_BR/ailocalpack_fonts/NotoSans-Bold.ttf")))
            .Should().BeEquivalentTo((
                new[]
                {
                    "game/tl/ailocalpack_pt_BR/ailocalpack_fonts.rpy",
                    "game/tl/ailocalpack_pt_BR/ailocalpack_fonts/NotoSans-Bold.ttf",
                    "game/tl/ailocalpack_pt_BR/ailocalpack_fonts/NotoSans-Regular.ttf",
                },
                new[] { "game/tl/ailocalpack_pt_BR/ailocalpack_fonts.rpyc" },
                Bold));
    }

    [Fact]
    public void Without_a_bold_face_ren_py_makes_bold_from_the_regular_one()
    {
        // Arrange
        _dir.RenPyGame("game");

        // Act
        _recipe.Swap(Game, "ru", [new FontSwap(TextFont, NotoSans, Regular, null)], new DirectoryRebuildOutput(Out));

        // Assert
        File.ReadAllText(_dir.Combine("out/game/tl/ailocalpack_ru/ailocalpack_fonts.rpy")).Should().Contain(
            "config.font_replacement_map[\"gui/font/FixtureSans.ttf\", True, False] = (\"tl/ailocalpack_ru/ailocalpack_fonts/NotoSans-Regular.ttf\", True, False)");
    }
}
