using AiLocalPack.Desktop.Updates;

namespace AiLocalPack.Desktop.Tests.Updates;

public sealed class VelopackUpdateSourceTests
{
    [Theory]
    [InlineData("win")]
    [InlineData("osx")]
    [InlineData("linux")]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    [InlineData("osx-x64")]
    [InlineData("osx-arm64")]
    [InlineData("linux-x64")]
    public void Stable_channel_isnt_named(string channel)
    {
        // Arrange
        var installed = channel;

        // Act
        var shown = VelopackUpdateSource.ShownChannel(installed);

        // Assert
        shown.Should().BeNull();
    }

    [Theory]
    [InlineData("beta")]
    [InlineData("win-beta")]
    [InlineData("osx-arm64-beta")]
    [InlineData("windows")]
    public void Other_channel_is_named(string channel)
    {
        // Arrange
        var installed = channel;

        // Act
        var shown = VelopackUpdateSource.ShownChannel(installed);

        // Assert
        shown.Should().Be(channel);
    }

    [Fact]
    public void Not_installed_has_no_channel()
    {
        // Arrange
        string? installed = null;

        // Act
        var shown = VelopackUpdateSource.ShownChannel(installed);

        // Assert
        shown.Should().BeNull();
    }
}
