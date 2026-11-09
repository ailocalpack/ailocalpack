using AiLocalPack.Core.Generation;

namespace AiLocalPack.Pipeline.Tests;

/// <summary><c>progress.json</c> (tasks 50, 53): what the progress card reads besides the job's counters.</summary>
public sealed class PipelineProgressFileTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private GenerationFiles Files => new(_dir.Path);

    [Fact]
    public void Warnings_limit_and_build_step_read_back_as_written()
    {
        // Arrange
        var written = PipelineProgress.Empty with
        {
            Recent = [new RecentTranslation("k", "Hi ", "Привет", "whitespace")],
            Warnings = [new TranslationWarning("k", "whitespace", "Hi ", "Привет")],
            Limit = new AgentLimitInfo(0.45, "five_hour", new DateTimeOffset(2026, 11, 9, 18, 0, 0, TimeSpan.Zero)),
            Build = new BuildProgress(BuildStep.TestRun, 12, ["Orbitron"], "Noto Sans"),
        };
        PipelineProgressFile.Update(Files, _ => written);

        // Act
        var read = PipelineProgressFile.Read(Files);

        // Assert
        new { read.Recent, read.Warnings, read.Limit, read.Build }.Should().BeEquivalentTo(new { written.Recent, written.Warnings, written.Limit, written.Build });
    }

    [Fact]
    public void A_build_step_from_a_newer_app_is_left_out()
    {
        // Arrange
        File.WriteAllText(Files.ProgressPath, """{"schemaVersion":1,"recent":[],"usage":{"runs":0,"inputTokens":0,"outputTokens":0,"cacheReadTokens":0,"cacheWriteTokens":0},"build":{"step":"signing","files":1}}""");

        // Act
        var read = PipelineProgressFile.Read(Files);

        // Assert
        read.Build.Should().BeNull();
    }

    [Fact]
    public void A_file_from_before_task_53_has_no_warnings_limit_or_build_step()
    {
        // Arrange
        File.WriteAllText(Files.ProgressPath, """{"schemaVersion":1,"recent":[{"key":"k","original":"Hi","translation":"Привет"}],"usage":{"runs":1,"inputTokens":1,"outputTokens":1,"cacheReadTokens":0,"cacheWriteTokens":0}}""");

        // Act
        var read = PipelineProgressFile.Read(Files);

        // Assert
        new { Warnings = read.Warnings.Count, read.Limit, read.Build, Recent = read.Recent.Single().Warning }
            .Should().Be(new { Warnings = 0, Limit = (AgentLimitInfo?)null, Build = (BuildProgress?)null, Recent = (string?)null });
    }
}
