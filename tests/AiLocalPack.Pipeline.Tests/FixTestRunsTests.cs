using AiLocalPack.Agents;
using AiLocalPack.Core.Fonts;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Fonts;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Recipes.Agent;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>The agent's <c>test_run</c> during a fix: the app builds the translation, runs the game and answers.</summary>
public sealed class FixTestRunsTests : IDisposable
{
    private readonly StageHarness _h = new();
    private readonly LocalPackStore _packs;
    private readonly FakeHttpHandler _http = new();

    public FixTestRunsTests()
    {
        _packs = new LocalPackStore(AppPaths.UnderHome(_h.Dir.Combine("home")));
        Directory.CreateDirectory(Path.Combine(_h.Copy, "data"));
        File.WriteAllText(Path.Combine(_h.Copy, "data", "text.txt"), "Hello");
        SourceStringsFile.Write(_h.Files, new SourceStrings(new EngineKind(GameEngine.Unknown, null), AgentScriptTool.ToolId, new Version(1, 0, 0), ["data/text.txt"], [new ExtractedString("t1", "Hello", null, null, [])]));
        _h.Files.WriteStrings(new StringTable("ru", new Dictionary<string, string> { ["t1"] = "Привет" }));
        _packs.Save(Manifest, new StringTable("ru", new Dictionary<string, string> { ["t1"] = "Привет" }), Report);
    }

    public void Dispose()
    {
        _http.Dispose();
        _h.Dispose();
    }

    private GenerationJobEntry Entry => new(_h.Folder, _h.Context(GenerationStage.Done).Job with { Status = GenerationJobStatus.Completed });

    private static PackManifest Manifest => new(
        "claude-code",
        StageHarness.Game.Id,
        "ru",
        "Claude Code translation",
        [new PackAuthor(LocalPackStore.PlaceholderAuthor)],
        [new PackRevision(1, StageHarness.T0, LocalPackStore.PlaceholderAuthor)],
        PackStatus.Alpha,
        new PackAgent("claude-code", "2.1.319", "sonnet"),
        [new PackBuild("1000", new Dictionary<string, string> { ["data/text.txt"] = new('a', 64) })],
        new PackRecipe("rpgmaker", "1.0.0"),
        null,
        null,
        [],
        [],
        new PackCoverage(1, 1),
        StageHarness.T0,
        StageHarness.T0);

    private static LocalPackReport Report => new("job", StageHarness.T0, TestRunResult.Skipped(TestRunSkip.OtherPlatform, "Windows program"), LocalPackFonts.NotChecked, [], [], 0);

    /// <summary>
    /// What the agent does: asks for a test run as the toolkit server would, runs its rebuild when told to, asks again,
    /// and keeps every answer.
    /// </summary>
    private static ScriptedAgent Testing(List<TestRunAnswer> answers, Func<int, TestRunAsk>? ask = null) => new(async (run, _, token) =>
    {
        var mailbox = new TestRunMailbox(run.Files.Folder);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var number = mailbox.NextNumber();
            mailbox.Ask(ask?.Invoke(number) ?? new TestRunAsk(number, 60));
            TestRunAnswer? answer;
            while ((answer = mailbox.Answer(number)) is null)
            {
                await Task.Delay(20, token);
            }

            answers.Add(answer);
            if (answer.Outcome == "rebuildFirst")
            {
                var output = Path.Combine(run.Request!.WorkingDirectory, "out", "data");
                Directory.CreateDirectory(output);
                await File.WriteAllTextAsync(Path.Combine(output, "text.txt"), "Привет", token);
            }
        }

        return [new AgentToolCall("1", "mcp__ailocalpack__test_run", "{}"), new AgentDone(AgentOutcome.Succeeded, "Checked.")];
    });

    private FixRunner Runner(IAgentAdapter agent, IGameTestRunner testRunner) => new(
        StageHarness.Game,
        new PipelineAgent(agent, StageHarness.Server),
        new NoBuild(),
        _packs,
        _h.Clock,
        (RecipeTools.Builtin, new BuildServices(new FontFixer(FontWhitelist.Bundled, new FontStore(_h.Dir.Combine("cache"), _http.CreateClient())), _packs, testRunner)));

    [Fact]
    public async Task An_unknown_engines_first_test_run_asks_the_agent_to_rebuild_first()
    {
        // Arrange
        var answers = new List<TestRunAnswer>();
        var runner = Runner(Testing(answers), new FakeTestRunner(new TestRunResult(TestRunOutcome.Passed, Ran: TimeSpan.FromSeconds(50))));

        // Act
        await runner.SendAsync(Entry, new FixRequest("The game crashes.", []), null, TestContext.Current.CancellationToken);

        // Assert
        answers.Select(a => (a.Outcome, a.RunsLeft)).Should().Equal(("rebuildFirst", 5), ("passed", 4));
    }

    [Fact]
    public async Task The_game_runs_with_the_files_the_agents_rebuild_wrote_and_the_seconds_it_asked_for()
    {
        // Arrange
        var testRunner = new FakeTestRunner(new TestRunResult(TestRunOutcome.Passed, Ran: TimeSpan.FromSeconds(60)));
        var runner = Runner(Testing([]), testRunner);

        // Act
        await runner.SendAsync(Entry, new FixRequest("The game crashes.", []), null, TestContext.Current.CancellationToken);

        // Assert
        testRunner.Requests.Select(r => (Files: string.Join(",", r.BuildFiles), r.AtLeast)).Should().Equal(("data/text.txt", (TimeSpan?)TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public async Task The_game_run_shows_as_a_numbered_step()
    {
        // Arrange
        var progress = new RecordingProgress<FixProgress>();
        var runner = Runner(Testing([]), new FakeTestRunner(new TestRunResult(TestRunOutcome.Passed, Ran: TimeSpan.FromSeconds(50))));

        // Act
        await runner.SendAsync(Entry, new FixRequest("The game crashes.", []), progress, TestContext.Current.CancellationToken);

        // Assert
        progress.Reports.SelectMany(p => p.Steps).Should().Contain(FixSteps.TestRun(1, FixTestRuns.MaxRuns + FixTestRuns.MaxQuickRuns)).And.NotContain(FixSteps.TestRunTool);
    }

    [Fact]
    public async Task What_the_test_runs_left_is_removed_when_the_fix_run_ends()
    {
        // Arrange
        var runner = Runner(Testing([]), new FakeTestRunner(new TestRunResult(TestRunOutcome.Passed, Ran: TimeSpan.FromSeconds(50))));

        // Act
        await runner.SendAsync(Entry, new FixRequest("The game crashes.", []), null, TestContext.Current.CancellationToken);

        // Assert
        new
        {
            Mailbox = Directory.Exists(new TestRunMailbox(_h.Folder).Folder),
            Build = Directory.Exists(Path.Combine(_h.Folder, "test-build")),
            Shots = Directory.Exists(Path.Combine(_h.Files.AgentFolder, FixTestRuns.FolderName)),
        }.Should().Be(new { Mailbox = false, Build = false, Shots = false });
    }

    [Fact]
    public async Task A_quick_run_counts_against_the_quick_runs_only()
    {
        // Arrange
        var answers = new List<TestRunAnswer>();
        var testRunner = new FakeTestRunner(new TestRunResult(TestRunOutcome.Passed, Ran: TimeSpan.FromSeconds(20)));
        var runner = Runner(Testing(answers, n => new TestRunAsk(n, null, Quick: true)), testRunner);

        // Act
        await runner.SendAsync(Entry, new FixRequest("The game crashes.", []), null, TestContext.Current.CancellationToken);

        // Assert
        new { Last = (answers[^1].RunsLeft, answers[^1].QuickRunsLeft), Quick = testRunner.Requests.Single().Quick }
            .Should().Be(new { Last = (FixTestRuns.MaxRuns, (int?)(FixTestRuns.MaxQuickRuns - 1)), Quick = true });
    }

    [Fact]
    public async Task Only_the_files_the_agent_names_are_laid_over_the_game()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_h.Copy, "data", "more.txt"), "Bye");
        var testRunner = new FakeTestRunner(new TestRunResult(TestRunOutcome.Passed, Ran: TimeSpan.FromSeconds(20)));
        var agent = new ScriptedAgent(async (run, _, token) =>
        {
            var mailbox = new TestRunMailbox(run.Files.Folder);
            var output = Path.Combine(run.Request!.WorkingDirectory, "out", "data");
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "text.txt"), "Привет", token);
            await File.WriteAllTextAsync(Path.Combine(output, "more.txt"), "Пока", token);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var number = mailbox.NextNumber();
                mailbox.Ask(new TestRunAsk(number, null, Quick: true, Files: ["data/more.txt"]));
                while (mailbox.Answer(number) is null)
                {
                    await Task.Delay(20, token);
                }
            }

            return [new AgentDone(AgentOutcome.Succeeded, "Checked.")];
        });
        var runner = Runner(agent, testRunner);

        // Act
        await runner.SendAsync(Entry, new FixRequest("The game crashes.", []), null, TestContext.Current.CancellationToken);

        // Assert
        testRunner.Requests.Select(r => string.Join(",", r.BuildFiles)).Should().NotBeEmpty().And.AllBe("data/more.txt");
    }

    [Fact]
    public async Task A_file_that_wasnt_rebuilt_is_refused_without_running_the_game()
    {
        // Arrange
        var answers = new List<TestRunAnswer>();
        var testRunner = new FakeTestRunner(new TestRunResult(TestRunOutcome.Passed, Ran: TimeSpan.FromSeconds(20)));
        var runner = Runner(Testing(answers, n => new TestRunAsk(n, null, Files: ["data/missing.txt"])), testRunner);

        // Act
        await runner.SendAsync(Entry, new FixRequest("The game crashes.", []), null, TestContext.Current.CancellationToken);

        // Assert
        new { answers[^1].Outcome, Runs = testRunner.Requests.Count }.Should().Be(new { Outcome = "failed", Runs = 0 });
    }

    [Fact]
    public async Task Untranslated_keys_are_refused_for_a_game_read_by_the_agents_scripts()
    {
        // Arrange
        var answers = new List<TestRunAnswer>();
        var runner = Runner(Testing(answers, n => new TestRunAsk(n, null, Untranslated: ["t1"])), new FakeTestRunner(new TestRunResult(TestRunOutcome.Passed, Ran: TimeSpan.FromSeconds(20))));

        // Act
        await runner.SendAsync(Entry, new FixRequest("The game crashes.", []), null, TestContext.Current.CancellationToken);

        // Assert
        answers.Should().AllSatisfy(a => a.Outcome.Should().Be("failed"));
    }

    [Fact]
    public void A_test_run_step_code_reads_back_its_number_and_maximum()
    {
        // Arrange
        var code = FixSteps.TestRun(2, 5);

        // Act
        var parsed = FixSteps.ParseTestRun(code);

        // Assert
        parsed.Should().Be((2, 5));
    }

    /// <summary>A Build stage that succeeds without building (the fix's changes don't matter here).</summary>
    private sealed class NoBuild : IGenerationStage
    {
        public GenerationStage Stage => GenerationStage.Build;

        public Task<GenerationStepResult> RunStepAsync(GenerationStepContext context, CancellationToken cancellationToken) =>
            Task.FromResult<GenerationStepResult>(new GenerationStepResult.StageDone());
    }
}
