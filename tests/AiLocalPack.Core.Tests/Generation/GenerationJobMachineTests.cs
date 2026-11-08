using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;

namespace AiLocalPack.Core.Tests.Generation;

/// <summary>The job's state machine (task 47): every allowed and forbidden transition, and what each one changes.</summary>
public sealed class GenerationJobMachineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private static GenerationJob Job(
        GenerationJobStatus status = GenerationJobStatus.Running,
        GenerationStage stage = GenerationStage.Setup,
        GenerationCounters? counters = null,
        bool review = false) =>
        GenerationJobMachine.Create("job1", GameId.Steam(620), "Sample Quest", "ru", "fake", new GenerationOptions(review), T0) with
        {
            Status = status,
            Stage = stage,
            Counters = counters ?? new GenerationCounters(100, 0, 2, 0),
            RunningSince = status == GenerationJobStatus.Running ? T0 : null,
        };

    private static readonly Dictionary<string, GenerationTrigger> Triggers = new()
    {
        ["stage"] = new GenerationTrigger.StageCompleted(),
        ["chunk"] = new GenerationTrigger.ChunkCompleted(50, TimeSpan.FromMinutes(1)),
        ["limit"] = new GenerationTrigger.LimitReached(T0.AddHours(2)),
        ["pause"] = new GenerationTrigger.PausedByPlayer(),
        ["resume"] = new GenerationTrigger.Resumed(),
        ["interrupt"] = new GenerationTrigger.Interrupted(),
        ["fail"] = new GenerationTrigger.Failed(GenerationFailureKind.Agent, "boom"),
        ["retry"] = new GenerationTrigger.Retried(),
        ["approve"] = new GenerationTrigger.GlossaryApproved(),
        ["cancel"] = new GenerationTrigger.Cancelled(),
    };

    private static readonly Dictionary<GenerationJobStatus, string[]> Allowed = new()
    {
        [GenerationJobStatus.Running] = ["stage", "chunk", "limit", "pause", "interrupt", "fail", "cancel"],
        [GenerationJobStatus.Paused] = ["resume", "cancel"],
        [GenerationJobStatus.Interrupted] = ["resume", "cancel"],
        [GenerationJobStatus.Failed] = ["retry", "cancel"],
        [GenerationJobStatus.WaitingForReview] = ["approve", "cancel"],
        [GenerationJobStatus.Cancelled] = [],
        [GenerationJobStatus.Completed] = [],
    };

    public static TheoryData<GenerationJobStatus, string, bool> Transitions()
    {
        var data = new TheoryData<GenerationJobStatus, string, bool>();
        foreach (var status in Enum.GetValues<GenerationJobStatus>())
        {
            foreach (var trigger in Triggers.Keys)
            {
                data.Add(status, trigger, Allowed[status].Contains(trigger));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Transitions))]
    public void Each_state_allows_exactly_its_triggers(GenerationJobStatus status, string trigger, bool allowed)
    {
        // Arrange
        var job = Job(status, GenerationStage.Translate);

        // Act
        var can = GenerationJobMachine.CanApply(job, Triggers[trigger]);

        // Assert
        can.Should().Be(allowed);
    }

    [Theory]
    [MemberData(nameof(Transitions))]
    public void A_forbidden_trigger_throws(GenerationJobStatus status, string trigger, bool allowed)
    {
        // Arrange
        var job = Job(status, GenerationStage.Translate);

        // Act
        var act = () => GenerationJobMachine.Apply(job, Triggers[trigger], T0.AddMinutes(5));

        // Assert
        if (allowed)
        {
            act.Should().NotThrow();
        }
        else
        {
            act.Should().Throw<GenerationTransitionException>();
        }
    }

    [Theory]
    [InlineData(GenerationStage.Setup)]
    [InlineData(GenerationStage.Extract)]
    [InlineData(GenerationStage.Glossary)]
    [InlineData(GenerationStage.Build)]
    public void A_chunk_outside_Translate_is_forbidden(GenerationStage stage)
    {
        // Arrange
        var job = Job(stage: stage);

        // Act
        var can = GenerationJobMachine.CanApply(job, Triggers["chunk"]);

        // Assert
        can.Should().BeFalse();
    }

    [Fact]
    public void A_chunk_past_the_last_one_is_forbidden()
    {
        // Arrange
        var job = Job(stage: GenerationStage.Translate, counters: new GenerationCounters(100, 100, 2, 2));

        // Act
        var can = GenerationJobMachine.CanApply(job, Triggers["chunk"]);

        // Assert
        can.Should().BeFalse();
    }

    [Theory]
    [InlineData(GenerationStage.Setup, GenerationStage.Extract)]
    [InlineData(GenerationStage.Extract, GenerationStage.Glossary)]
    [InlineData(GenerationStage.Glossary, GenerationStage.Translate)]
    [InlineData(GenerationStage.Translate, GenerationStage.Build)]
    public void A_completed_stage_moves_to_the_next_one(GenerationStage stage, GenerationStage next)
    {
        // Arrange
        var job = Job(stage: stage);

        // Act
        var result = GenerationJobMachine.Apply(job, new GenerationTrigger.StageCompleted(), T0.AddMinutes(1));

        // Assert
        new { result.Stage, result.Status }.Should().Be(new { Stage = next, Status = GenerationJobStatus.Running });
    }

    [Fact]
    public void Build_completed_ends_the_job()
    {
        // Arrange
        var job = Job(stage: GenerationStage.Build);

        // Act
        var result = GenerationJobMachine.Apply(job, new GenerationTrigger.StageCompleted(), T0.AddMinutes(30));

        // Assert
        new { result.Stage, result.Status, result.ActiveTime, result.RunningSince }.Should().Be(new
        {
            Stage = GenerationStage.Done,
            Status = GenerationJobStatus.Completed,
            ActiveTime = TimeSpan.FromMinutes(30),
            RunningSince = (DateTimeOffset?)null,
        });
    }

    [Fact]
    public void The_glossary_waits_for_review_when_the_player_asked()
    {
        // Arrange
        var job = Job(stage: GenerationStage.Glossary, review: true);

        // Act
        var result = GenerationJobMachine.Apply(job, new GenerationTrigger.StageCompleted(), T0.AddMinutes(1));

        // Assert
        new { result.Stage, result.Status }.Should().Be(new { Stage = GenerationStage.Glossary, Status = GenerationJobStatus.WaitingForReview });
    }

    [Fact]
    public void Approving_the_glossary_starts_the_translation()
    {
        // Arrange
        var job = Job(GenerationJobStatus.WaitingForReview, GenerationStage.Glossary, review: true);

        // Act
        var result = GenerationJobMachine.Apply(job, new GenerationTrigger.GlossaryApproved(), T0.AddMinutes(1));

        // Assert
        new { result.Stage, result.Status, result.RunningSince }.Should().Be(new
        {
            Stage = GenerationStage.Translate,
            Status = GenerationJobStatus.Running,
            RunningSince = (DateTimeOffset?)T0.AddMinutes(1),
        });
    }

    [Fact]
    public void A_completed_stage_takes_the_counters_it_reports()
    {
        // Arrange
        var job = Job(stage: GenerationStage.Extract, counters: GenerationCounters.Zero);

        // Act
        var result = GenerationJobMachine.Apply(job, new GenerationTrigger.StageCompleted(new GenerationCounters(120, 0, 3, 0)), T0);

        // Assert
        result.Counters.Should().Be(new GenerationCounters(120, 0, 3, 0));
    }

    [Fact]
    public void A_chunk_counts_its_strings_and_time()
    {
        // Arrange
        var job = Job(stage: GenerationStage.Translate);

        // Act
        var result = GenerationJobMachine.Apply(job, new GenerationTrigger.ChunkCompleted(50, TimeSpan.FromMinutes(2)), T0.AddMinutes(2));

        // Assert
        new { result.Counters, result.ChunkTime }.Should().Be(new { Counters = new GenerationCounters(100, 50, 2, 1), ChunkTime = TimeSpan.FromMinutes(2) });
    }

    [Fact]
    public void Translated_strings_never_exceed_the_total()
    {
        // Arrange
        var job = Job(stage: GenerationStage.Translate, counters: new GenerationCounters(60, 50, 2, 1));

        // Act
        var result = GenerationJobMachine.Apply(job, new GenerationTrigger.ChunkCompleted(50, TimeSpan.FromMinutes(1)), T0);

        // Assert
        result.Counters.Translated.Should().Be(60);
    }

    [Fact]
    public void The_limit_pauses_with_its_reset_time()
    {
        // Arrange
        var job = Job(stage: GenerationStage.Translate);

        // Act
        var result = GenerationJobMachine.Apply(job, new GenerationTrigger.LimitReached(T0.AddHours(3)), T0.AddMinutes(10));

        // Assert
        new { result.Status, result.PauseReason, result.ResumeAt, result.ActiveTime }.Should().Be(new
        {
            Status = GenerationJobStatus.Paused,
            PauseReason = (GenerationPauseReason?)GenerationPauseReason.Limit,
            ResumeAt = (DateTimeOffset?)T0.AddHours(3),
            ActiveTime = TimeSpan.FromMinutes(10),
        });
    }

    [Fact]
    public void The_player_pauses_without_a_resume_time()
    {
        // Arrange
        var job = Job(stage: GenerationStage.Translate);

        // Act
        var result = GenerationJobMachine.Apply(job, new GenerationTrigger.PausedByPlayer(), T0.AddMinutes(1));

        // Assert
        new { result.Status, result.PauseReason, result.ResumeAt }.Should().Be(new
        {
            Status = GenerationJobStatus.Paused,
            PauseReason = (GenerationPauseReason?)GenerationPauseReason.Player,
            ResumeAt = (DateTimeOffset?)null,
        });
    }

    [Fact]
    public void Resuming_keeps_the_stage_and_chunk_and_clears_the_pause()
    {
        // Arrange
        var paused = GenerationJobMachine.Apply(
            Job(stage: GenerationStage.Translate, counters: new GenerationCounters(100, 50, 2, 1)),
            new GenerationTrigger.LimitReached(T0.AddHours(3)),
            T0);

        // Act
        var result = GenerationJobMachine.Apply(paused, new GenerationTrigger.Resumed(), T0.AddHours(3));

        // Assert
        new { result.Status, result.Stage, result.Counters, result.PauseReason, result.ResumeAt, result.RunningSince }.Should().Be(new
        {
            Status = GenerationJobStatus.Running,
            Stage = GenerationStage.Translate,
            Counters = new GenerationCounters(100, 50, 2, 1),
            PauseReason = (GenerationPauseReason?)null,
            ResumeAt = (DateTimeOffset?)null,
            RunningSince = (DateTimeOffset?)T0.AddHours(3),
        });
    }

    [Fact]
    public void A_failure_records_the_stage_and_reason()
    {
        // Arrange
        var job = Job(stage: GenerationStage.Glossary);

        // Act
        var result = GenerationJobMachine.Apply(job, new GenerationTrigger.Failed(GenerationFailureKind.SignedOut, "Not logged in"), T0);

        // Assert
        new { result.Status, result.Failure }.Should().Be(new
        {
            Status = GenerationJobStatus.Failed,
            Failure = (GenerationFailure?)new GenerationFailure(GenerationStage.Glossary, GenerationFailureKind.SignedOut, "Not logged in"),
        });
    }

    [Fact]
    public void Retrying_runs_the_failed_stage_again()
    {
        // Arrange
        var failed = GenerationJobMachine.Apply(Job(stage: GenerationStage.Glossary), new GenerationTrigger.Failed(GenerationFailureKind.Agent, "x"), T0);

        // Act
        var result = GenerationJobMachine.Apply(failed, new GenerationTrigger.Retried(), T0.AddMinutes(1));

        // Assert
        new { result.Status, result.Stage, result.Failure }.Should().Be(new
        {
            Status = GenerationJobStatus.Running,
            Stage = GenerationStage.Glossary,
            Failure = (GenerationFailure?)null,
        });
    }

    [Fact]
    public void An_interrupted_run_counts_its_time_up_to_the_last_saved_step()
    {
        // Arrange
        var job = Job(stage: GenerationStage.Translate) with { Updated = T0.AddMinutes(20) };

        // Act
        var result = GenerationJobMachine.Apply(job, new GenerationTrigger.Interrupted(), T0.AddHours(5));

        // Assert
        new { result.Status, result.ActiveTime, result.Updated }.Should().Be(new
        {
            Status = GenerationJobStatus.Interrupted,
            ActiveTime = TimeSpan.FromMinutes(20),
            Updated = T0.AddHours(5),
        });
    }

    [Fact]
    public void Running_time_adds_up_over_runs()
    {
        // Arrange
        var first = GenerationJobMachine.Apply(Job(stage: GenerationStage.Translate), new GenerationTrigger.PausedByPlayer(), T0.AddMinutes(10));
        var second = GenerationJobMachine.Apply(first, new GenerationTrigger.Resumed(), T0.AddHours(1));

        // Act
        var result = GenerationJobMachine.Apply(second, new GenerationTrigger.PausedByPlayer(), T0.AddHours(1).AddMinutes(5));

        // Assert
        result.ActiveTime.Should().Be(TimeSpan.FromMinutes(15));
    }

    [Theory]
    [InlineData(GenerationJobStatus.Running, true)]
    [InlineData(GenerationJobStatus.Paused, true)]
    [InlineData(GenerationJobStatus.WaitingForReview, true)]
    [InlineData(GenerationJobStatus.Interrupted, true)]
    [InlineData(GenerationJobStatus.Failed, true)]
    [InlineData(GenerationJobStatus.Cancelled, false)]
    [InlineData(GenerationJobStatus.Completed, false)]
    public void Only_cancelled_and_completed_jobs_are_closed(GenerationJobStatus status, bool open)
    {
        // Arrange
        var job = Job(status);

        // Act
        var isOpen = job.IsOpen;

        // Assert
        isOpen.Should().Be(open);
    }
}
