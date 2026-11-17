using AiLocalPack.Core.Generation;

namespace AiLocalPack.Core.Tests.Generation;

/// <summary>Limits the agent learned about a game, kept in the job's <c>limits.json</c>.</summary>
public sealed class StringLimitsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("lang/en.txt:*", "lang/en.txt:2501", true)]
    [InlineData("lang/en.txt:*", "lang/fr.txt:1", false)]
    [InlineData("items/*/name", "items/sword/name", true)]
    [InlineData("items/sword/name", "items/sword/name", true)]
    [InlineData("items/sword/name", "items/sword/name2", false)]
    public void A_pattern_matches_its_keys(string pattern, string key, bool matches)
    {
        // Arrange
        var limit = new StringLimit(pattern, 64, "buffer");

        // Act
        var result = limit.Matches(key);

        // Assert
        result.Should().Be(matches);
    }

    [Fact]
    public void The_smallest_matching_limit_applies()
    {
        // Arrange
        StringLimit[] limits = [new("lang/*", 128, "lines"), new("lang/en.txt:*", 64, "names"), new("data/*", 8, "other")];

        // Act
        var limit = StringLimit.For(limits, "lang/en.txt:5");

        // Assert
        limit!.MaxBytes.Should().Be(64);
    }

    [Fact]
    public void A_limit_with_the_same_keys_replaces_the_earlier_one()
    {
        // Arrange
        StringLimitsFile.Put(_dir.Path, new StringLimit("lang/en.txt:*", 64, "first guess"));

        // Act
        StringLimitsFile.Put(_dir.Path, new StringLimit("lang/en.txt:*", 48, "measured"));

        // Assert
        StringLimitsFile.Read(_dir.Path).Should().Equal(new StringLimit("lang/en.txt:*", 48, "measured"));
    }

    [Fact]
    public void Zero_bytes_removes_the_limit()
    {
        // Arrange
        StringLimitsFile.Put(_dir.Path, new StringLimit("lang/en.txt:*", 64, "guess"));

        // Act
        StringLimitsFile.Put(_dir.Path, new StringLimit("lang/en.txt:*", 0, string.Empty));

        // Assert
        StringLimitsFile.Read(_dir.Path).Should().BeEmpty();
    }

    [Fact]
    public void A_broken_file_reads_as_no_limits()
    {
        // Arrange
        _dir.File(StringLimitsFile.FileName, "{ not json");

        // Act
        var limits = StringLimitsFile.Read(_dir.Path);

        // Assert
        limits.Should().BeEmpty();
    }
}
