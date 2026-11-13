using AiLocalPack.Agents;
using AiLocalPack.Core.Generation;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>Stage Study of a Thorough job (decided 2026-11-13): the game brief, web lookup, the brief in later runs.</summary>
public sealed class StudyStageTests : IDisposable
{
    private static readonly string Brief = "## Setting\n" + new string('x', 300) + "\n## Characters\nMira: a fixer, female, informal with the player.";

    private static readonly GenerationOptions Thorough = new(Quality: GenerationQuality.Thorough);

    private readonly StageHarness _h = new();

    public StudyStageTests()
    {
        _h.WithRpgMakerCopy();
        new ExtractStage(RecipeTools.Builtin).RunStepAsync(_h.Context(GenerationStage.Extract), TestContext.Current.CancellationToken).GetAwaiter().GetResult();
    }

    public void Dispose() => _h.Dispose();

    private static ScriptedAgent Replying(string? reply) =>
        new((_, _, _) => Task.FromResult<IReadOnlyList<AgentEvent>>([new AgentDone(AgentOutcome.Succeeded, reply)]));

    private Task<GenerationStepResult> RunAsync(ScriptedAgent agent, GenerationOptions? options = null) =>
        new StudyStage(new PipelineAgent(agent, StageHarness.Server), "Sample Quest")
            .RunStepAsync(_h.Context(GenerationStage.Study, options: options ?? Thorough), TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_agents_brief_completes_the_stage_and_is_stored()
    {
        // Arrange
        var agent = Replying("Here is the brief.\n\n" + Brief);

        // Act
        var result = await RunAsync(agent);

        // Assert
        (result, _h.Files.ReadBrief()).Should().Be((new GenerationStepResult.StageDone(), Brief));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("I read the game; it is about couriers.")]
    [InlineData("## Setting\nToo short.")]
    public async Task A_reply_without_a_brief_fails_the_stage(string? reply)
    {
        // Arrange
        var agent = Replying(reply);

        // Act
        var result = await RunAsync(agent);

        // Assert
        result.Should().Be(new GenerationStepResult.Failed(GenerationFailureKind.Agent, "Scripted agent finished without writing the game brief."));
    }

    [Fact]
    public async Task Web_lookup_gives_only_the_study_run_the_web_tools()
    {
        // Arrange
        var agent = Replying(Brief);

        // Act
        await RunAsync(agent, Thorough with { WebLookup = true });

        // Assert
        agent.Runs.Single().Request!.BuiltinTools.Should().BeEquivalentTo([AgentBuiltinTool.WebSearch]);
    }

    [Fact]
    public async Task Without_web_lookup_the_study_run_has_no_built_in_tools()
    {
        // Arrange
        var agent = Replying(Brief);

        // Act
        await RunAsync(agent);

        // Assert
        agent.Runs.Single().Request!.BuiltinTools.Should().BeEmpty();
    }

    [Fact]
    public async Task The_prompt_says_whether_the_web_may_be_used()
    {
        // Arrange
        var agent = Replying(Brief);

        // Act
        await RunAsync(agent, Thorough with { WebLookup = true });

        // Assert
        agent.Runs.Single().Prompt.Should().Contain("Look the game up on the web");
    }

    [Fact]
    public async Task A_thorough_glossary_run_gets_the_brief_in_its_system_prompt()
    {
        // Arrange
        _h.Files.WriteBrief(Brief);
        var agent = Replying("done");

        // Act
        await new GlossaryStage(new PipelineAgent(agent, StageHarness.Server), "Sample Quest")
            .RunStepAsync(_h.Context(GenerationStage.Glossary, options: Thorough), TestContext.Current.CancellationToken);

        // Assert
        agent.Runs.Single().Request!.SystemPrompt.Should().Contain("<game-brief>\n" + Brief + "\n</game-brief>");
    }

    [Fact]
    public async Task A_balanced_glossary_run_gets_no_brief()
    {
        // Arrange
        _h.Files.WriteBrief(Brief);
        var agent = Replying("done");

        // Act
        await new GlossaryStage(new PipelineAgent(agent, StageHarness.Server), "Sample Quest")
            .RunStepAsync(_h.Context(GenerationStage.Glossary), TestContext.Current.CancellationToken);

        // Assert
        agent.Runs.Single().Request!.SystemPrompt.Should().NotContain("<game-brief>");
    }

    [Fact]
    public void The_brief_starts_at_its_first_heading()
    {
        // Act
        var brief = StudyStage.Brief("Done! Here it is:\n\n" + Brief + "\n");

        // Assert
        brief.Should().Be(Brief);
    }

    [Fact]
    public void A_long_brief_is_cut_at_a_line_end()
    {
        // Arrange
        var line = new string('y', 99);
        var reply = "## Setting\n" + string.Join('\n', Enumerable.Repeat(line, 400));

        // Act
        var brief = StudyStage.Brief(reply)!;

        // Assert
        (brief.Length <= StudyStage.MaxLength, brief.EndsWith(line, StringComparison.Ordinal)).Should().Be((true, true));
    }

    [Theory]
    [InlineData("claude", GenerationQuality.Thorough, false, "opus")]
    [InlineData("claude", GenerationQuality.Quick, false, "haiku")]
    [InlineData("claude", GenerationQuality.Thorough, true, "sonnet")]
    public void A_run_takes_the_qualitys_model_and_scripts_the_balanced_one(string agent, GenerationQuality quality, bool scripts, string model)
    {
        // Arrange
        var job = _h.Context(GenerationStage.Translate, options: new GenerationOptions(Quality: quality)).Job;

        // Act
        var choice = PipelineAgent.ModelFor(job, agent, scripts);

        // Assert
        choice.Model.Should().Be(model);
    }

    [Fact]
    public void A_model_the_player_named_wins_over_the_quality()
    {
        // Arrange
        var job = _h.Context(GenerationStage.Translate, options: new GenerationOptions(Model: "claude-opus-5-5", Quality: GenerationQuality.Quick)).Job;

        // Act
        var choice = PipelineAgent.ModelFor(job, "claude", scripts: false);

        // Assert
        choice.Should().Be(new AgentModelChoice("claude-opus-5-5", null));
    }
}
