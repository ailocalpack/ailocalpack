using AiLocalPack.Core.Packs;
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
    [InlineData("unity", "1.0.0", RecipeToolSupport.UnknownTool)]
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
}
