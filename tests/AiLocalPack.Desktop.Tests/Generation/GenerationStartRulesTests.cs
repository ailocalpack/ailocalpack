using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Desktop.Generation;

namespace AiLocalPack.Desktop.Tests.Generation;

/// <summary>When <c>Start generation</c> is on, and the reason shown when it isn't (design §6a).</summary>
public sealed class GenerationStartRulesTests
{
    public static TheoryData<bool, bool, EstimateState, bool, bool, GenerationStartBlock?> Rules => new()
    {
        // game available, agent ready, estimate, enough disk, starting → block
        { true, true, EstimateState.Done, true, false, null },
        { true, true, EstimateState.Done, true, true, GenerationStartBlock.Starting },
        { false, true, EstimateState.Done, true, false, GenerationStartBlock.GameUnavailable },
        { true, false, EstimateState.Done, true, false, GenerationStartBlock.AgentNotReady },
        { true, false, EstimateState.Estimating, true, false, GenerationStartBlock.AgentNotReady },
        { true, true, EstimateState.Estimating, false, false, GenerationStartBlock.Estimating },
        { true, true, EstimateState.Failed, false, false, GenerationStartBlock.EstimateFailed },
        { true, true, EstimateState.Done, false, false, GenerationStartBlock.NotEnoughDisk },
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public void Start_is_on_only_when_every_condition_holds(
        bool gameAvailable, bool agentReady, EstimateState estimate, bool enoughDisk, bool starting, GenerationStartBlock? expected)
    {
        // Arrange
        var inputs = new GenerationStartInputs(gameAvailable, agentReady, estimate, enoughDisk, starting);

        // Act
        var block = GenerationStartRules.Resolve(inputs);

        // Assert
        block.Should().Be(expected);
    }

    [Fact]
    public void Start_form_waits_at_setup()
    {
        // Arrange
        GenerationJob? job = null;

        // Act
        var steps = GenerationSteps.Of(job);

        // Assert
        steps.Should().Equal(GenerationStepState.Current, GenerationStepState.Upcoming, GenerationStepState.Upcoming, GenerationStepState.Upcoming, GenerationStepState.Upcoming);
    }

    [Fact]
    public void Running_job_marks_earlier_stages_done()
    {
        // Arrange
        var job = Job(GenerationStage.Translate, GenerationJobStatus.Running);

        // Act
        var steps = GenerationSteps.Of(job);

        // Assert
        steps.Should().Equal(GenerationStepState.Done, GenerationStepState.Done, GenerationStepState.Done, GenerationStepState.Running, GenerationStepState.Upcoming);
    }

    [Fact]
    public void Glossary_under_review_waits_for_the_player()
    {
        // Arrange
        var job = Job(GenerationStage.Glossary, GenerationJobStatus.WaitingForReview);

        // Act
        var steps = GenerationSteps.Of(job);

        // Assert
        steps[2].Should().Be(GenerationStepState.Waiting);
    }

    [Fact]
    public void Failed_stage_is_the_failure_s_stage()
    {
        // Arrange
        var job = Job(GenerationStage.Build, GenerationJobStatus.Failed) with
        {
            Failure = new GenerationFailure(GenerationStage.Build, GenerationFailureKind.TestRun, "crashed"),
        };

        // Act
        var steps = GenerationSteps.Of(job);

        // Assert
        steps.Should().Equal(GenerationStepState.Done, GenerationStepState.Done, GenerationStepState.Done, GenerationStepState.Done, GenerationStepState.Failed);
    }

    [Fact]
    public void Paused_job_shows_the_pause_on_its_stage()
    {
        // Arrange
        var job = Job(GenerationStage.Translate, GenerationJobStatus.Paused);

        // Act
        var steps = GenerationSteps.Of(job);

        // Assert
        steps[3].Should().Be(GenerationStepState.Paused);
    }

    [Fact]
    public void Completed_job_has_every_step_done()
    {
        // Arrange
        var job = Job(GenerationStage.Done, GenerationJobStatus.Completed);

        // Act
        var steps = GenerationSteps.Of(job);

        // Assert
        steps.Should().AllBeEquivalentTo(GenerationStepState.Done);
    }

    private static GenerationJob Job(GenerationStage stage, GenerationJobStatus status) => new()
    {
        Id = "20261109-120000-abcdef",
        Game = GameId.Steam(10),
        GameName = "Alpha",
        Language = "ru",
        Agent = "claude",
        Stage = stage,
        Status = status,
        Created = DateTimeOffset.UnixEpoch,
        Updated = DateTimeOffset.UnixEpoch,
    };
}
