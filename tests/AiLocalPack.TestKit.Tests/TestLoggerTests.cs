using Microsoft.Extensions.Logging;

namespace AiLocalPack.TestKit.Tests;

public sealed class TestLoggerTests
{
    [Fact]
    public void Records_level_formatted_message_and_exception()
    {
        // Arrange
        var logger = new TestLogger<TestLoggerTests>();
        var error = new InvalidOperationException("boom");

        // Act
        logger.LogWarning(error, "file {Path} is broken", "a.json");

        // Assert
        logger.Entries.Should().ContainSingle().Which.Should().Be(new LogEntry(LogLevel.Warning, "file a.json is broken", error));
    }
}
