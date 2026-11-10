using AiLocalPack.Agents;
using AiLocalPack.Core.Generation;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Recipes.Agent;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>Stage Extract for a game the toolkit can't read: the agent's own extractor (task 57).</summary>
public sealed class UnknownEngineExtractTests : IDisposable
{
    internal const string Strings = """
        {"strings": [
          {"key": "data/text.ltx:11", "original": "{hero}, you came!", "file": "data/text.ltx", "context": "Dialogue", "placeholders": ["{hero}"]},
          {"key": "data/text.ltx:1", "original": "New Game", "file": "data/text.ltx", "context": "Title menu", "maxLength": 20},
          {"key": "data/names.ltx:1", "original": "Mira", "file": "data/names.ltx"}
        ]}
        """;

    private readonly StageHarness _h = new();

    public UnknownEngineExtractTests() => CustomFormatFixture.Create(_h.Copy);

    public void Dispose() => _h.Dispose();

    /// <summary>An agent whose run writes <paramref name="strings"/> (if any) into its workspace's strings.json.</summary>
    internal static ScriptedAgent Extracting(string? strings = Strings) => new((run, _, _) =>
    {
        if (strings is not null)
        {
            File.WriteAllText(Path.Combine(run.Request!.WorkingDirectory, "strings.json"), strings);
        }

        return Task.FromResult<IReadOnlyList<AgentEvent>>([ScriptedAgent.Done]);
    });

    private Task<GenerationStepResult> RunAsync(ScriptedAgent agent) =>
        new ExtractStage(RecipeTools.Builtin, agent: new PipelineAgent(agent, StageHarness.Server), gameName: "Lantern Tale")
            .RunStepAsync(_h.Context(GenerationStage.Extract), TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_agents_strings_are_extracted_into_the_job()
    {
        // Act
        var result = await RunAsync(Extracting());

        // Assert
        result.Should().Be(new GenerationStepResult.StageDone(new GenerationCounters(3, 0, 1, 0)));
    }

    [Fact]
    public async Task Source_json_names_the_agent_script_tool_and_keeps_limits_and_placeholders()
    {
        // Act
        await RunAsync(Extracting());

        // Assert
        var source = SourceStringsFile.Read(_h.Files)!;
        new { source.Tool, Engine = source.Engine.ToString(), source.Files, Placeholders = source.Strings.Single(s => s.Key == "data/text.ltx:11").Placeholders, source.Strings.Single(s => s.Key == "data/text.ltx:1").MaxLength }
            .Should().BeEquivalentTo(new { Tool = AgentScriptTool.ToolId, Engine = "unknown", Files = new[] { "data/names.ltx", "data/text.ltx" }, Placeholders = new[] { "{hero}" }, MaxLength = (int?)20 });
    }

    [Fact]
    public async Task The_agent_works_in_its_workspace_with_file_and_command_tools()
    {
        // Arrange
        var agent = Extracting();

        // Act
        await RunAsync(agent);

        // Assert
        var request = agent.Runs.Single().Request!;
        (request.WorkingDirectory, request.BuiltinTools.Order().ToList()).Should().BeEquivalentTo(
            (_h.Files.AgentFolder, new List<AgentBuiltinTool> { AgentBuiltinTool.ReadFiles, AgentBuiltinTool.EditFiles, AgentBuiltinTool.RunCommands }));
    }

    [Fact]
    public async Task The_agent_is_told_where_the_game_copy_is_and_never_to_change_it()
    {
        // Arrange
        var agent = Extracting();

        // Act
        await RunAsync(agent);

        // Assert
        var request = agent.Runs.Single().Request!;
        (request.Prompt.Contains(_h.Copy, StringComparison.Ordinal), request.SystemPrompt!.Contains("never change it", StringComparison.Ordinal)).Should().Be((true, true));
    }

    [Fact]
    public async Task The_toolkit_server_still_reads_the_working_copy()
    {
        // Arrange
        var agent = Extracting();

        // Act
        await RunAsync(agent);

        // Assert
        var server = agent.Runs.Single().Request!.McpServers.Single();
        server.Arguments.Should().ContainInOrder("--root", _h.Copy);
    }

    [Fact]
    public async Task An_agent_that_writes_no_strings_fails_the_stage_as_an_agent_failure()
    {
        // Act
        var result = await RunAsync(Extracting(strings: null));

        // Assert
        result.Should().BeOfType<GenerationStepResult.Failed>().Which.Should().Match<GenerationStepResult.Failed>(
            f => f.Kind == GenerationFailureKind.Agent && f.Message.Contains("strings.json isn't there", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_duplicate_key_fails_the_stage_with_the_reason()
    {
        // Arrange
        const string twice = """{"strings": [{"key": "a", "original": "One"}, {"key": "a", "original": "Two"}]}""";

        // Act
        var result = await RunAsync(Extracting(twice));

        // Assert
        result.Should().BeOfType<GenerationStepResult.Failed>().Which.Message.Should().Contain("listed twice");
    }

    [Fact]
    public async Task No_strings_at_all_fails_the_stage()
    {
        // Act
        var result = await RunAsync(Extracting("""{"strings": []}"""));

        // Assert
        result.Should().BeOfType<GenerationStepResult.Failed>().Which.Message.Should().Be("The agent's extractor found no text to translate.");
    }

    [Fact]
    public async Task A_limit_during_the_agents_run_pauses_the_job()
    {
        // Arrange
        var reset = StageHarness.T0.AddHours(2);
        var agent = new ScriptedAgent((_, _, _) => Task.FromResult<IReadOnlyList<AgentEvent>>(
            [new AgentLimitReached(reset, "limit"), new AgentDone(AgentOutcome.LimitReached, null)]));

        // Act
        var result = await RunAsync(agent);

        // Assert
        result.Should().Be(new GenerationStepResult.LimitReached(reset, "limit"));
    }
}
