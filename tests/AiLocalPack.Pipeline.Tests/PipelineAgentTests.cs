using System.Runtime.CompilerServices;
using AiLocalPack.Agents;
using AiLocalPack.Core.Generation;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>What a stage's agent run is given (task 49): only the toolkit server on the game folder, the job's agent folder to work in, prompts, model.</summary>
public sealed class PipelineAgentTests : IDisposable
{
    private readonly StageHarness _h = new();
    private readonly RecordingAdapter _adapter = new();

    public void Dispose() => _h.Dispose();

    private async Task<AgentRunRequest> RunAsync(GenerationOptions? options = null)
    {
        await new PipelineAgent(_adapter, StageHarness.Server).RunAsync(
            _h.Context(GenerationStage.Glossary, options: options),
            PromptTemplate.System,
            PromptValues.System("Sample Quest", "ru"),
            "Sample Quest",
            TestContext.Current.CancellationToken);
        return _adapter.Request!;
    }

    [Fact]
    public async Task The_agent_works_in_its_job_folder_with_only_the_toolkit_server_on_the_game()
    {
        // Arrange
        var expected = StageHarness.Server.For(_h.Copy, _h.Folder, "ru");

        // Act
        var request = await RunAsync();

        // Assert
        new { request.WorkingDirectory, Servers = request.McpServers, Tools = request.BuiltinTools }.Should().BeEquivalentTo(new
        {
            WorkingDirectory = _h.Files.AgentFolder,
            Servers = new[] { expected },
            Tools = new HashSet<AgentBuiltinTool>(),
        });
    }

    [Fact]
    public async Task The_system_prompt_is_appended()
    {
        // Arrange
        var expected = PromptTemplate.System.Render(PromptValues.System("Sample Quest", "ru"));

        // Act
        var request = await RunAsync();

        // Assert
        request.SystemPrompt.Should().Be(expected);
    }

    [Fact]
    public async Task A_job_with_reference_packs_tells_the_glossary_run_about_them()
    {
        // Arrange
        var manifest = new Core.Packs.PackManifest(
            "full", Core.Games.GameId.Steam(620), "en", "Full translation", [new Core.Packs.PackAuthor("alice")],
            [new Core.Packs.PackRevision(1, DateTimeOffset.UnixEpoch, "alice")], Core.Packs.PackStatus.Verified, null,
            [new Core.Packs.PackBuild("1000", new Dictionary<string, string> { ["data/Map001.json"] = new('a', 64) })],
            new Core.Packs.PackRecipe("rpgmaker", "1.0.0"), new Core.Packs.PackStringsFile(Core.Packs.PackStringsFile.DefaultPath, new string('e', 64), 10),
            null, [], [], new Core.Packs.PackCoverage(1, 1), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        new GenerationFiles(_h.Folder).WriteReferences([new ReferencePack(manifest, new Core.Packs.StringTable("en", new Dictionary<string, string>()), true)]);

        // Act
        var request = await RunAsync(new GenerationOptions(References: [new("en", "full", 1)]));

        // Assert
        request.SystemPrompt.Should().Contain("\n\nPlayers already published translations of \"Sample Quest\"");
    }

    [Fact]
    public async Task The_jobs_model_is_used()
    {
        // Arrange
        var options = new GenerationOptions(Model: "opus");

        // Act
        var request = await RunAsync(options);

        // Assert
        request.Model.Should().Be("opus");
    }

    [Fact]
    public async Task A_missing_agent_fails_without_running()
    {
        // Arrange
        _adapter.Installed = false;

        // Act
        var result = await new PipelineAgent(_adapter, StageHarness.Server).RunAsync(
            _h.Context(GenerationStage.Glossary), PromptTemplate.System, PromptValues.System("Sample Quest", "ru"), "Sample Quest", TestContext.Current.CancellationToken);

        // Assert
        new { result.Stop, Ran = _adapter.Request is not null }.Should().BeEquivalentTo(new
        {
            Stop = (GenerationStepResult)new GenerationStepResult.Failed(GenerationFailureKind.AgentMissing, "Recorder isn't installed: install it in Settings → AI agents."),
            Ran = false,
        });
    }

    [Fact]
    public async Task Messages_tool_calls_and_usage_go_to_the_job_log()
    {
        // Arrange
        _adapter.Events = [new AgentMessage("Reading strings."), new AgentToolCall("1", "mcp__ailocalpack__extract_strings", "{}"), new AgentUsage(100, 20)];

        // Act
        await RunAsync();

        // Assert
        _h.Log.Should().Contain("Agent: Reading strings.").And.Contain("Tool call: mcp__ailocalpack__extract_strings").And.Contain("Usage: 100 input, 20 output");
    }

    private sealed class RecordingAdapter : IAgentAdapter
    {
        public bool Installed { get; set; } = true;

        public IReadOnlyList<AgentEvent> Events { get; set; } = [];

        public AgentRunRequest? Request { get; private set; }

        public string Id => "recorder";

        public string DisplayName => "Recorder";

        public Task<AgentInstallation?> DetectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Installed ? new AgentInstallation(Id, "/bin/recorder", "1.0.0", AgentSource.Configured) : null);

        public Task<AgentAuthStatus> GetAuthStatusAsync(AgentInstallation installation, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentAuthStatus(AgentAuthState.Unknown, null));

        public async IAsyncEnumerable<AgentEvent> RunAsync(AgentInstallation installation, AgentRunRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Request = request;
            foreach (var e in Events)
            {
                yield return e;
            }

            await Task.Yield();
            yield return new AgentDone(AgentOutcome.Succeeded, "ok");
        }
    }
}
