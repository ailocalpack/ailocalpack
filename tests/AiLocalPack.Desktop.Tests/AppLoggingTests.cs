using AiLocalPack.Desktop.Logging;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Tests;

public sealed class AppLoggingTests
{
    [Fact]
    public void Writes_log_entries_to_a_rolling_file()
    {
        // Arrange
        using var dir = new TempDir();
        var factory = AppLogging.CreateFactory(dir.Path);

        // Act
        factory.CreateLogger("Test").LogError("something broke {Code}", 42);

        // Assert
        factory.Dispose();
        var file = Directory.GetFiles(dir.Path, "app-*.log").Should().ContainSingle().Subject;
        File.ReadAllText(file).Should().Contain("something broke 42");
    }

    [Fact]
    public void Creates_the_log_directory_when_missing()
    {
        // Arrange
        using var dir = new TempDir();
        var logs = dir.Combine("nested/logs");
        var factory = AppLogging.CreateFactory(logs);

        // Act
        factory.CreateLogger("Test").LogInformation("hello");

        // Assert
        factory.Dispose();
        Directory.GetFiles(logs, "app-*.log").Should().ContainSingle();
    }
}
