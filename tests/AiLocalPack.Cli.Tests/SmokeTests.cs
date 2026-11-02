namespace AiLocalPack.Cli.Tests;

public sealed class SmokeTests
{
    [Fact]
    public async Task Cli_runs_and_exits_cleanly()
    {
        // Arrange: no arguments

        // Act
        var result = await CliRunner.RunAsync();

        // Assert
        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Contain("ailocalpack");
        result.StdErr.Should().BeEmpty();
    }
}
