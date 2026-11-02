using AiLocalPack.Core.Games;

namespace AiLocalPack.Core.Tests.Games;

public sealed class GameVersionTests
{
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);

    [Fact]
    public void Same_build_ids_are_equal()
    {
        // Arrange
        var first = GameVersion.FromBuildId("16826371");

        // Act
        var second = GameVersion.FromBuildId("16826371");

        // Assert
        second.Should().Be(first);
    }

    [Fact]
    public void Different_build_ids_are_not_equal()
    {
        // Arrange
        var first = GameVersion.FromBuildId("1");

        // Act
        var equal = first == GameVersion.FromBuildId("2");

        // Assert
        equal.Should().BeFalse();
    }

    [Fact]
    public void File_hash_sets_with_the_same_content_in_any_order_are_equal()
    {
        // Arrange
        var first = GameVersion.FromFileHashes([new("a.dat", HashA), new("b/c.dat", HashB)]);

        // Act
        var second = GameVersion.FromFileHashes([new("b\\c.dat", HashB), new("a.dat", HashA)]);

        // Assert
        second.Should().Be(first);
        second.GetHashCode().Should().Be(first.GetHashCode());
    }

    [Fact]
    public void Changed_file_hash_gives_a_different_version()
    {
        // Arrange
        var first = GameVersion.FromFileHashes([new("a.dat", HashA)]);

        // Act
        var second = GameVersion.FromFileHashes([new("a.dat", HashB)]);

        // Assert
        second.Should().NotBe(first);
    }

    [Fact]
    public void Paths_use_forward_slashes()
    {
        // Arrange
        // Act
        var version = GameVersion.FromFileHashes([new("data\\www\\a.json", HashA)]);

        // Assert
        version.FileHashes!.Keys.Should().Equal("data/www/a.json");
    }

    [Theory]
    [InlineData("../a.dat")]
    [InlineData("/a.dat")]
    [InlineData("a//b")]
    public void Non_relative_path_is_rejected(string path)
    {
        // Arrange
        var act = () => GameVersion.FromFileHashes([new(path, HashA)]);

        // Act
        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Invalid_hash_is_rejected()
    {
        // Arrange
        var act = () => GameVersion.FromFileHashes([new("a.dat", "xyz")]);

        // Act
        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Same_file_twice_is_rejected()
    {
        // Arrange
        var act = () => GameVersion.FromFileHashes([new("a/b", HashA), new("a\\b", HashA)]);

        // Act
        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Empty_file_set_is_rejected()
    {
        // Arrange
        var act = () => GameVersion.FromFileHashes([]);

        // Act
        // Assert
        act.Should().Throw<ArgumentException>();
    }
}
