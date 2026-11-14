using AiLocalPack.Core.Generation;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Generation;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Toolkit.Tests.Generation;

public sealed class GameReaderTests
{
    private static readonly EngineKind Unity = new(GameEngine.Unity, EngineVariants.Mono);

    [Fact]
    public void Without_a_choice_the_detected_engines_tool_reads_the_game()
    {
        // Act
        var reader = GameReader.Resolve(RecipeTools.Builtin, Unity, null);

        // Assert
        reader.Should().BeEquivalentTo(new { Kind = Unity, Tool = new { Id = "unity" }, Source = TextSizeSource.Toolkit, Problem = (string?)null });
    }

    [Fact]
    public void The_agents_own_code_keeps_the_detected_engine_and_has_no_tool()
    {
        // Act
        var reader = GameReader.Resolve(RecipeTools.Builtin, Unity, GenerationOptions.AgentReader);

        // Assert
        reader.Should().BeEquivalentTo(new { Kind = Unity, Tool = (IRecipeTool?)null, Source = TextSizeSource.AgentChosen });
    }

    [Fact]
    public void The_agents_own_code_says_the_player_chose_it()
    {
        // Act
        var reader = GameReader.Resolve(RecipeTools.Builtin, Unity, GenerationOptions.AgentReader);

        // Assert
        reader.Problem.Should().Contain("player chose");
    }

    [Fact]
    public void Another_engines_tool_reads_the_game_as_that_engine()
    {
        // Act
        var reader = GameReader.Resolve(RecipeTools.Builtin, Unity, "godot");

        // Assert
        reader.Should().BeEquivalentTo(new { Kind = new EngineKind(GameEngine.Godot), Tool = new { Id = "godot" }, Source = TextSizeSource.Toolkit });
    }

    [Fact]
    public void The_detected_engines_own_tool_keeps_its_variant()
    {
        // Act
        var reader = GameReader.Resolve(RecipeTools.Builtin, Unity, "unity");

        // Assert
        reader.Kind.Should().Be(Unity);
    }

    [Fact]
    public void A_tool_of_an_engine_switched_off_here_has_no_tool()
    {
        // Arrange
        var tools = new RecipeTools(RecipeTools.BuiltinTools(), [new EngineKind(GameEngine.RpgMaker)]);

        // Act
        var reader = GameReader.Resolve(tools, Unity, "rpgmaker");

        // Assert
        reader.Should().BeEquivalentTo(new { Kind = new EngineKind(GameEngine.RpgMaker), Tool = (IRecipeTool?)null, Source = TextSizeSource.EngineDisabled });
    }

    [Fact]
    public void A_tool_this_app_lacks_falls_back_to_the_detected_engine()
    {
        // Act
        var reader = GameReader.Resolve(RecipeTools.Builtin, Unity, "frostbite");

        // Assert
        reader.Should().BeEquivalentTo(new { Kind = Unity, Tool = new { Id = "unity" }, Source = TextSizeSource.Toolkit });
    }

    [Fact]
    public void An_unknown_engine_without_a_choice_has_no_tool()
    {
        // Act
        var reader = GameReader.Resolve(RecipeTools.Builtin, EngineKind.Unknown, null);

        // Assert
        reader.Should().BeEquivalentTo(new { Tool = (IRecipeTool?)null, Source = TextSizeSource.UnknownEngine });
    }
}
