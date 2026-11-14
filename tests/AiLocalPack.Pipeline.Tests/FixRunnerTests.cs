using AiLocalPack.Agents;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>Fixes with AI of a completed job's translation (task 75): drafts, plans, putting back, going back.</summary>
public sealed class FixRunnerTests : IDisposable
{
    private readonly StageHarness _h = new();
    private readonly LocalPackStore _packs;

    public FixRunnerTests()
    {
        _packs = new LocalPackStore(AppPaths.UnderHome(_h.Dir.Combine("home")));
        Directory.CreateDirectory(_h.Copy);
        var strings = Enumerable.Range(1, 80).Select(i => new ExtractedString($"k{i:000}", $"Text {i}", null, null, [])).ToList();
        SourceStringsFile.Write(_h.Files, new SourceStrings(new EngineKind(GameEngine.RpgMaker, "mz"), "rpgmaker", new Version(1, 0, 0), ["data/Map001.json"], strings));
        _h.Files.WriteStrings(new StringTable("ru", strings.ToDictionary(s => s.Key, s => "RU " + s.Original)));
        _packs.Save(Manifest, Table("RU"), Report(StageHarness.T0));
    }

    public void Dispose() => _h.Dispose();

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
        [new PackBuild("1000", new Dictionary<string, string> { ["data/Map001.json"] = new('a', 64) })],
        new PackRecipe("rpgmaker", "1.0.0"),
        null,
        null,
        [],
        [],
        new PackCoverage(80, 80),
        StageHarness.T0,
        StageHarness.T0);

    private static StringTable Table(string prefix) => new("ru", Enumerable.Range(1, 80).ToDictionary(i => $"k{i:000}", i => $"{prefix} Text {i}"));

    private static LocalPackReport Report(DateTimeOffset created) =>
        new("job", created, TestRunResult.Skipped(TestRunSkip.OtherPlatform, "Windows program"), LocalPackFonts.NotChecked, [], [], 0);

    /// <summary>A Build stage that saves the job's strings as the next pack (or fails).</summary>
    private sealed class FakeBuild(LocalPackStore packs, bool fail = false) : IGenerationStage
    {
        public int Runs { get; private set; }

        public GenerationStage Stage => GenerationStage.Build;

        public Task<GenerationStepResult> RunStepAsync(GenerationStepContext context, CancellationToken cancellationToken)
        {
            Runs++;
            if (fail)
            {
                return Task.FromResult<GenerationStepResult>(new GenerationStepResult.Failed(GenerationFailureKind.TestRun, "Test run failed: the game closed with exit code 3."));
            }

            packs.Save(Manifest, context.Files.ReadStrings()!, new LocalPackReport("job", context.Time.GetUtcNow(), new TestRunResult(TestRunOutcome.Passed, null, null, TimeSpan.FromSeconds(15), null), LocalPackFonts.NotChecked, [], [], 0));
            return Task.FromResult<GenerationStepResult>(new GenerationStepResult.StageDone());
        }
    }

    /// <summary>An agent run that changes the first <paramref name="count"/> translations and replies.</summary>
    private static ScriptedAgent Changing(int count, string reply = "Fixed.") => new((run, _, _) =>
    {
        var table = run.Files.ReadStrings()!;
        var strings = table.Strings.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        foreach (var key in strings.Keys.Order(StringComparer.Ordinal).Take(count).ToList())
        {
            strings[key] = "FIX " + strings[key];
        }

        run.Files.WriteStrings(new StringTable("ru", strings));
        return Task.FromResult<IReadOnlyList<AgentEvent>>(
            [new AgentToolCall("1", "mcp__ailocalpack__put_translations", "{}"), new AgentDone(AgentOutcome.Succeeded, reply)]);
    });

    private FixRunner Runner(IAgentAdapter agent, IGenerationStage? build = null) =>
        new(StageHarness.Game, new PipelineAgent(agent, StageHarness.Server), build ?? new FakeBuild(_packs), _packs, _h.Clock);

    private static FixRequest Request(string text = "The game crashes.") => new(text, [FixAttachment.Changes()]);

    [Fact]
    public async Task A_fix_of_a_few_strings_builds_the_next_draft()
    {
        // Arrange
        var runner = Runner(Changing(3));
        _h.Clock.Advance(TimeSpan.FromHours(1));

        // Act
        var turn = await runner.SendAsync(Entry, Request(), null, TestContext.Current.CancellationToken);

        // Assert
        (turn.Outcome, turn.Change?.Draft, turn.Change?.Strings, _packs.FindByJob(StageHarness.Game.Id, "job")!.Report.DraftNumber)
            .Should().Be((FixTurnOutcome.Changed, 2, 3, 2));
    }

    [Fact]
    public async Task The_change_card_shows_samples_of_what_changed()
    {
        // Arrange
        var runner = Runner(Changing(3));

        // Act
        var turn = await runner.SendAsync(Entry, Request(), null, TestContext.Current.CancellationToken);

        // Assert
        turn.Change!.Samples[0].Should().Be(new FixStringChange("k001", "Text 1", "RU Text 1", "FIX RU Text 1"));
    }

    [Fact]
    public async Task The_turn_lists_the_agents_tool_calls_and_the_apps_build()
    {
        // Arrange
        var runner = Runner(Changing(3));

        // Act
        var turn = await runner.SendAsync(Entry, Request(), null, TestContext.Current.CancellationToken);

        // Assert
        turn.Steps.Should().Equal("put_translations", FixSteps.Build, FixSteps.Saved);
    }

    [Fact]
    public async Task The_earlier_draft_is_kept_for_going_back()
    {
        // Arrange
        var runner = Runner(Changing(3));
        await runner.SendAsync(Entry, Request(), null, TestContext.Current.CancellationToken);

        // Act
        var back = FixRunner.GoBack(Entry, 1, _packs, _h.Clock);

        // Assert
        (back.Report.DraftNumber, _h.Files.ReadStrings()!.Strings["k001"]).Should().Be((1, "RU Text 1"));
    }

    [Fact]
    public async Task Going_back_keeps_the_later_draft_too()
    {
        // Arrange
        var runner = Runner(Changing(3));
        await runner.SendAsync(Entry, Request(), null, TestContext.Current.CancellationToken);
        FixRunner.GoBack(Entry, 1, _packs, _h.Clock);

        // Act
        var forward = FixRunner.GoBack(Entry, 2, _packs, _h.Clock);

        // Assert
        (forward.Report.DraftNumber, _h.Files.ReadStrings()!.Strings["k001"]).Should().Be((2, "FIX RU Text 1"));
    }

    [Fact]
    public async Task More_than_50_changed_strings_become_a_plan_and_the_job_is_put_back()
    {
        // Arrange
        var build = new FakeBuild(_packs);
        var runner = Runner(Changing(60), build);

        // Act
        var turn = await runner.SendAsync(Entry, Request("Use «вы» everywhere."), null, TestContext.Current.CancellationToken);

        // Assert
        (turn.Outcome, turn.Plan?.Strings, turn.Plan?.State, build.Runs, _h.Files.ReadStrings()!.Strings["k001"])
            .Should().Be((FixTurnOutcome.Planned, 60, FixPlanState.Waiting, 0, "RU Text 1"));
    }

    [Fact]
    public async Task Applying_the_plan_builds_its_changes_without_the_agent()
    {
        // Arrange
        var agent = Changing(60);
        var runner = Runner(agent);
        var plan = await runner.SendAsync(Entry, Request("Use «вы» everywhere."), null, TestContext.Current.CancellationToken);

        // Act
        var applied = await runner.ApplyPlanAsync(Entry, plan.Id, null, TestContext.Current.CancellationToken);

        // Assert
        (applied.Plan?.State, applied.Change?.Draft, applied.Change?.Strings, agent.Runs.Count).Should().Be((FixPlanState.Applied, 2, 60, 1));
    }

    [Fact]
    public async Task Dropping_the_plan_changes_nothing()
    {
        // Arrange
        var runner = Runner(Changing(60));
        var plan = await runner.SendAsync(Entry, Request("Use «вы» everywhere."), null, TestContext.Current.CancellationToken);

        // Act
        var dropped = runner.DropPlan(Entry, plan.Id);

        // Assert
        (dropped.Plan?.State, Directory.Exists(new FixFiles(_h.Folder).PendingFolder)).Should().Be((FixPlanState.Dropped, false));
    }

    [Fact]
    public async Task An_answer_without_changes_keeps_the_draft_and_offers_the_choices()
    {
        // Arrange
        var runner = Runner(new ScriptedAgent((_, _, _) => Task.FromResult<IReadOnlyList<AgentEvent>>(
            [new AgentDone(AgentOutcome.Succeeded, "Which shop?\nChoices: The first | The second")])));

        // Act
        var turn = await runner.SendAsync(Entry, Request("The shopkeeper is rude."), null, TestContext.Current.CancellationToken);

        // Assert
        (turn.Outcome, turn.Text, string.Join("|", turn.Choices), _packs.FindByJob(StageHarness.Game.Id, "job")!.Report.DraftNumber)
            .Should().Be((FixTurnOutcome.Answered, "Which shop?", "The first|The second", 1));
    }

    [Fact]
    public async Task A_build_that_fails_puts_the_job_back()
    {
        // Arrange
        var runner = Runner(Changing(3), new FakeBuild(_packs, fail: true));

        // Act
        var turn = await runner.SendAsync(Entry, Request(), null, TestContext.Current.CancellationToken);

        // Assert
        (turn.Outcome, turn.Problem, _h.Files.ReadStrings()!.Strings["k001"]).Should().Be((FixTurnOutcome.Failed, "Test run failed: the game closed with exit code 3.", "RU Text 1"));
    }

    [Fact]
    public async Task A_failed_agent_run_puts_the_job_back()
    {
        // Arrange
        var runner = Runner(new ScriptedAgent((run, _, _) =>
        {
            run.Files.WriteStrings(new StringTable("ru", new Dictionary<string, string> { ["k001"] = "half done" }));
            return Task.FromResult<IReadOnlyList<AgentEvent>>([new AgentDone(AgentOutcome.Failed, Message: "boom")]);
        }));

        // Act
        var turn = await runner.SendAsync(Entry, Request(), null, TestContext.Current.CancellationToken);

        // Assert
        (turn.Outcome, _h.Files.ReadStrings()!.Strings["k001"]).Should().Be((FixTurnOutcome.Failed, "RU Text 1"));
    }

    [Fact]
    public async Task A_stopped_fix_is_recorded_and_changes_nothing()
    {
        // Arrange
        using var stop = new CancellationTokenSource();
        var runner = Runner(new ScriptedAgent((run, _, token) =>
        {
            run.Files.WriteStrings(new StringTable("ru", new Dictionary<string, string> { ["k001"] = "half done" }));
            stop.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<AgentEvent>>([ScriptedAgent.Done]);
        }));

        // Act
        var act = () => runner.SendAsync(Entry, Request(), null, stop.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        (new FixFiles(_h.Folder).Read().Turns[^1].Outcome, _h.Files.ReadStrings()!.Strings["k001"]).Should().Be((FixTurnOutcome.Stopped, "RU Text 1"));
    }

    [Fact]
    public async Task The_conversation_keeps_the_request_and_the_answer()
    {
        // Arrange
        var runner = Runner(Changing(3));

        // Act
        await runner.SendAsync(Entry, Request(), null, TestContext.Current.CancellationToken);

        // Assert
        new FixFiles(_h.Folder).Read().Turns.Select(t => (t.Id, t.Kind, t.Text)).Should().Equal((1, FixTurnKind.Player, "The game crashes."), (2, FixTurnKind.Agent, "Fixed."));
    }

    [Fact]
    public async Task An_empty_request_is_refused()
    {
        // Arrange
        var runner = Runner(Changing(3));

        // Act
        var act = () => runner.SendAsync(Entry, Request("  "), null, TestContext.Current.CancellationToken);

        // Assert
        (await act.Should().ThrowAsync<FixException>()).Which.Problem.Should().Be(FixProblem.BadRequest);
    }

    [Fact]
    public async Task A_fix_is_refused_while_the_job_is_locked()
    {
        // Arrange
        using var held = GenerationJobLock.TryAcquire(_h.Folder);
        var runner = Runner(Changing(3));

        // Act
        var act = () => runner.SendAsync(Entry, Request(), null, TestContext.Current.CancellationToken);

        // Assert
        (await act.Should().ThrowAsync<FixException>()).Which.Problem.Should().Be(FixProblem.Busy);
    }

    [Fact]
    public void A_fix_left_running_by_a_crash_is_put_back_on_recovery()
    {
        // Arrange
        var fix = new FixFiles(_h.Folder);
        fix.Write(new FixState([FixTurn.FromPlayer(1, StageHarness.T0, "Fix it.", [])], [], 1));
        FixJobState.Capture(_h.Files, fix.BeforeFolder);
        _h.Files.WriteStrings(new StringTable("ru", new Dictionary<string, string> { ["k001"] = "half done" }));

        // Act
        var recovered = FixRunner.Recover(Entry, _h.Clock);

        // Assert
        (recovered, fix.Read().Turns[^1].Outcome, _h.Files.ReadStrings()!.Strings["k001"]).Should().Be((true, FixTurnOutcome.Failed, "RU Text 1"));
    }

    [Fact]
    public async Task The_fix_prompt_carries_the_request_and_the_conversation_so_far()
    {
        // Arrange
        var agent = Changing(1);
        var runner = Runner(agent);
        await runner.SendAsync(Entry, Request("First."), null, TestContext.Current.CancellationToken);

        // Act
        await runner.SendAsync(Entry, Request("Second."), null, TestContext.Current.CancellationToken);

        // Assert
        (agent.Runs[^1].Prompt.Contains("Second.", StringComparison.Ordinal), agent.Runs[^1].Prompt.Contains("- Player: First.", StringComparison.Ordinal),
            agent.Runs[^1].Request!.SystemPrompt!.Contains("A person reads your reply", StringComparison.Ordinal)).Should().Be((true, true, true));
    }
}

/// <summary>The fix prompt's parts (task 75).</summary>
public sealed class FixPromptTests
{
    [Fact]
    public void The_choices_line_is_split_off_the_reply()
    {
        // Act
        var (text, choices) = FixPrompt.SplitChoices("Keep spell names in English inside descriptions too?\nChoices: Yes, everywhere | Only in the spell book | Show me examples");

        // Assert
        (text, string.Join("|", choices)).Should().Be(("Keep spell names in English inside descriptions too?", "Yes, everywhere|Only in the spell book|Show me examples"));
    }

    [Fact]
    public void A_reply_without_choices_is_kept_whole()
    {
        // Act
        var (text, choices) = FixPrompt.SplitChoices("Fixed the separators.");

        // Assert
        (text, choices.Count).Should().Be(("Fixed the separators.", 0));
    }

    [Fact]
    public void A_single_choice_is_no_question()
    {
        // Act
        var (_, choices) = FixPrompt.SplitChoices("Done.\nChoices: OK");

        // Assert
        choices.Should().BeEmpty();
    }

    [Fact]
    public void The_first_request_says_so()
    {
        // Act
        var history = FixPrompt.History([]);

        // Assert
        history.Should().Be("This is the player's first request.");
    }

    [Fact]
    public void The_history_names_what_each_answer_did()
    {
        // Arrange
        var at = StageHarness.T0;
        FixTurn[] turns =
        [
            FixTurn.FromPlayer(1, at, "The game crashes.", []),
            new FixTurn(2, FixTurnKind.Agent, at, "Fixed the separators.", [], [], [], FixTurnOutcome.Changed, new FixChange(2, 3, 0, false, TestRunOutcome.Passed, [])),
        ];

        // Act
        var history = FixPrompt.History(turns);

        // Assert
        history.Should().Be("Earlier in this conversation:\n- Player: The game crashes.\n- You (made Draft 2): Fixed the separators.");
    }

    [Fact]
    public void The_session_attachment_says_how_long_the_game_ran()
    {
        // Arrange
        var context = new FixContext(1, 64, 64, [], new Dictionary<string, string>(), new Dictionary<string, string>());

        // Act
        var text = FixPrompt.Attachments([FixAttachment.Session(TimeSpan.FromSeconds(4.2), 3)], context);

        // Assert
        text.Should().Be("The game session: it closed 4.2 s after it started with exit code 3.");
    }
}
