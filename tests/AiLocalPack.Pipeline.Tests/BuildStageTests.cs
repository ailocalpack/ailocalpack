using AiLocalPack.Core.Fonts;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Fonts;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>Stage Build: rebuild, font swap, test run, local pack (task 51).</summary>
public sealed class BuildStageTests : IDisposable
{
    private static readonly Game Game = StageHarness.Game with { Version = GameVersion.FromBuildId("1000") };

    private readonly StageHarness _h = new();
    private readonly FakeHttpHandler _http = new();
    private readonly LocalPackStore _store;

    public BuildStageTests()
    {
        _h.WithRpgMakerCopy();
        _store = new LocalPackStore(AppPaths.UnderHome(_h.Dir.Combine("home")));
    }

    public void Dispose()
    {
        _http.Dispose();
        _h.Dispose();
    }

    private FakeTestRunner Runner { get; } = new(TestRunResult.Skipped(TestRunSkip.OtherPlatform, "Game.exe is a Windows program"));

    private async Task<SourceStrings> ExtractAsync()
    {
        await new ExtractStage(RecipeTools.Builtin).RunStepAsync(_h.Context(GenerationStage.Extract), TestContext.Current.CancellationToken);
        return SourceStringsFile.Read(_h.Files)!;
    }

    /// <summary>The fixture extracted and every string stored as <paramref name="translate"/> makes it.</summary>
    private async Task TranslateAsync(Func<string, string> translate)
    {
        var source = await ExtractAsync();
        _h.Files.WriteStrings(new StringTable("ru", source.Strings.ToDictionary(s => s.Key, s => translate(s.Original))));
    }

    private Task TranslateAllAsync() => TranslateAsync(original => "RU " + original);

    private BuildStage Stage(Game? game = null, IGameTestRunner? runner = null) => new(
        game ?? Game,
        new PipelineAgent(ScriptedAgent.Translating(), StageHarness.Server),
        RecipeTools.Builtin,
        new BuildServices(
            new FontFixer(FontWhitelist.Bundled, new FontStore(_h.Dir.Combine("cache"), _http.CreateClient())),
            _store,
            runner ?? Runner));

    private static TestRunResult Crash => TestRunResult.Crashed(TimeSpan.FromSeconds(4), 3, "the game closed with exit code 3");

    /// <summary>A Build stage that hands a crash to <paramref name="agent"/> (the stage's repair run).</summary>
    private Task<GenerationStepResult> RepairAsync(ScriptedAgent agent, IGameTestRunner runner) =>
        new BuildStage(
            Game,
            new PipelineAgent(agent, StageHarness.Server),
            RecipeTools.Builtin,
            new BuildServices(new FontFixer(FontWhitelist.Bundled, new FontStore(_h.Dir.Combine("cache"), _http.CreateClient())), _store, runner),
            repair: true).RunStepAsync(Context("job"), TestContext.Current.CancellationToken);

    /// <summary>An agent that changes one stored translation (or nothing) and replies.</summary>
    private static ScriptedAgent Repairing(bool change, string reply = "Shortened a name.") => new((run, _, _) =>
    {
        if (change)
        {
            var table = run.Files.ReadStrings()!;
            var strings = new Dictionary<string, string>(table.Strings, StringComparer.Ordinal);
            var key = strings.Keys.Order(StringComparer.Ordinal).First();
            strings[key] = "RU short";
            run.Files.WriteStrings(new StringTable("ru", strings));
        }

        return Task.FromResult<IReadOnlyList<Agents.AgentEvent>>([ScriptedAgent.Usage, new Agents.AgentDone(Agents.AgentOutcome.Succeeded, reply)]);
    });

    private Task<GenerationStepResult> RunAsync(Game? game = null, IGameTestRunner? runner = null, string job = "job") =>
        Stage(game, runner).RunStepAsync(Context(job), TestContext.Current.CancellationToken);

    private GenerationStepContext Context(string job)
    {
        var context = _h.Context(GenerationStage.Build, counters: new GenerationCounters(64, 64, 2, 2));
        return job == "job" ? context : new GenerationStepContext(new GenerationJobEntry(_h.Folder, context.Job with { Id = job }), _h.Clock);
    }

    [Fact]
    public async Task The_saved_local_pack_passes_the_registry_checks()
    {
        // Arrange
        await TranslateAllAsync();

        // Act
        await RunAsync();

        // Assert
        PackValidator.Validate(_store.List().Single().Folder, TestContext.Current.CancellationToken).Problems.Should().BeEmpty();
    }

    [Fact]
    public async Task The_stage_is_done_with_the_accepted_translations_counted()
    {
        // Arrange
        await TranslateAllAsync();

        // Act
        var result = await RunAsync();

        // Assert
        result.Should().Be(new GenerationStepResult.StageDone(new GenerationCounters(64, 64, 2, 2)));
    }

    [Fact]
    public async Task The_pack_covers_every_translated_string()
    {
        // Arrange
        var source = await ExtractAsync();
        _h.Files.WriteStrings(new StringTable("ru", source.Strings.Take(40).ToDictionary(s => s.Key, s => "RU " + s.Original)));

        // Act
        await RunAsync();

        // Assert
        var pack = _store.List().Single();
        new { pack.Manifest.Coverage, Untranslated = pack.Report.Untranslated.Count }.Should().Be(new { Coverage = new PackCoverage(40, 64), Untranslated = 24 });
    }

    [Fact]
    public async Task The_rebuilt_files_go_to_the_build_folder_and_the_copy_stays_as_it_was()
    {
        // Arrange
        await TranslateAllAsync();
        var before = File.ReadAllText(Path.Combine(_h.Copy, "data", "Items.json"));

        // Act
        await RunAsync();

        // Assert
        new
        {
            Rebuilt = File.ReadAllText(Path.Combine(_h.Files.BuildFolder, "data", "Items.json")).Contains("RU ", StringComparison.Ordinal),
            Copy = File.ReadAllText(Path.Combine(_h.Copy, "data", "Items.json")) == before,
        }.Should().Be(new { Rebuilt = true, Copy = true });
    }

    [Fact]
    public async Task The_test_run_gets_the_rebuilt_files()
    {
        // Arrange
        await TranslateAllAsync();

        // Act
        await RunAsync();

        // Assert
        Runner.Requests.Single().BuildFiles.Should().Contain("data/Items.json");
    }

    [Fact]
    public async Task Letters_the_games_font_lacks_swap_in_a_whitelisted_font()
    {
        // Arrange
        await TranslateAsync(original => "Жёлтый " + original);

        // Act
        await RunAsync();

        // Assert
        var pack = _store.List().Single();
        new { Fonts = string.Join(",", pack.Manifest.Fonts), pack.Report.Fonts.Replacement }.Should().Be(new { Fonts = "noto-sans", Replacement = (string?)"noto-sans" });
    }

    [Fact]
    public async Task The_build_step_and_the_font_swap_go_to_the_progress_file()
    {
        // Arrange
        await TranslateAsync(original => "Жёлтый " + original);

        // Act
        await RunAsync();

        // Assert
        var build = PipelineProgressFile.Read(_h.Files).Build!;
        new { build.Step, Fonts = build.SwappedFonts?.Count > 0, build.Replacement, Files = build.Files > 0 }
            .Should().Be(new { Step = BuildStep.Packing, Fonts = true, Replacement = (string?)"Noto Sans", Files = true });
    }

    [Fact]
    public async Task A_crash_in_the_test_run_leaves_the_progress_at_the_test_run()
    {
        // Arrange
        await TranslateAllAsync();
        var runner = new FakeTestRunner(TestRunResult.Crashed(TimeSpan.FromSeconds(4), 3, "the game closed with exit code 3"));

        // Act
        await RunAsync(runner: runner);

        // Assert
        PipelineProgressFile.Read(_h.Files).Build!.Step.Should().Be(BuildStep.TestRun);
    }

    [Fact]
    public async Task A_skipped_test_run_is_kept_in_the_report()
    {
        // Arrange
        await TranslateAllAsync();

        // Act
        await RunAsync();

        // Assert
        _store.List().Single().Report.TestRun.Skip.Should().Be(TestRunSkip.OtherPlatform);
    }

    [Fact]
    public async Task A_crash_in_the_test_run_fails_the_stage()
    {
        // Arrange
        await TranslateAllAsync();
        var runner = new FakeTestRunner(TestRunResult.Crashed(TimeSpan.FromSeconds(4), 3, "the game closed with exit code 3"));

        // Act
        var result = await RunAsync(runner: runner);

        // Assert
        result.Should().BeOfType<GenerationStepResult.Failed>().Which.Kind.Should().Be(GenerationFailureKind.TestRun);
    }

    [Fact]
    public async Task A_crash_in_the_test_run_saves_no_pack()
    {
        // Arrange
        await TranslateAllAsync();
        var runner = new FakeTestRunner(TestRunResult.Crashed(TimeSpan.FromSeconds(4), 3, "the game closed with exit code 3"));

        // Act
        await RunAsync(runner: runner);

        // Assert
        _store.List().Should().BeEmpty();
    }

    [Fact]
    public async Task A_crash_in_the_test_run_writes_what_it_found_with_the_rebuilt_files_and_their_strings()
    {
        // Arrange
        await TranslateAllAsync();
        var runner = new FakeTestRunner(TestRunResult.Crashed(TimeSpan.FromSeconds(4), 3, "the game closed with exit code 3"));

        // Act
        await RunAsync(runner: runner);

        // Assert
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(_h.Files.BuildFailurePath));
        var items = json.RootElement.GetProperty("files").EnumerateArray().Single(f => f.GetProperty("path").GetString() == "data/Items.json");
        new
        {
            ExitCode = json.RootElement.GetProperty("testRun").GetProperty("exitCode").GetInt32(),
            Strings = items.GetProperty("strings").GetInt32() > 0,
            Longest = items.GetProperty("longest").EnumerateArray().First().GetProperty("translation").GetString()!.StartsWith("RU ", StringComparison.Ordinal),
        }.Should().Be(new { ExitCode = 3, Strings = true, Longest = true });
    }

    [Fact]
    public async Task The_failure_report_goes_when_the_stage_runs_again()
    {
        // Arrange
        await TranslateAllAsync();
        await RunAsync(runner: new FakeTestRunner(TestRunResult.Crashed(TimeSpan.FromSeconds(4), 3, "the game closed with exit code 3")));

        // Act
        await RunAsync();

        // Assert
        File.Exists(_h.Files.BuildFailurePath).Should().BeFalse();
    }

    [Fact]
    public async Task A_crash_the_agent_fixes_passes_on_the_second_build()
    {
        // Arrange
        await TranslateAllAsync();
        var runner = new FakeTestRunner(Crash, TestRunResult.Passed(TimeSpan.FromSeconds(60)));

        // Act
        var result = await RepairAsync(Repairing(change: true), runner);

        // Assert
        new { Done = result is GenerationStepResult.StageDone, Runs = runner.Requests.Count }.Should().Be(new { Done = true, Runs = 2 });
    }

    [Fact]
    public async Task The_repair_prompt_names_the_rebuilt_files_and_the_report()
    {
        // Arrange
        await TranslateAllAsync();
        var agent = Repairing(change: false);

        // Act
        await RepairAsync(agent, new FakeTestRunner(Crash));

        // Assert
        var prompt = agent.Runs.Single().Prompt;
        new { Files = prompt.Contains("- `data/Items.json`", StringComparison.Ordinal), Report = prompt.Contains("`build-failure.json`", StringComparison.Ordinal) }
            .Should().Be(new { Files = true, Report = true });
    }

    [Fact]
    public async Task The_agent_reads_the_report_in_its_working_folder()
    {
        // Arrange
        await TranslateAllAsync();
        var seen = false;
        var agent = new ScriptedAgent((run, _, _) =>
        {
            seen = File.Exists(Path.Combine(run.Request!.WorkingDirectory, BuildRepair.ReportCopy));
            return Task.FromResult<IReadOnlyList<Agents.AgentEvent>>([ScriptedAgent.Done]);
        });

        // Act
        await RepairAsync(agent, new FakeTestRunner(Crash));

        // Assert
        seen.Should().BeTrue();
    }

    [Fact]
    public async Task A_repair_that_changes_nothing_fails_without_building_again()
    {
        // Arrange
        await TranslateAllAsync();
        var runner = new FakeTestRunner(Crash);

        // Act
        var result = await RepairAsync(Repairing(change: false, "The game reads a file I can't change."), runner);

        // Assert
        new { Message = ((GenerationStepResult.Failed)result).Message.Contains("found no fix: The game reads a file I can't change.", StringComparison.Ordinal), Runs = runner.Requests.Count }
            .Should().Be(new { Message = true, Runs = 1 });
    }

    [Fact]
    public async Task A_crash_after_the_repair_fails_the_stage_with_what_the_agent_tried()
    {
        // Arrange
        await TranslateAllAsync();

        // Act
        var result = await RepairAsync(Repairing(change: true), new FakeTestRunner(Crash));

        // Assert
        result.Should().BeOfType<GenerationStepResult.Failed>().Which.Message.Should().StartWith("Still crashes after Scripted agent's fix (Shortened a name.).");
    }

    [Fact]
    public async Task Each_repair_attempt_and_its_outcome_are_kept_for_the_next_one()
    {
        // Arrange
        await TranslateAllAsync();

        // Act
        await RepairAsync(Repairing(change: true), new FakeTestRunner(Crash));

        // Assert
        var attempts = File.ReadAllText(Path.Combine(_h.Folder, BuildRepair.AttemptsFile));
        new
        {
            Attempt = attempts.Contains("## Attempt 1", StringComparison.Ordinal),
            Reply = attempts.Contains("Shortened a name.", StringComparison.Ordinal),
            Outcome = attempts.Contains("Test run after it: crashed again.", StringComparison.Ordinal),
        }.Should().Be(new { Attempt = true, Reply = true, Outcome = true });
    }

    [Fact]
    public async Task A_stage_without_repair_doesnt_run_the_agent_on_a_crash()
    {
        // Arrange
        await TranslateAllAsync();
        var agent = Repairing(change: true);
        var stage = new BuildStage(Game, new PipelineAgent(agent, StageHarness.Server), RecipeTools.Builtin,
            new BuildServices(new FontFixer(FontWhitelist.Bundled, new FontStore(_h.Dir.Combine("cache"), _http.CreateClient())), _store, new FakeTestRunner(Crash)));

        // Act
        await stage.RunStepAsync(Context("job"), TestContext.Current.CancellationToken);

        // Assert
        agent.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task Nothing_translated_fails_the_stage()
    {
        // Arrange
        await ExtractAsync();

        // Act
        var result = await RunAsync();

        // Assert
        result.Should().BeOfType<GenerationStepResult.Failed>().Which.Kind.Should().Be(GenerationFailureKind.Agent);
    }

    [Fact]
    public async Task Building_again_replaces_the_jobs_pack()
    {
        // Arrange
        await TranslateAllAsync();
        await RunAsync();

        // Act
        await RunAsync();

        // Assert
        _store.List().Select(p => p.Manifest.Id).Should().Equal("scripted");
    }

    [Fact]
    public async Task Another_jobs_pack_keeps_its_id()
    {
        // Arrange
        await TranslateAllAsync();
        await RunAsync(job: "earlier");

        // Act
        await RunAsync();

        // Assert
        _store.List().Select(p => (p.Report.Job, p.Manifest.Id)).Should().Equal(("earlier", "scripted"), ("job", "scripted-2"));
    }

    [Fact]
    public async Task An_overlay_left_by_an_interrupted_test_run_is_undone_first()
    {
        // Arrange
        await TranslateAllAsync();
        var before = File.ReadAllText(Path.Combine(_h.Copy, "data", "Items.json"));
        _h.Dir.File("work/steam-620/job/build/data/Items.json", "rebuilt");
        TestRunOverlay.Apply(_h.Folder, _h.Copy, _h.Files.BuildFolder, ["data/Items.json"], []);

        // Act
        await RunAsync();

        // Assert
        File.ReadAllText(Path.Combine(_h.Copy, "data", "Items.json")).Should().Be(before);
    }

    [Fact]
    public async Task A_steam_game_without_a_build_id_fails_the_stage()
    {
        // Arrange
        await TranslateAllAsync();

        // Act
        var result = await RunAsync(StageHarness.Game);

        // Assert
        result.Should().BeOfType<GenerationStepResult.Failed>().Which.Kind.Should().Be(GenerationFailureKind.Toolkit);
    }

    [Fact]
    public async Task Without_the_extracted_strings_the_stage_fails()
    {
        // Act
        var result = await RunAsync();

        // Assert
        result.Should().BeOfType<GenerationStepResult.Failed>().Which.Kind.Should().Be(GenerationFailureKind.Toolkit);
    }
}

/// <summary>Returns the results in order (the last one for every run after) and records the requests.</summary>
internal sealed class FakeTestRunner(params TestRunResult[] results) : IGameTestRunner
{
    public List<TestRunRequest> Requests { get; } = [];

    public Task<TestRunResult> RunAsync(TestRunRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(results[Math.Min(Requests.Count, results.Length) - 1]);
    }
}
