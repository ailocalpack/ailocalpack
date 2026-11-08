using AiLocalPack.Core.Activity;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Generation;

/// <summary>Auto-resume at the limit's reset time, the Activity running task and the library state (task 47).</summary>
public sealed class GenerationResumeTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new(T0);

    public void Dispose() => _dir.Dispose();

    private static GenerationJobEntry Entry(string id, GenerationJobStatus status, GenerationPauseReason? reason = null, DateTimeOffset? resumeAt = null) => new(
        "/work/" + id,
        GenerationJobMachine.Create(id, GameId.Steam(620), "Sample Quest", "ru", "fake", GenerationOptions.Default, T0) with
        {
            Status = status,
            PauseReason = reason,
            ResumeAt = resumeAt,
        });

    [Fact]
    public void Only_jobs_paused_by_the_limit_whose_reset_came_are_due()
    {
        // Arrange
        GenerationJobEntry[] jobs =
        [
            Entry("later", GenerationJobStatus.Paused, GenerationPauseReason.Limit, T0.AddHours(2)),
            Entry("due", GenerationJobStatus.Paused, GenerationPauseReason.Limit, T0.AddMinutes(-1)),
            Entry("player", GenerationJobStatus.Paused, GenerationPauseReason.Player),
            Entry("interrupted", GenerationJobStatus.Interrupted),
            Entry("failed", GenerationJobStatus.Failed),
        ];

        // Act
        var due = GenerationResumeSchedule.Due(jobs, T0);

        // Assert
        due.Select(e => e.Job.Id).Should().Equal("due");
    }

    [Fact]
    public void Next_is_the_earliest_reset_time()
    {
        // Arrange
        GenerationJobEntry[] jobs =
        [
            Entry("a", GenerationJobStatus.Paused, GenerationPauseReason.Limit, T0.AddHours(5)),
            Entry("b", GenerationJobStatus.Paused, GenerationPauseReason.Limit, T0.AddHours(2)),
            Entry("c", GenerationJobStatus.Running),
        ];

        // Act
        var next = GenerationResumeSchedule.Next(jobs);

        // Assert
        next.Should().Be(T0.AddHours(2));
    }

    [Fact]
    public async Task The_scheduler_resumes_a_job_once_the_clock_reaches_its_reset()
    {
        // Arrange
        var resumed = new List<string>();
        GenerationJobEntry[] jobs = [Entry("job", GenerationJobStatus.Paused, GenerationPauseReason.Limit, T0.AddHours(3))];
        var scheduler = new GenerationResumeScheduler(_clock, () => jobs, e =>
        {
            resumed.Add(e.Job.Id);
            return Task.CompletedTask;
        });
        await scheduler.TickAsync();
        _clock.Advance(TimeSpan.FromHours(3));

        // Act
        await scheduler.TickAsync();

        // Assert
        resumed.Should().Equal("job");
    }

    [Fact]
    public async Task A_job_that_cant_be_resumed_now_is_tried_again_on_the_next_tick()
    {
        // Arrange
        var tries = 0;
        GenerationJobEntry[] jobs = [Entry("job", GenerationJobStatus.Paused, GenerationPauseReason.Limit, T0)];
        var scheduler = new GenerationResumeScheduler(_clock, () => jobs, _ => ++tries == 1
            ? throw new GenerationJobException(GenerationJobProblem.Busy, "busy")
            : Task.CompletedTask);
        await scheduler.TickAsync();

        // Act
        var resumed = await scheduler.TickAsync();

        // Assert
        resumed.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_running_job_shows_as_an_Activity_task_that_can_be_paused_and_ends_with_the_run()
    {
        // Arrange
        var activity = new ActivityStore(AppPaths.UnderHome(_dir.Combine("home")), _clock);
        var titles = new List<string>();
        activity.Changed += (_, _) => titles.AddRange(activity.Tasks.Select(t => t.Title));
        var jobs = new GenerationJobs(new GenerationJobStore(AppPaths.UnderHome(_dir.Combine("home")), _clock), new GenerationRunner(_clock), _clock);
        var game = new Game(GameId.Steam(620), "Sample Quest", GameSource.Steam, "/games/sample", null, null, null, "none", null, true);
        var policy = new AiLocalPack.Core.AntiCheat.AntiCheatPolicy([], new Dictionary<GameId, string>(), new HashSet<GameId>());
        var run = jobs.Start(game, policy, null, "ru", "fake", GenerationOptions.Default, _ => new GenerationStages(
            Enum.GetValues<GenerationStage>().Where(s => s != GenerationStage.Done).Select(s => (IGenerationStage)new Done(s))), cancellationToken: TestContext.Current.CancellationToken);
        var publisher = new GenerationActivity(activity, (job, progress) => ($"{job.Stage} {progress.Percent}%", null));

        // Act
        publisher.Follow(run);
        await run.Completion;

        // Assert
        new { HadTask = titles.Count > 0, activity.Tasks.Count }.Should().Be(new { HadTask = true, Count = 0 });
    }

    [Fact]
    public void An_open_job_maps_to_the_library_state()
    {
        // Arrange
        var job = Entry("job", GenerationJobStatus.Paused, GenerationPauseReason.Limit, T0.AddHours(1)).Job with
        {
            Stage = GenerationStage.Translate,
            Counters = new GenerationCounters(100, 50, 2, 1),
        };

        // Act
        var state = GenerationJobStates.Of(job, "limit");

        // Assert
        state.Should().Be(new AiLocalPack.Core.Library.GenerationJobState(55, true, null, "limit"));
    }

    [Fact]
    public void A_completed_job_has_no_library_state()
    {
        // Arrange
        var job = Entry("job", GenerationJobStatus.Completed).Job with { Stage = GenerationStage.Done };

        // Act
        var state = GenerationJobStates.Of(job);

        // Assert
        state.Should().BeNull();
    }

    private sealed class Done(GenerationStage stage) : IGenerationStage
    {
        public GenerationStage Stage => stage;

        public Task<GenerationStepResult> RunStepAsync(GenerationStepContext context, CancellationToken cancellationToken) =>
            Task.FromResult<GenerationStepResult>(new GenerationStepResult.StageDone());
    }
}
