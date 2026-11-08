using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Toolkit.Tests.Recipes;

public sealed class RecipeToolsTests
{
    [Theory]
    [InlineData("rpgmaker", "1.0.0", RecipeToolSupport.Supported)]
    [InlineData("rpgmaker", "1.0.7", RecipeToolSupport.Supported)]
    [InlineData("rpgmaker", "1.1.0", RecipeToolSupport.TooNew)]
    [InlineData("rpgmaker", "2.0.0", RecipeToolSupport.TooNew)]
    [InlineData("rpgmaker", "0.9.0", RecipeToolSupport.UnknownTool)]
    [InlineData("rpgmaker", "one", RecipeToolSupport.UnknownTool)]
    [InlineData("unity", "1.0.0", RecipeToolSupport.Supported)]
    public void A_recipe_is_supported_by_a_tool_with_its_id_same_major_and_at_least_its_minor(string tool, string version, RecipeToolSupport expected)
    {
        // Arrange
        var recipe = new PackRecipe(tool, version);

        // Act
        var support = RecipeTools.Builtin.Check(recipe);

        // Assert
        support.Should().Be(expected);
    }

    [Fact]
    public void The_rpgmaker_tool_handles_rpg_maker_games()
    {
        // Arrange
        var tools = RecipeTools.Builtin;

        // Act
        var tool = tools.For(GameEngine.RpgMaker);

        // Assert
        (tool?.Id, tool?.Version.ToString(3)).Should().Be(("rpgmaker", "1.0.0"));
    }

    [Theory]
    [InlineData(OsKind.MacOS, "rpgmaker:mv", RecipeToolSupport.EngineUnsupported)]
    [InlineData(OsKind.MacOS, "rpgmaker:mz", RecipeToolSupport.EngineUnsupported)]
    [InlineData(OsKind.MacOS, null, RecipeToolSupport.Supported)]
    [InlineData(OsKind.Windows, "rpgmaker:mv", RecipeToolSupport.Supported)]
    [InlineData(OsKind.Windows, "rpgmaker:mz", RecipeToolSupport.Supported)]
    [InlineData(OsKind.Linux, "rpgmaker:mz", RecipeToolSupport.Supported)]
    public void Rpg_maker_is_left_alone_on_macos_only(OsKind os, string? engine, RecipeToolSupport expected)
    {
        // Arrange
        var tools = new RecipeTools(RecipeTools.BuiltinTools(), RecipeTools.DisabledOn(os));

        // Act
        var support = tools.Check(new PackRecipe("rpgmaker", "1.0.0"), engine);

        // Assert
        support.Should().Be(expected);
    }

    [Fact]
    public void Ren_py_stays_on_on_macos()
    {
        // Arrange
        var tools = new RecipeTools(RecipeTools.BuiltinTools(), RecipeTools.DisabledOn(OsKind.MacOS));

        // Act
        var tool = (RenPy: tools.For(new EngineKind(GameEngine.RenPy)), RpgMaker: tools.For(new EngineKind(GameEngine.RpgMaker, EngineVariants.RpgMakerMv)));

        // Assert
        (tool.RenPy?.Id, tool.RpgMaker).Should().Be(("renpy", (IRecipeTool?)null));
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("0", 1)]
    [InlineData("1", 0)]
    public void The_all_engines_variable_switches_nothing_off(string? value, int disabled)
    {
        // Arrange
        var env = new OsEnvironment(OsKind.MacOS, "/home", name => name == RecipeTools.AllEnginesVariable ? value : null);

        // Act
        var tools = RecipeTools.Create(env);

        // Assert
        tools.Disabled.Count.Should().Be(disabled);
    }
}
