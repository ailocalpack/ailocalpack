using AiLocalPack.Agents;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>Stage Translate: one chunk per step, validation, bounded retries, resume (task 50).</summary>
public sealed class TranslateStageTests : IDisposable
{
    private readonly StageHarness _h = new();
    private GenerationCounters _counters = new();

    public TranslateStageTests()
    {
        _h.WithRpgMakerCopy();
    }

    public void Dispose() => _h.Dispose();

    private IReadOnlyList<TranslationChunk> Chunks => ChunkPlanFile.Read(_h.Files)!;

    private SourceStrings Source => SourceStringsFile.Read(_h.Files)!;

    private async Task ExtractAsync(ChunkBudget? budget = null)
    {
        var result = await new ExtractStage(RecipeTools.Builtin, budget ?? new ChunkBudget(10, 100_000))
            .RunStepAsync(_h.Context(GenerationStage.Extract), TestContext.Current.CancellationToken);
        _counters = ((GenerationStepResult.StageDone)result).Counters!;
    }

    private Task<GenerationStepResult> StepAsync(ScriptedAgent agent, int chunk = 0, int translated = 0, int maxAttempts = 3, int workers = 1) =>
        new TranslateStage(new PipelineAgent(agent, StageHarness.Server), "Sample Quest", RecipeTools.Builtin, new TranslateOptions(maxAttempts, workers))
            .RunStepAsync(
                _h.Context(GenerationStage.Translate, counters: _counters with { ChunksDone = chunk, Translated = translated }),
                TestContext.Current.CancellationToken);

    private IReadOnlyDictionary<string, string> Stored => _h.Files.ReadStrings()?.Strings ?? new Dictionary<string, string>();

    private static ScriptedAgent Agent(Func<ScriptedRun, int, IReadOnlyList<AgentEvent>> script) =>
        new((run, n, _) => Task.FromResult(script(run, n)));

    [Fact]
    public async Task A_step_translates_the_chunk_it_is_at()
    {
        // Arrange
        await ExtractAsync();
        var agent = ScriptedAgent.Translating();

        // Act
        await StepAsync(agent, chunk: 1);

        // Assert
        Stored.Keys.Should().BeEquivalentTo(Chunks[1].Keys);
    }

    [Fact]
    public async Task A_step_reports_the_strings_it_translated()
    {
        // Arrange
        await ExtractAsync();
        var agent = ScriptedAgent.Translating();

        // Act
        var result = await StepAsync(agent);

        // Assert
        result.Should().Be(new GenerationStepResult.ChunkDone(Chunks[0].Keys.Count));
    }

    [Fact]
    public async Task The_prompt_lists_the_chunks_strings_in_order()
    {
        // Arrange
        await ExtractAsync();
        var agent = ScriptedAgent.Translating();

        // Act
        await StepAsync(agent, chunk: 2);

        // Assert
        agent.Runs.Single().Keys.Should().Equal(Chunks[2].Keys);
    }

    [Fact]
    public async Task The_prompt_gives_only_the_glossary_entries_its_strings_mention()
    {
        // Arrange
        await ExtractAsync();
        _h.Files.WriteGlossary(new Glossary("ru", "informal, ты", [new GlossaryEntry("Harold", "Гарольд"), new GlossaryEntry("Zzyzx", "Ззыкс")]));
        var chunk = Chunks.Select((c, i) => (c, i)).First(x => x.c.Keys.Any(k => Source.Strings.Single(s => s.Key == k).Original.Contains("Harold", StringComparison.Ordinal))).i;
        var agent = ScriptedAgent.Translating();

        // Act
        await StepAsync(agent, chunk);

        // Assert
        var prompt = agent.Runs.Single().Prompt;
        new { Harold = prompt.Contains("\"Harold\" → \"Гарольд\"", StringComparison.Ordinal), Other = prompt.Contains("Zzyzx", StringComparison.Ordinal), Tone = prompt.Contains("informal, ты", StringComparison.Ordinal) }
            .Should().Be(new { Harold = true, Other = false, Tone = true });
    }

    [Fact]
    public async Task The_prompt_gives_the_players_edited_term_and_form_of_address()
    {
        // Arrange
        await ExtractAsync();
        var drafted = new Glossary("ru", "informal, ты", [new GlossaryEntry("Harold", "Гарольд")]);
        _h.Files.WriteGlossary(GlossaryReview.SetFormOfAddress(GlossaryReview.SetTranslation(drafted, "Harold", "Гаральд"), FormOfAddress.Formal));
        var chunk = Chunks.Select((c, i) => (c, i)).First(x => x.c.Keys.Any(k => Source.Strings.Single(s => s.Key == k).Original.Contains("Harold", StringComparison.Ordinal))).i;
        var agent = ScriptedAgent.Translating();

        // Act
        await StepAsync(agent, chunk);

        // Assert
        var prompt = agent.Runs.Single().Prompt;
        new
        {
            Term = prompt.Contains("\"Harold\" → \"Гаральд\"", StringComparison.Ordinal),
            Address = prompt.Contains("Form of address chosen by the player: formal (вы); it overrides anything above.", StringComparison.Ordinal),
        }.Should().Be(new { Term = true, Address = true });
    }

    [Fact]
    public async Task Strings_already_translated_are_not_asked_for_again()
    {
        // Arrange
        await ExtractAsync();
        var done = Chunks[0].Keys[0];
        _h.Files.WriteStrings(new StringTable("ru", new Dictionary<string, string> { [done] = "RU уже" }));
        var agent = ScriptedAgent.Translating();

        // Act
        await StepAsync(agent);

        // Assert
        agent.Runs.Single().Keys.Should().Equal(Chunks[0].Keys.Skip(1));
    }

    [Fact]
    public async Task A_chunk_already_translated_runs_no_agent()
    {
        // Arrange
        await ExtractAsync();
        _h.Files.WriteStrings(new StringTable("ru", Chunks[0].Keys.ToDictionary(k => k, k => "RU " + Source.Strings.Single(s => s.Key == k).Original)));
        var agent = ScriptedAgent.Translating();

        // Act
        await StepAsync(agent);

        // Assert
        agent.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task Strings_left_out_are_asked_for_again_with_the_problem()
    {
        // Arrange
        await ExtractAsync();
        var agent = Agent((run, n) =>
        {
            run.Store((key, original) => n == 1 && key == Chunks[0].Keys[^1] ? null : "RU " + original);
            return [ScriptedAgent.Done];
        });

        // Act
        await StepAsync(agent);

        // Assert
        agent.Runs.Select(r => r.Strings.Count).Should().Equal(Chunks[0].Keys.Count, 1);
        agent.Runs[1].Strings.Single().Should().Be((Chunks[0].Keys[^1], Source.Strings.Single(s => s.Key == Chunks[0].Keys[^1]).Original, "no translation was stored for it"));
    }

    [Fact]
    public async Task A_retry_uses_the_retry_prompt()
    {
        // Arrange
        await ExtractAsync();
        var agent = Agent((run, n) =>
        {
            run.Store((key, original) => n == 1 && key == Chunks[0].Keys[0] ? null : "RU " + original);
            return [ScriptedAgent.Done];
        });

        // Act
        await StepAsync(agent);

        // Assert
        agent.Runs[1].Prompt.Should().StartWith("An earlier attempt at chunk 1 of");
    }

    [Fact]
    public async Task Retries_stop_after_the_attempts_allowed()
    {
        // Arrange
        await ExtractAsync();
        var stubborn = Chunks[0].Keys[0];
        var agent = Agent((run, _) =>
        {
            run.Store((key, original) => key == stubborn ? null : "RU " + original);
            return [ScriptedAgent.Done];
        });

        // Act
        await StepAsync(agent, maxAttempts: 3);

        // Assert
        agent.Runs.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_string_still_missing_after_the_retries_stays_untranslated_and_the_job_goes_on()
    {
        // Arrange
        await ExtractAsync();
        var stubborn = Chunks[0].Keys[0];
        var agent = Agent((run, _) =>
        {
            run.Store((key, original) => key == stubborn ? null : "RU " + original);
            return [ScriptedAgent.Done];
        });

        // Act
        var result = await StepAsync(agent);

        // Assert
        new { result, Logged = _h.Log.Contains($"left untranslated after 3 attempts: {stubborn}", StringComparison.Ordinal) }
            .Should().BeEquivalentTo(new { result = (GenerationStepResult)new GenerationStepResult.ChunkDone(Chunks[0].Keys.Count - 1), Logged = true });
    }

    [Fact]
    public async Task An_agent_that_stores_nothing_fails_the_stage()
    {
        // Arrange
        await ExtractAsync();
        var agent = Agent((_, _) => [ScriptedAgent.Done]);

        // Act
        var result = await StepAsync(agent);

        // Assert
        result.Should().Be(new GenerationStepResult.Failed(GenerationFailureKind.Agent, "Scripted agent stored no translations for chunk 1 after 3 attempts."));
    }

    [Fact]
    public async Task A_stored_translation_with_an_error_is_retried()
    {
        // Arrange
        await ExtractAsync();
        var withPlaceholder = Source.Strings.First(s => s.Placeholders.Count > 0);
        var chunk = Chunks.Select((c, i) => (c, i)).First(x => x.c.Keys.Contains(withPlaceholder.Key)).i;
        var agent = Agent((run, n) =>
        {
            run.Store((key, original) => n == 1 && key == withPlaceholder.Key ? "RU без кода" : "RU " + original);
            return [ScriptedAgent.Done];
        });

        // Act
        await StepAsync(agent, chunk);

        // Assert
        agent.Runs[1].Strings.Single().Problem.Should().StartWith("the stored translation \"RU без кода\" is refused:");
    }

    [Fact]
    public async Task A_translation_with_an_error_left_after_the_retries_is_removed()
    {
        // Arrange
        await ExtractAsync();
        var withPlaceholder = Source.Strings.First(s => s.Placeholders.Count > 0);
        var chunk = Chunks.Select((c, i) => (c, i)).First(x => x.c.Keys.Contains(withPlaceholder.Key)).i;
        var agent = Agent((run, _) =>
        {
            run.Store((key, original) => key == withPlaceholder.Key ? "RU без кода" : "RU " + original);
            return [ScriptedAgent.Done];
        });

        // Act
        await StepAsync(agent, chunk);

        // Assert
        Stored.Should().NotContainKey(withPlaceholder.Key);
    }

    [Fact]
    public async Task A_translation_with_only_warnings_is_kept()
    {
        // Arrange
        await ExtractAsync();
        var agent = Agent((run, _) =>
        {
            run.Store((_, original) => original + "  ");
            return [ScriptedAgent.Done];
        });

        // Act
        var result = await StepAsync(agent);

        // Assert
        new { result, Runs = agent.Runs.Count }.Should().BeEquivalentTo(new { result = (GenerationStepResult)new GenerationStepResult.ChunkDone(Chunks[0].Keys.Count), Runs = 1 });
    }

    [Fact]
    public async Task The_limit_pauses_at_the_chunk_and_keeps_what_was_stored()
    {
        // Arrange
        await ExtractAsync();
        var reset = StageHarness.T0.AddHours(2);
        var half = Chunks[0].Keys.Take(4).ToHashSet();
        var agent = Agent((run, _) =>
        {
            run.Store((key, original) => half.Contains(key) ? "RU " + original : null);
            return [new AgentLimitReached(reset, "limit"), new AgentDone(AgentOutcome.LimitReached, Message: "limit")];
        });

        // Act
        var result = await StepAsync(agent);

        // Assert
        new { result, Stored = Stored.Keys.ToHashSet() }.Should().BeEquivalentTo(new { result = (GenerationStepResult)new GenerationStepResult.LimitReached(reset, "limit"), Stored = half });
    }

    [Fact]
    public async Task The_chunk_after_a_limit_asks_only_for_the_rest()
    {
        // Arrange
        await ExtractAsync();
        var half = Chunks[0].Keys.Take(4).ToHashSet();
        await StepAsync(Agent((run, _) =>
        {
            run.Store((key, original) => half.Contains(key) ? "RU " + original : null);
            return [new AgentLimitReached(null, "limit"), new AgentDone(AgentOutcome.LimitReached, Message: "limit")];
        }));
        var agent = ScriptedAgent.Translating();

        // Act
        await StepAsync(agent);

        // Assert
        agent.Runs.Single().Keys.Should().Equal(Chunks[0].Keys.Skip(4));
    }

    [Fact]
    public async Task The_translated_count_is_the_stores_total_less_the_jobs()
    {
        // Arrange
        await ExtractAsync();
        await StepAsync(ScriptedAgent.Translating(), chunk: 0);
        var agent = ScriptedAgent.Translating();

        // Act
        var result = await StepAsync(agent, chunk: 1, translated: Chunks[0].Keys.Count);

        // Assert
        result.Should().Be(new GenerationStepResult.ChunkDone(Chunks[1].Keys.Count));
    }

    [Fact]
    public async Task The_last_strings_translated_and_the_usage_go_to_the_progress_file()
    {
        // Arrange
        await ExtractAsync();
        var agent = ScriptedAgent.Translating();

        // Act
        await StepAsync(agent);

        // Assert
        var last = Chunks[0].Keys[^1];
        var progress = PipelineProgressFile.Read(_h.Files);
        new { Recent = progress.Recent.Count, Last = progress.Recent[^1].Key, Translation = progress.Recent[^1].Translation, progress.Usage }.Should().BeEquivalentTo(new
        {
            Recent = 3,
            Last = last,
            Translation = Stored[last],
            Usage = new UsageTotals(1, 1_000, 200, 50, 10, 0.01m),
        });
    }

    [Fact]
    public async Task Translations_with_warnings_go_to_the_progress_file_with_their_rule()
    {
        // Arrange
        await ExtractAsync();
        var last = Chunks[0].Keys[^1];
        var agent = Agent((run, _) =>
        {
            run.Store((key, original) => key == last ? "RU " + original + " " : "RU " + original);
            return [ScriptedAgent.Usage, ScriptedAgent.Done];
        });

        // Act
        await StepAsync(agent);

        // Assert
        var progress = PipelineProgressFile.Read(_h.Files);
        new { Warning = progress.Warnings.Single().Key, progress.Warnings.Single().Rule, Recent = progress.Recent[^1].Warning }
            .Should().Be(new { Warning = last, Rule = "whitespace", Recent = (string?)"whitespace" });
    }

    [Fact]
    public async Task Warnings_of_other_chunks_stay_in_the_progress_file()
    {
        // Arrange
        await ExtractAsync();
        var first = Chunks[0].Keys[0];
        await StepAsync(Agent((run, _) =>
        {
            run.Store((key, original) => key == first ? "RU " + original + " " : "RU " + original);
            return [ScriptedAgent.Done];
        }));

        // Act
        await StepAsync(ScriptedAgent.Translating(), chunk: 1, translated: Chunks[0].Keys.Count);

        // Assert
        PipelineProgressFile.Read(_h.Files).Warnings.Select(w => w.Key).Should().Equal(first);
    }

    [Fact]
    public async Task The_agents_reported_limit_share_goes_to_the_progress_file()
    {
        // Arrange
        await ExtractAsync();
        var agent = Agent((run, _) =>
        {
            run.Store();
            return [new AgentLimitStatus(0.45, "five_hour", null), ScriptedAgent.Done];
        });

        // Act
        await StepAsync(agent);

        // Assert
        PipelineProgressFile.Read(_h.Files).Limit.Should().Be(new AgentLimitInfo(0.45, "five_hour", null));
    }

    [Fact]
    public async Task A_missing_chunk_plan_fails_the_stage()
    {
        // Arrange
        await ExtractAsync();
        File.Delete(_h.Files.ChunksPath);

        // Act
        var result = await StepAsync(ScriptedAgent.Translating());

        // Assert
        result.Should().Be(new GenerationStepResult.Failed(GenerationFailureKind.Toolkit, "chunks.json is missing: the Extract stage didn't store it."));
    }

    [Fact]
    public async Task A_chunk_plan_not_matching_the_job_fails_the_stage()
    {
        // Arrange
        await ExtractAsync();
        ChunkPlanFile.Write(_h.Files, Chunks.Take(1).ToList());

        // Act
        var result = await StepAsync(ScriptedAgent.Translating());

        // Assert
        result.Should().BeOfType<GenerationStepResult.Failed>().Which.Kind.Should().Be(GenerationFailureKind.Toolkit);
    }

    [Fact]
    public async Task A_step_with_workers_translates_that_many_chunks()
    {
        // Arrange
        await ExtractAsync();
        var agent = ScriptedAgent.Translating();

        // Act
        var result = await StepAsync(agent, workers: 3);

        // Assert
        var keys = Chunks.Take(3).SelectMany(c => c.Keys).ToList();
        new { result, Stored = Stored.Keys.ToHashSet() }.Should().BeEquivalentTo(new
        {
            result = (GenerationStepResult)new GenerationStepResult.ChunkDone(keys.Count, 3),
            Stored = keys.ToHashSet(),
        });
    }

    [Fact]
    public async Task The_chunks_of_a_step_run_side_by_side()
    {
        // Arrange
        await ExtractAsync();
        var started = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new ScriptedAgent(async (run, _, cancellationToken) =>
        {
            if (Interlocked.Increment(ref started) == 3)
            {
                allStarted.TrySetResult();
            }

            // Run one after another, the first would wait here in vain.
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            run.Store();
            return [ScriptedAgent.Usage, ScriptedAgent.Done];
        });

        // Act
        var result = await StepAsync(agent, workers: 3);

        // Assert
        result.Should().BeOfType<GenerationStepResult.ChunkDone>().Which.Chunks.Should().Be(3);
    }

    [Fact]
    public async Task The_last_step_takes_only_the_chunks_left()
    {
        // Arrange
        await ExtractAsync();
        var agent = ScriptedAgent.Translating();

        // Act
        var result = await StepAsync(agent, chunk: Chunks.Count - 1, workers: 3);

        // Assert
        new { result, Runs = agent.Runs.Count }.Should().BeEquivalentTo(new
        {
            result = (GenerationStepResult)new GenerationStepResult.ChunkDone(Chunks[^1].Keys.Count, 1),
            Runs = 1,
        });
    }

    [Fact]
    public async Task A_chunk_stopped_by_the_limit_ends_the_step_with_the_others_stored()
    {
        // Arrange
        await ExtractAsync();
        var reset = StageHarness.T0.AddHours(2);
        var second = Chunks[1].Keys.ToHashSet();
        var agent = Agent((run, _) =>
        {
            if (run.Keys.Any(second.Contains))
            {
                return [new AgentLimitReached(reset, "limit"), new AgentDone(AgentOutcome.LimitReached, Message: "limit")];
            }

            run.Store();
            return [ScriptedAgent.Usage, ScriptedAgent.Done];
        });

        // Act
        var result = await StepAsync(agent, workers: 3);

        // Assert
        new { result, Stored = Stored.Keys.ToHashSet() }.Should().BeEquivalentTo(new
        {
            result = (GenerationStepResult)new GenerationStepResult.LimitReached(reset, "limit"),
            Stored = Chunks[0].Keys.Concat(Chunks[2].Keys).ToHashSet(),
        });
    }

    [Fact]
    public async Task Agent_log_lines_name_their_chunk_when_chunks_run_side_by_side()
    {
        // Arrange
        await ExtractAsync();
        var agent = ScriptedAgent.Translating();

        // Act
        await StepAsync(agent, workers: 2);

        // Assert
        _h.Log.Should().Contain($"Chunk 2 of {Chunks.Count}: Agent run ended: Succeeded");
    }

    [Fact]
    public void A_string_line_carries_the_limit_and_placeholders()
    {
        // Arrange
        var s = new ExtractedString("k", "Hi \\N[1]", "Greeting", 12, ["\\N[1]"]) { MaxLengthUnit = Toolkit.Validation.LengthUnit.DisplayWidth };

        // Act
        var line = TranslateStage.StringLines([s], null);

        // Assert
        line.Should().Be("""{"key":"k","original":"Hi \\N[1]","context":"Greeting","maxLength":12,"maxLengthUnit":"display width: wide characters count 2","placeholders":["\\N[1]"]}""");
    }

    [Fact]
    public void Glossary_lines_name_grammar_notes_and_terms_kept_as_is()
    {
        // Arrange
        GlossaryEntry[] entries = [new("Potion", "Зелье", "heals 50 HP", "noun", "neuter"), new("HP", "HP", KeepAsIs: true)];

        // Act
        var lines = TranslateStage.GlossaryLines(entries);

        // Assert
        lines.Should().Be("- \"Potion\" → \"Зелье\" (noun, neuter): heals 50 HP\n- \"HP\": keep as is");
    }
}
