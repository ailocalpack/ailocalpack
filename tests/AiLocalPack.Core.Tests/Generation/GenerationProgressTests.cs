using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;

namespace AiLocalPack.Core.Tests.Generation;

/// <summary>Overall percent, stage counter and ETA (task 47).</summary>
public sealed class GenerationProgressTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private static GenerationJob Job(GenerationStage stage, GenerationCounters? counters = null, TimeSpan? chunkTime = null, TimeSpan? estimate = null) =>
        GenerationJobMachine.Create("job1", GameId.Steam(620), "Sample Quest", "ru", "fake", GenerationOptions.Default, T0, estimate) with
        {
            Stage = stage,
            Counters = counters ?? GenerationCounters.Zero,
            ChunkTime = chunkTime ?? TimeSpan.Zero,
        };

    [Theory]
    [InlineData(GenerationStage.Setup, 0)]
    [InlineData(GenerationStage.Extract, 5)]
    [InlineData(GenerationStage.Glossary, 10)]
    [InlineData(GenerationStage.Translate, 20)]
    [InlineData(GenerationStage.Build, 90)]
    public void Percent_starts_each_stage_at_the_weights_of_the_ones_before(GenerationStage stage, int percent)
    {
        // Arrange
        var job = Job(stage);

        // Act
        var progress = GenerationProgress.Of(job);

        // Assert
        progress.Percent.Should().Be(percent);
    }

    [Fact]
    public void Translate_moves_the_percent_by_chunks()
    {
        // Arrange
        var job = Job(GenerationStage.Translate, new GenerationCounters(400, 200, 8, 4));

        // Act
        var progress = GenerationProgress.Of(job);

        // Assert
        new { progress.Percent, progress.StageDone, progress.StageTotal }.Should().Be(new { Percent = 55, StageDone = (int?)4, StageTotal = (int?)8 });
    }

    [Fact]
    public void A_completed_job_is_100_with_nothing_left()
    {
        // Arrange
        var job = Job(GenerationStage.Done);

        // Act
        var progress = GenerationProgress.Of(job);

        // Assert
        progress.Should().Be(new GenerationProgress(100, null, null, TimeSpan.Zero));
    }

    [Fact]
    public void The_ETA_comes_from_the_measured_chunks()
    {
        // Arrange
        var job = Job(GenerationStage.Translate, new GenerationCounters(500, 100, 10, 2), chunkTime: TimeSpan.FromMinutes(6), estimate: TimeSpan.FromHours(9));

        // Act
        var progress = GenerationProgress.Of(job);

        // Assert
        progress.Remaining.Should().Be(TimeSpan.FromMinutes(3 * 8) + GenerationProgress.BuildAllowance);
    }

    [Fact]
    public void Before_any_chunk_the_ETA_is_the_estimate_for_what_is_left()
    {
        // Arrange
        var job = Job(GenerationStage.Glossary, estimate: TimeSpan.FromHours(2));

        // Act
        var progress = GenerationProgress.Of(job);

        // Assert
        progress.Remaining.Should().Be(TimeSpan.FromHours(2) * 0.9);
    }

    [Fact]
    public void Without_an_estimate_or_chunks_there_is_no_ETA()
    {
        // Arrange
        var job = Job(GenerationStage.Extract);

        // Act
        var progress = GenerationProgress.Of(job);

        // Assert
        progress.Remaining.Should().BeNull();
    }

    [Fact]
    public void During_Build_the_ETA_is_the_build_allowance()
    {
        // Arrange
        var job = Job(GenerationStage.Build, new GenerationCounters(500, 500, 10, 10), chunkTime: TimeSpan.FromMinutes(30));

        // Act
        var progress = GenerationProgress.Of(job);

        // Assert
        progress.Remaining.Should().Be(GenerationProgress.BuildAllowance);
    }
}
