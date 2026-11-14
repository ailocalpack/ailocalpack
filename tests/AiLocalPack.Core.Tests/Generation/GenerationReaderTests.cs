using System.Text;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;

namespace AiLocalPack.Core.Tests.Generation;

/// <summary>The start form's reader choice (decided 2026-11-14) in the job file.</summary>
public sealed class GenerationReaderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("godot")]
    [InlineData(GenerationOptions.AgentReader)]
    public void The_reader_survives_the_job_file(string reader)
    {
        // Arrange
        var job = GenerationJobMachine.Create("job1", GameId.Steam(620), "Sample Quest", "ru", "fake", new GenerationOptions(Reader: reader), T0);

        // Act
        var (read, _) = GenerationJobSerializer.Read(GenerationJobSerializer.Write(job));

        // Assert
        read!.Options.Reader.Should().Be(reader);
    }

    [Theory]
    [InlineData("")]
    [InlineData(""" "reader": "../../evil", """)]
    public void A_job_without_a_usable_reader_reads_the_detected_engine(string reader)
    {
        // Arrange
        var json = $$"""
            {"schemaVersion": 1, "id": "20261114-120000-abcdef", "game": "steam:620", "gameName": "Sample Quest", "language": "ru", "agent": "claude",
             "options": { {{reader}} "reviewGlossary": false }, "stage": "extract", "status": "running",
             "created": "2026-11-14T12:00:00Z", "updated": "2026-11-14T12:00:00Z"}
            """;

        // Act
        var (read, error) = GenerationJobSerializer.Read(Encoding.UTF8.GetBytes(json));

        // Assert
        (read?.Options.Reader, error).Should().Be(((string?)null, (string?)null));
    }
}
