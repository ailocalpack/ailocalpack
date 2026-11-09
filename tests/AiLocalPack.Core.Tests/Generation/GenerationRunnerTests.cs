using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Generation;

/// <summary>Running, pausing, resuming and cancelling jobs through <see cref="GenerationJobs"/> (task 47).</summary>
public sealed class GenerationRunnerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new(T0);
    private readonly GenerationJobs _jobs;
    private readonly List<int> _chunksRun = [];

    public GenerationRunnerTests()
    {
        var store = new GenerationJobStore(AppPaths.UnderHome(_dir.Combine("home")), _clock);
        _jobs = new GenerationJobs(store, new GenerationRunner(_clock), _clock);
    }

    public void Dispose() => _dir.Dispose();

    private static Game Game(string antiCheat = "none") =>
        new(GameId.Steam(620), "Sample Quest", GameSource.Steam, "/games/sample", null, null, "rpgmaker:mz", antiCheat, null, true);

    private static AntiCheatPolicy Policy => new([new AntiCheatRule("eac", "Easy Anti-Cheat", ["EasyAntiCheat/"])], new Dictionary<GameId, string>(), new HashSet<GameId>());

    /// <summary>Setup, Extract (3 chunks of 10 strings), Glossary, Translate (<paramref name="translate"/>, else records the chunk), Build.</summary>
    private GenerationStages Stages(
        Func<GenerationStepContext, CancellationToken, Task<GenerationStepResult>>? translate = null,
        Func<GenerationStepResult>? glossary = null,
        GenerationStage? pauseBefore = null) => new(
    [
        new Step(GenerationStage.Setup, (_, _) => Done()),
        new Step(GenerationStage.Extract, (_, _) => Task.FromResult<GenerationStepResult>(new GenerationStepResult.StageDone(new GenerationCounters(30, 0, 3, 0)))),
        new Step(GenerationStage.Glossary, (_, _) => glossary is null ? Done() : Task.FromResult(glossary())),
        new Step(GenerationStage.Translate, translate ?? ((context, _) =>
        {
            _chunksRun.Add(context.Job.Counters.ChunksDone);
            return Task.FromResult<GenerationStepResult>(new GenerationStepResult.ChunkDone(10));
        })),
        new Step(GenerationStage.Build, (_, _) => Done()),
    ],
    pauseBefore);

    private static Task<GenerationStepResult> Done() => Task.FromResult<GenerationStepResult>(new GenerationStepResult.StageDone());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GenerationRun Start(GenerationStages stages, bool review = false, CancellationTokenSource? stop = null) =>
        _jobs.Start(Game(), Policy, null, "ru", "fake", new GenerationOptions(review), _ => stages, cancellationToken: stop?.Token ?? Ct);

    private GenerationRun Resume(GenerationRun run, GenerationStages stages) => _jobs.Resume(run.Folder, Game(), Policy, null, _ => stages, Ct);

    [Fact]
    public async Task A_job_runs_every_stage_and_completes()
    {
        // Arrange
        var run = Start(Stages());

        // Act
        var job = await run.Completion;

        // Assert
        new { job.Status, job.Stage, job.Counters, Chunks = _chunksRun }.Should().BeEquivalentTo(new
        {
            Status = GenerationJobStatus.Completed,
            Stage = GenerationStage.Done,
            Counters = new GenerationCounters(30, 30, 3, 3),
            Chunks = new[] { 0, 1, 2 },
        });
    }

    [Fact]
    public async Task A_job_pauses_before_the_stage_asked_without_running_it()
    {
        // Arrange
        var run = Start(Stages(pauseBefore: GenerationStage.Translate));

        // Act
        var job = await run.Completion;

        // Assert
        new { job.Status, job.PauseReason, job.Stage, Chunks = _chunksRun }.Should().BeEquivalentTo(new
        {
            Status = GenerationJobStatus.Paused,
            PauseReason = (GenerationPauseReason?)GenerationPauseReason.Player,
            Stage = GenerationStage.Translate,
            Chunks = Array.Empty<int>(),
        });
    }

    [Fact]
    public void A_job_cannot_pause_before_setup()
    {
        // Arrange
        var stages = Array.Empty<IGenerationStage>();

        // Act
        var act = () => new GenerationStages(stages, GenerationStage.Setup);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Every_step_is_saved_to_job_json()
    {
        // Arrange
        var saved = new List<GenerationStage>();
        var run = Start(Stages());
        run.Changed += (_, _) => saved.Add(GenerationJobStore.Read(run.Folder).Stage);

        // Act
        await run.Completion;

        // Assert
        saved.Should().Contain([GenerationStage.Extract, GenerationStage.Translate, GenerationStage.Done]);
    }

    [Fact]
    public void A_blocked_game_is_refused_before_anything_is_written()
    {
        // Arrange
        var game = Game("eac");

        // Act
        var act = () => _jobs.Start(game, Policy, null, "ru", "fake", GenerationOptions.Default, _ => Stages(), cancellationToken: Ct);

        // Assert
        act.Should().Throw<GameBlockedException>().Which.Operation.Should().Be(BlockedOperation.Generate);
        _jobs.Store.List().Should().BeEmpty();
    }

    [Fact]
    public async Task The_limit_pauses_the_job_until_the_reset_time()
    {
        // Arrange
        var run = Start(Stages((_, _) => Task.FromResult<GenerationStepResult>(new GenerationStepResult.LimitReached(T0.AddHours(4)))));

        // Act
        var job = await run.Completion;

        // Assert
        new { job.Status, job.PauseReason, job.ResumeAt, job.Stage }.Should().Be(new
        {
            Status = GenerationJobStatus.Paused,
            PauseReason = (GenerationPauseReason?)GenerationPauseReason.Limit,
            ResumeAt = (DateTimeOffset?)T0.AddHours(4),
            Stage = GenerationStage.Translate,
        });
    }

    [Fact]
    public async Task A_limit_without_a_reset_time_waits_the_default()
    {
        // Arrange
        var run = Start(Stages((_, _) => Task.FromResult<GenerationStepResult>(new GenerationStepResult.LimitReached(null))));

        // Act
        var job = await run.Completion;

        // Assert
        job.ResumeAt.Should().Be(T0 + GenerationRunner.DefaultLimitWait);
    }

    [Fact]
    public async Task Resuming_after_the_limit_goes_on_from_the_same_chunk()
    {
        // Arrange
        var limited = true;
        var stages = Stages((context, _) =>
        {
            if (context.Job.Counters.ChunksDone == 1 && limited)
            {
                limited = false;
                return Task.FromResult<GenerationStepResult>(new GenerationStepResult.LimitReached(T0.AddHours(1)));
            }

            _chunksRun.Add(context.Job.Counters.ChunksDone);
            return Task.FromResult<GenerationStepResult>(new GenerationStepResult.ChunkDone(10));
        });
        var first = Start(stages);
        await first.Completion;
        _clock.Advance(TimeSpan.FromHours(1));

        // Act
        var job = await Resume(first, stages).Completion;

        // Assert
        new { job.Status, Chunks = _chunksRun }.Should().BeEquivalentTo(new { Status = GenerationJobStatus.Completed, Chunks = new[] { 0, 1, 2 } });
    }

    [Fact]
    public async Task An_interrupted_job_resumes_from_the_saved_chunk()
    {
        // Arrange
        using var stop = new CancellationTokenSource();
        var stages = Stages((context, ct) =>
        {
            if (context.Job.Counters.ChunksDone == 2)
            {
                stop.Cancel();
                ct.ThrowIfCancellationRequested();
            }

            _chunksRun.Add(context.Job.Counters.ChunksDone);
            return Task.FromResult<GenerationStepResult>(new GenerationStepResult.ChunkDone(10));
        });
        var first = Start(stages, stop: stop);
        var interrupted = await first.Completion;

        // Act
        var job = await Resume(first, Stages()).Completion;

        // Assert
        new { First = interrupted.Status, job.Status, Chunks = _chunksRun }.Should().BeEquivalentTo(new
        {
            First = GenerationJobStatus.Interrupted,
            Status = GenerationJobStatus.Completed,
            Chunks = new[] { 0, 1, 2 },
        });
    }

    [Fact]
    public async Task A_job_left_running_by_a_dead_process_resumes_from_its_chunk()
    {
        // Arrange
        var first = Start(Stages(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new GenerationStepResult.ChunkDone(10);
        }));
        while (first.Job.Stage != GenerationStage.Translate)
        {
            await Task.Delay(10, Ct);
        }

        first.Cancel();
        await first.Completion;
        GenerationJobStore.Write(first.Folder, first.Job with
        {
            Status = GenerationJobStatus.Running,
            Counters = new GenerationCounters(30, 10, 3, 1),
            RunningSince = T0,
        });

        // Act
        var job = await Resume(first, Stages()).Completion;

        // Assert
        new { job.Status, Chunks = _chunksRun }.Should().BeEquivalentTo(new { Status = GenerationJobStatus.Completed, Chunks = new[] { 1, 2 } });
    }

    [Fact]
    public async Task Pause_stops_the_running_step_and_keeps_the_chunk()
    {
        // Arrange
        var started = new TaskCompletionSource();
        var run = Start(Stages(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new GenerationStepResult.ChunkDone(10);
        }));
        await started.Task;

        // Act
        run.Pause();

        // Assert
        var job = await run.Completion;
        new { job.Status, job.PauseReason, job.Counters.ChunksDone }.Should().Be(new
        {
            Status = GenerationJobStatus.Paused,
            PauseReason = (GenerationPauseReason?)GenerationPauseReason.Player,
            ChunksDone = 0,
        });
    }

    [Fact]
    public async Task Cancel_ends_the_job()
    {
        // Arrange
        var started = new TaskCompletionSource();
        var run = Start(Stages(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new GenerationStepResult.ChunkDone(10);
        }));
        await started.Task;

        // Act
        run.Cancel();

        // Assert
        (await run.Completion).Status.Should().Be(GenerationJobStatus.Cancelled);
    }

    [Fact]
    public async Task A_stage_failure_fails_the_job_with_its_reason()
    {
        // Arrange
        var run = Start(Stages(glossary: () => new GenerationStepResult.Failed(GenerationFailureKind.SignedOut, "Not logged in")));

        // Act
        var job = await run.Completion;

        // Assert
        new { job.Status, job.Failure }.Should().Be(new
        {
            Status = GenerationJobStatus.Failed,
            Failure = (GenerationFailure?)new GenerationFailure(GenerationStage.Glossary, GenerationFailureKind.SignedOut, "Not logged in"),
        });
    }

    [Fact]
    public async Task A_handler_that_throws_fails_the_stage_as_unexpected()
    {
        // Arrange
        var run = Start(Stages((_, _) => throw new InvalidOperationException("bug")));

        // Act
        var job = await run.Completion;

        // Assert
        job.Failure.Should().Be(new GenerationFailure(GenerationStage.Translate, GenerationFailureKind.Unexpected, "bug"));
    }

    [Fact]
    public async Task A_missing_handler_fails_the_stage()
    {
        // Arrange
        var run = Start(new GenerationStages([new Step(GenerationStage.Setup, (_, _) => Done())]));

        // Act
        var job = await run.Completion;

        // Assert
        new { job.Status, job.Failure?.Stage }.Should().Be(new { Status = GenerationJobStatus.Failed, Stage = (GenerationStage?)GenerationStage.Extract });
    }

    [Fact]
    public async Task A_failed_stage_is_retried_on_resume()
    {
        // Arrange
        var first = Start(Stages(glossary: () => new GenerationStepResult.Failed(GenerationFailureKind.Agent, "x")));
        await first.Completion;

        // Act
        var job = await Resume(first, Stages()).Completion;

        // Assert
        job.Status.Should().Be(GenerationJobStatus.Completed);
    }

    [Fact]
    public async Task With_review_the_job_waits_after_the_glossary_and_resume_approves_it()
    {
        // Arrange
        var first = Start(Stages(), review: true);
        var waiting = await first.Completion;

        // Act
        var job = await Resume(first, Stages()).Completion;

        // Assert
        new { Waiting = waiting.Status, job.Status }.Should().Be(new { Waiting = GenerationJobStatus.WaitingForReview, Status = GenerationJobStatus.Completed });
    }

    private static readonly Glossary Drafted = new("ru", "на ты", [
        new GlossaryEntry("Harold", "Гарольд"),
        new GlossaryEntry("Potion", "Эликсир", Edited: true),
    ]);

    [Fact]
    public async Task Regenerating_the_glossary_keeps_only_the_players_edits_for_the_agent_to_complete()
    {
        // Arrange
        var first = Start(Stages(), review: true);
        await first.Completion;
        new GenerationFiles(first.Folder).WriteGlossary(Drafted);

        // Act
        await _jobs.RegenerateGlossary(first.Folder, Game(), Policy, null, _ => Stages(), Ct).Completion;

        // Assert
        new GenerationFiles(first.Folder).ReadGlossary().Should().BeEquivalentTo(new Glossary("ru", null, [new GlossaryEntry("Potion", "Эликсир", Edited: true)]));
    }

    [Fact]
    public async Task Regenerating_the_glossary_runs_the_glossary_stage_and_waits_for_the_review_again()
    {
        // Arrange
        var runs = 0;
        var first = Start(Stages(glossary: () => { runs++; return new GenerationStepResult.StageDone(); }), review: true);
        await first.Completion;

        // Act
        var job = await _jobs.RegenerateGlossary(first.Folder, Game(), Policy, null, _ => Stages(glossary: () => { runs++; return new GenerationStepResult.StageDone(); }), Ct).Completion;

        // Assert
        new { job.Status, job.Stage, Runs = runs }.Should().Be(new { Status = GenerationJobStatus.WaitingForReview, Stage = GenerationStage.Glossary, Runs = 2 });
    }

    [Fact]
    public async Task Regenerating_is_refused_for_a_job_not_waiting_for_review()
    {
        // Arrange
        var first = Start(Stages(), review: false);
        await first.Completion;

        // Act
        var act = () => _jobs.RegenerateGlossary(first.Folder, Game(), Policy, null, _ => Stages(), Ct);

        // Assert
        act.Should().Throw<GenerationJobException>().Which.Problem.Should().Be(GenerationJobProblem.WrongState);
    }

    [Fact]
    public async Task An_edit_of_a_waiting_glossary_is_saved_at_once()
    {
        // Arrange
        var first = Start(Stages(), review: true);
        await first.Completion;
        new GenerationFiles(first.Folder).WriteGlossary(Drafted);

        // Act
        _jobs.EditGlossary(first.Folder, g => GlossaryReview.SetTranslation(g, "harold", "Гаральд"));

        // Assert
        new GenerationFiles(first.Folder).ReadGlossary()!.Entries[0].Should().Be(new GlossaryEntry("Harold", "Гаральд", Edited: true));
    }

    [Fact]
    public async Task An_edit_is_refused_once_the_job_left_the_review()
    {
        // Arrange
        var first = Start(Stages(), review: true);
        await first.Completion;
        new GenerationFiles(first.Folder).WriteGlossary(Drafted);
        await Resume(first, Stages()).Completion;

        // Act
        var act = () => _jobs.EditGlossary(first.Folder, g => GlossaryReview.SetTranslation(g, "Harold", "Гаральд"));

        // Assert
        act.Should().Throw<GenerationJobException>().Which.Problem.Should().Be(GenerationJobProblem.WrongState);
    }

    [Fact]
    public async Task A_completed_job_cant_be_resumed()
    {
        // Arrange
        var first = Start(Stages());
        await first.Completion;

        // Act
        var act = () => Resume(first, Stages());

        // Assert
        act.Should().Throw<GenerationJobException>().Which.Problem.Should().Be(GenerationJobProblem.WrongState);
    }

    [Fact]
    public async Task A_running_job_cant_be_resumed_by_a_second_runner()
    {
        // Arrange
        var started = new TaskCompletionSource();
        var run = Start(Stages(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new GenerationStepResult.ChunkDone(10);
        }));
        await started.Task;

        // Act
        var act = () => Resume(run, Stages());

        // Assert
        act.Should().Throw<GenerationJobException>().Which.Problem.Should().Be(GenerationJobProblem.Busy);
        run.Cancel();
        await run.Completion;
    }

    [Fact]
    public async Task A_paused_job_can_be_cancelled_without_a_runner()
    {
        // Arrange
        var first = Start(Stages((_, _) => Task.FromResult<GenerationStepResult>(new GenerationStepResult.LimitReached(T0.AddHours(1)))));
        await first.Completion;

        // Act
        var job = _jobs.Cancel(first.Folder);

        // Assert
        new { job.Status, Saved = GenerationJobStore.Read(first.Folder).Status }.Should().Be(new
        {
            Status = GenerationJobStatus.Cancelled,
            Saved = GenerationJobStatus.Cancelled,
        });
    }

    [Fact]
    public async Task Chunk_time_is_measured_for_the_ETA()
    {
        // Arrange
        var run = Start(Stages((_, _) =>
        {
            _clock.Advance(TimeSpan.FromMinutes(2));
            return Task.FromResult<GenerationStepResult>(new GenerationStepResult.ChunkDone(10));
        }));

        // Act
        var job = await run.Completion;

        // Assert
        job.ChunkTime.Should().Be(TimeSpan.FromMinutes(6));
    }

    [Fact]
    public async Task The_job_log_records_the_steps()
    {
        // Arrange
        var run = Start(Stages());

        // Act
        await run.Completion;

        // Assert
        File.ReadAllText(GenerationJobStore.LogFile(run.Folder)).Should().Contain("Chunk 3 of 3 done").And.Contain("Completed.");
    }

    private sealed class Step(GenerationStage stage, Func<GenerationStepContext, CancellationToken, Task<GenerationStepResult>> step) : IGenerationStage
    {
        GenerationStage IGenerationStage.Stage => stage;

        public Task<GenerationStepResult> RunStepAsync(GenerationStepContext context, CancellationToken cancellationToken) => step(context, cancellationToken);
    }
}
