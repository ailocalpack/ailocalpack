using AiLocalPack.Core.Generation;

namespace AiLocalPack.Agents.Tests;

/// <summary>The model of each translation quality (decided 2026-11-13).</summary>
public sealed class AgentModelsTests
{
    [Theory]
    [InlineData(GenerationQuality.Quick, "haiku")]
    [InlineData(GenerationQuality.Balanced, "sonnet")]
    [InlineData(GenerationQuality.Thorough, "opus")]
    public void Claude_code_runs_the_latest_model_of_a_family(GenerationQuality quality, string model)
    {
        // Act
        var choice = AgentModels.For("claude", quality);

        // Assert
        choice.Should().Be(new AgentModelChoice(model, null));
    }

    [Theory]
    [InlineData(GenerationQuality.Quick, AgentEffort.Low)]
    [InlineData(GenerationQuality.Balanced, AgentEffort.Medium)]
    [InlineData(GenerationQuality.Thorough, AgentEffort.High)]
    public void Codex_keeps_its_default_model_and_changes_the_effort(GenerationQuality quality, AgentEffort effort)
    {
        // Act
        var choice = AgentModels.For("codex", quality);

        // Assert
        choice.Should().Be(new AgentModelChoice(null, effort));
    }

    [Fact]
    public void Another_agent_gets_its_own_defaults()
    {
        // Act
        var choice = AgentModels.For("fake", GenerationQuality.Thorough);

        // Assert
        choice.Should().Be(new AgentModelChoice(null, null));
    }

    [Fact]
    public void The_agents_own_scripts_run_with_the_balanced_model()
    {
        // Act
        var choice = AgentModels.ForExtraction("claude");

        // Assert
        choice.Model.Should().Be("sonnet");
    }
}
