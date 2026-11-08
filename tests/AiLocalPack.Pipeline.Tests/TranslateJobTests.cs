using AiLocalPack.Agents;
using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Generation;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>A job through Translate as the runner saves it: paused mid-chunk and resumed, no chunk twice, none skipped (task 50).</summary>
public sealed class TranslateJobTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly GenerationJobs _jobs;
    private readonly Game _game;
    private readonly WorkingCopies _copies;
    private readonly TaskCompletionSource<GenerationRun> _run = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TranslateJobTests()
    {
        _dir.RpgMakerGame("games/sample");
        _game = StageHarness.Game with { InstallPath = _dir.Combine("games/sample") };
        var paths = AppPaths.UnderHome(_dir.Combine("home"));
        _copies = new WorkingCopies(paths, FileCloner.Create(), new SystemDiskSpace());
        _jobs = new GenerationJobs(new GenerationJobStore(paths), new GenerationRunner());
    }

    public void Dispose() => _dir.Dispose();

    private Func<GenerationJobEntry, GenerationStages> Stages(IAgentAdapter agent) => _ =>
        GenerationPipeline.Create(
            new PipelineServices(
                _game,
                new PipelineAgent(agent, StageHarness.Server),
                RecipeTools.Builtin,
                new GenerationEstimator(RecipeTools.Builtin, _copies),
                _copies,
                new ChunkBudget(10, 100_000)),
            pauseBefore: GenerationStage.Build);

    /// <summary>Saves a glossary when asked for one; translates chunks; at the second chunk pauses the job, after storing it when <paramref name="storeFirst"/>.</summary>
    private ScriptedAgent PausingAtChunk2(bool storeFirst) => new(async (run, _, ct) =>
    {
        if (run.Strings.Count == 0)
        {
            run.Files.WriteGlossary(new Glossary("ru", "informal", [new GlossaryEntry("Harold", "Гарольд")]));
            return [ScriptedAgent.Done];
        }

        if (Interlocked.Increment(ref _translateRuns) == 2)
        {
            if (storeFirst)
            {
                run.Store();
            }

            (await _run.Task).Pause();
            await Task.Delay(Timeout.Infinite, ct);
        }

        run.Store();
        return [ScriptedAgent.Done];
    });

    private int _translateRuns;

    /// <summary>The job paused, then resumed; <c>Stored</c> = the keys of every agent run that stored its strings.</summary>
    private async Task<(GenerationJob Paused, GenerationJob Resumed, List<string> Stored)> PauseAndResumeAsync(bool storeFirst)
    {
        var first = PausingAtChunk2(storeFirst);
        var run = _jobs.Start(_game, AntiCheatPolicy.Bundled, null, "ru", "scripted", GenerationOptions.Default, Stages(first), cancellationToken: TestContext.Current.CancellationToken);
        _run.SetResult(run);
        var paused = await run.Completion;
        var second = ScriptedAgent.Translating();
        var resumed = await _jobs.Resume(run.Folder, _game, AntiCheatPolicy.Bundled, null, Stages(second), TestContext.Current.CancellationToken).Completion;
        var stored = first.Runs.Concat(second.Runs).Where(r => r.Strings.Count > 0 && r.Stored).SelectMany(r => r.Keys).ToList();
        return (paused, resumed, stored);
    }

    [Fact]
    public async Task A_pause_mid_chunk_leaves_the_job_at_that_chunk()
    {
        // Arrange
        var storeFirst = false;

        // Act
        var (paused, _, _) = await PauseAndResumeAsync(storeFirst);

        // Assert
        new { paused.Status, paused.Stage, paused.Counters.ChunksDone }.Should().Be(new { Status = GenerationJobStatus.Paused, Stage = GenerationStage.Translate, ChunksDone = 1 });
    }

    [Fact]
    public async Task After_resuming_every_string_is_translated_once()
    {
        // Arrange
        var storeFirst = false;

        // Act
        var (_, resumed, asked) = await PauseAndResumeAsync(storeFirst);

        // Assert
        new { asked.Count, Unique = asked.Distinct().Count(), resumed.Counters.Translated, Done = resumed.Counters.ChunksDone == resumed.Counters.Chunks, resumed.Stage }
            .Should().Be(new { Count = 64, Unique = 64, Translated = 64, Done = true, Stage = GenerationStage.Build });
    }

    [Fact]
    public async Task A_chunk_stored_before_the_pause_isnt_asked_for_again()
    {
        // Arrange
        var storeFirst = true;

        // Act
        var (_, resumed, asked) = await PauseAndResumeAsync(storeFirst);

        // Assert
        new { asked.Count, Unique = asked.Distinct().Count(), resumed.Counters.Translated }.Should().Be(new { Count = 64, Unique = 64, Translated = 64 });
    }
}
