using AiLocalPack.Agents;
using AiLocalPack.Core.Generation;
using AiLocalPack.Toolkit.Recipes;
using static AiLocalPack.Pipeline.Tests.FakeAgentProgram;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>Stage Glossary with the fake agent (task 49): success, agent failures, limit, sign-in, what was saved.</summary>
public sealed class GlossaryStageTests : IDisposable
{
    private readonly StageHarness _h = new();

    public GlossaryStageTests()
    {
        _h.WithRpgMakerCopy();
        new ExtractStage(RecipeTools.Builtin).RunStepAsync(_h.Context(GenerationStage.Extract), TestContext.Current.CancellationToken).GetAwaiter().GetResult();
    }

    public void Dispose() => _h.Dispose();

    private string Recording(params string[] lines) => Write(_h.Dir, "agent.jsonl", lines);

    private Task<GenerationStepResult> RunAsync(string recording, AgentAuthState auth = AgentAuthState.SignedIn) =>
        new GlossaryStage(new PipelineAgent(Adapter(recording, auth), StageHarness.Server), "Sample Quest")
            .RunStepAsync(_h.Context(GenerationStage.Glossary), TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_saved_glossary_completes_the_stage()
    {
        // Arrange
        var recording = Recording(SaveGlossary(RussianGlossary), Done("2 terms, informal"));

        // Act
        var result = await RunAsync(recording);

        // Assert
        result.Should().Be(new GenerationStepResult.StageDone());
    }

    [Fact]
    public async Task The_glossary_is_logged()
    {
        // Arrange
        var recording = Recording(SaveGlossary(RussianGlossary), Done());

        // Act
        await RunAsync(recording);

        // Assert
        _h.Log.Should().Contain("Glossary: 2 terms, tone: informal, ты.");
    }

    [Fact]
    public async Task The_agent_gets_the_rendered_glossary_prompt()
    {
        // Arrange
        var recording = Recording("""{"control":"prompt"}""", SaveGlossary(RussianGlossary), Done());

        // Act
        await RunAsync(recording);

        // Assert
        var transcript = File.ReadAllLines(Path.Combine(_h.Folder, "logs", "agent-glossary.jsonl"));
        var message = (AgentMessage)AgentEventJson.Read(transcript[0], out _)!;
        message.Text.Should().StartWith("Build the glossary for translating the video game \"Sample Quest\" (RPG Maker MZ) into Russian.");
    }

    [Fact]
    public async Task An_agent_that_saves_nothing_fails_the_stage()
    {
        // Arrange
        var recording = Recording(Done());

        // Act
        var result = await RunAsync(recording);

        // Assert
        result.Should().Be(new GenerationStepResult.Failed(GenerationFailureKind.Agent, "Fake agent finished without saving a glossary."));
    }

    [Fact]
    public async Task A_run_that_leaves_only_the_players_edits_fails_the_stage()
    {
        // Arrange
        _h.Files.WriteGlossary(new Glossary("ru", null, [new GlossaryEntry("Potion", "Эликсир", Edited: true)]));
        var recording = Recording(Done());

        // Act
        var result = await RunAsync(recording);

        // Assert
        result.Should().Be(new GenerationStepResult.Failed(GenerationFailureKind.Agent, "Fake agent finished without saving a glossary."));
    }

    [Fact]
    public async Task An_empty_glossary_fails_the_stage()
    {
        // Arrange
        var recording = Recording(SaveGlossary("""{"schemaVersion":1,"language":"ru","entries":[]}"""), Done());

        // Act
        var result = await RunAsync(recording);

        // Assert
        result.Should().BeOfType<GenerationStepResult.Failed>().Which.Kind.Should().Be(GenerationFailureKind.Agent);
    }

    [Fact]
    public async Task A_damaged_glossary_fails_the_stage_with_the_reason()
    {
        // Arrange
        var recording = Recording(SaveGlossary("""{"schemaVersion":1,"language":"ru","entries":[{"term":"A"}]}"""), Done());

        // Act
        var result = await RunAsync(recording);

        // Assert
        result.Should().Be(new GenerationStepResult.Failed(GenerationFailureKind.Agent, "The glossary the agent saved can't be read: glossary.json: term 'A' has no translation"));
    }

    [Fact]
    public async Task A_glossary_in_another_language_fails_the_stage()
    {
        // Arrange
        var recording = Recording(SaveGlossary(RussianGlossary.Replace("\"ru\"", "\"de\"", StringComparison.Ordinal)), Done());

        // Act
        var result = await RunAsync(recording);

        // Assert
        result.Should().Be(new GenerationStepResult.Failed(GenerationFailureKind.Agent, "The saved glossary is in 'de', not 'ru'."));
    }

    [Fact]
    public async Task An_agent_error_fails_the_stage_with_its_reason()
    {
        // Arrange
        var recording = Recording(Event(new AgentDone(AgentOutcome.Failed, Message: "The model is overloaded.")));

        // Act
        var result = await RunAsync(recording);

        // Assert
        result.Should().Be(new GenerationStepResult.Failed(GenerationFailureKind.Agent, "The model is overloaded."));
    }

    [Fact]
    public async Task The_limit_mid_stage_reports_its_reset_time()
    {
        // Arrange
        var reset = new DateTimeOffset(2026, 11, 8, 17, 0, 0, TimeSpan.Zero);
        var recording = Recording(
            Event(new AgentLimitReached(reset, "You've hit your limit")),
            Event(new AgentDone(AgentOutcome.LimitReached, Message: "limit")));

        // Act
        var result = await RunAsync(recording);

        // Assert
        result.Should().Be(new GenerationStepResult.LimitReached(reset, "You've hit your limit"));
    }

    [Fact]
    public async Task A_signed_out_agent_fails_before_it_runs()
    {
        // Arrange
        var recording = Recording(SaveGlossary(RussianGlossary), Done());

        // Act
        var result = await RunAsync(recording, AgentAuthState.SignedOut);

        // Assert
        new { result, Saved = File.Exists(_h.Files.GlossaryPath) }.Should().BeEquivalentTo(new
        {
            result = (GenerationStepResult)new GenerationStepResult.Failed(GenerationFailureKind.SignedOut, "Fake agent isn't signed in: sign in in Settings → AI agents."),
            Saved = false,
        });
    }

    [Fact]
    public async Task Without_the_extracted_strings_the_stage_fails()
    {
        // Arrange
        File.Delete(_h.Files.SourcePath);
        var recording = Recording(SaveGlossary(RussianGlossary), Done());

        // Act
        var result = await RunAsync(recording);

        // Assert
        result.Should().BeOfType<GenerationStepResult.Failed>().Which.Kind.Should().Be(GenerationFailureKind.Toolkit);
    }
}
