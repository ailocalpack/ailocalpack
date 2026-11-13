using AiLocalPack.Core.Settings;

namespace AiLocalPack.Core.Tests.Settings;

public sealed class TranslateWorkersSettingTests
{
    [Fact]
    public void Two_chunks_run_at_once_by_default()
    {
        // Act
        var workers = AppSettings.Defaults.TranslateWorkerCount;

        // Assert
        workers.Should().Be(2);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(5, 5)]
    [InlineData(50, 8)]
    public void A_stored_value_is_kept_within_the_range(int stored, int expected)
    {
        // Arrange
        var settings = AppSettings.Defaults with { TranslateWorkers = stored };

        // Act
        var workers = settings.TranslateWorkerCount;

        // Assert
        workers.Should().Be(expected);
    }
}
