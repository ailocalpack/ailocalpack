using System.Text;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Tests.Packs;

namespace AiLocalPack.Core.Tests.Generation;

/// <summary>Translation quality (Quick / Balanced / Thorough, decided 2026-11-13): stages, job file, progress, estimate, manifest.</summary>
public sealed class GenerationQualityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private static GenerationJob Job(GenerationQuality quality, GenerationStage stage) =>
        GenerationJobMachine.Create("job1", GameId.Steam(620), "Sample Quest", "ru", "fake", new GenerationOptions(Quality: quality), T0) with
        {
            Stage = stage,
            Counters = new GenerationCounters(100, 0, 2, 0),
            RunningSince = T0,
        };

    [Theory]
    [InlineData(GenerationQuality.Quick)]
    [InlineData(GenerationQuality.Balanced)]
    public void Extract_leads_to_the_glossary_without_study(GenerationQuality quality)
    {
        // Arrange
        var job = Job(quality, GenerationStage.Extract);

        // Act
        var next = GenerationJobMachine.Apply(job, new GenerationTrigger.StageCompleted(), T0.AddMinutes(1));

        // Assert
        next.Stage.Should().Be(GenerationStage.Glossary);
    }

    [Fact]
    public void Extract_leads_a_thorough_job_to_study()
    {
        // Arrange
        var job = Job(GenerationQuality.Thorough, GenerationStage.Extract);

        // Act
        var next = GenerationJobMachine.Apply(job, new GenerationTrigger.StageCompleted(), T0.AddMinutes(1));

        // Assert
        next.Stage.Should().Be(GenerationStage.Study);
    }

    [Fact]
    public void Study_leads_to_the_glossary()
    {
        // Arrange
        var job = Job(GenerationQuality.Thorough, GenerationStage.Study);

        // Act
        var next = GenerationJobMachine.Apply(job, new GenerationTrigger.StageCompleted(), T0.AddMinutes(1));

        // Assert
        next.Stage.Should().Be(GenerationStage.Glossary);
    }

    [Fact]
    public void Only_a_thorough_job_has_the_study_stage()
    {
        // Arrange
        var options = GenerationQualityTestsData.All;

        // Act
        var studies = options.Select(o => GenerationStages.Of(new GenerationOptions(Quality: o)).Contains(GenerationStage.Study));

        // Assert
        studies.Should().Equal(false, false, true);
    }

    [Fact]
    public void Quality_and_web_lookup_survive_the_job_file()
    {
        // Arrange
        var job = Job(GenerationQuality.Thorough, GenerationStage.Study) with
        {
            Options = new GenerationOptions(Quality: GenerationQuality.Thorough, WebLookup: true),
        };

        // Act
        var (read, _) = GenerationJobSerializer.Read(GenerationJobSerializer.Write(job));

        // Assert
        (read!.Options.Quality, read.Options.WebLookup, read.Stage).Should().Be((GenerationQuality.Thorough, true, GenerationStage.Study));
    }

    [Theory]
    [InlineData("")]
    [InlineData(""" "quality": "meticulous", """)]
    public void A_job_without_a_known_quality_reads_as_balanced(string quality)
    {
        // Arrange
        var json = $$"""
            {"schemaVersion": 1, "id": "20261113-120000-abcdef", "game": "steam:620", "gameName": "Sample Quest", "language": "ru", "agent": "claude",
             "options": { {{quality}} "reviewGlossary": false }, "stage": "glossary", "status": "running",
             "created": "2026-11-13T12:00:00Z", "updated": "2026-11-13T12:00:00Z"}
            """;

        // Act
        var (read, error) = GenerationJobSerializer.Read(Encoding.UTF8.GetBytes(json));

        // Assert
        (read?.Options.Quality, error).Should().Be(((GenerationQuality?)GenerationQuality.Balanced, (string?)null));
    }

    [Fact]
    public void A_thorough_job_counts_study_in_its_progress()
    {
        // Arrange
        var job = Job(GenerationQuality.Thorough, GenerationStage.Glossary);

        // Act
        var progress = GenerationProgress.Of(job);

        // Assert
        progress.Percent.Should().Be(20);
    }

    [Fact]
    public void Quick_costs_less_and_thorough_more_than_balanced()
    {
        // Arrange
        var text = new TextSize(12_384, 180_000, 1_080_000);

        // Act
        var costs = GenerationQualityTestsData.All.Select(q => GenerationEstimateModel.Estimate(text, EstimateBasis.Toolkit, q)).ToList();

        // Assert
        (costs[0].LimitWindowsHigh < costs[1].LimitWindowsHigh, costs[1].LimitWindowsHigh < costs[2].LimitWindowsHigh,
            costs[0].TimeHigh < costs[1].TimeHigh, costs[1].TimeHigh < costs[2].TimeHigh)
            .Should().Be((true, true, true, true));
    }

    [Fact]
    public void An_estimate_at_another_quality_keeps_the_text()
    {
        // Arrange
        var balanced = GenerationEstimateModel.Estimate(new TextSize(500, 4_000, 24_000), EstimateBasis.FileSizes);

        // Act
        var thorough = balanced.At(GenerationQuality.Thorough);

        // Assert
        (thorough.Text, thorough.Basis, thorough.Quality).Should().Be((balanced.Text, EstimateBasis.FileSizes, GenerationQuality.Thorough));
    }

    [Fact]
    public void The_manifest_keeps_the_agents_quality()
    {
        // Arrange
        var manifest = PackManifestSerializer.Read(PackFixtures.ReadValid("recipe-steam")).Value!;
        var withQuality = manifest with { Agent = new PackAgent("claude-code", "2.1.323", "opus", "thorough") };

        // Act
        var read = PackManifestSerializer.Read(PackManifestSerializer.Write(withQuality));

        // Assert
        read.Value!.Agent!.Quality.Should().Be("thorough");
    }
}

internal static class GenerationQualityTestsData
{
    public static IReadOnlyList<GenerationQuality> All { get; } = [GenerationQuality.Quick, GenerationQuality.Balanced, GenerationQuality.Thorough];
}
