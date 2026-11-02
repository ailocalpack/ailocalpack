namespace AiLocalPack.TestKit.Tests;

public sealed class TempDirTests
{
    [Fact]
    public void Creates_the_given_tree()
    {
        // Arrange
        using var dir = new TempDir();

        // Act
        dir.File("game/data/strings.json", "{}")
            .File("game/Game.exe", [1, 2, 3])
            .Dir("empty/nested")
            .Files(new Dictionary<string, string> { ["a.txt"] = "a", ["b/c.txt"] = "c" });

        // Assert
        Directory.Exists(dir.Path).Should().BeTrue();
        File.ReadAllText(dir.Combine("game/data/strings.json")).Should().Be("{}");
        File.ReadAllBytes(dir.Combine("game/Game.exe")).Should().Equal(1, 2, 3);
        Directory.Exists(dir.Combine("empty/nested")).Should().BeTrue();
        File.ReadAllText(dir.Combine("a.txt")).Should().Be("a");
        File.ReadAllText(dir.Combine("b/c.txt")).Should().Be("c");
    }

    [Fact]
    public void Each_instance_gets_its_own_folder()
    {
        // Arrange
        using var first = new TempDir();

        // Act
        using var second = new TempDir();

        // Assert
        second.Path.Should().NotBe(first.Path);
    }

    [Fact]
    public void Dispose_removes_the_folder()
    {
        // Arrange
        var dir = new TempDir().File("x/y.txt", "y");

        // Act
        dir.Dispose();

        // Assert
        Directory.Exists(dir.Path).Should().BeFalse();
    }

    [Fact]
    public void Dispose_removes_read_only_files()
    {
        // Arrange
        var dir = new TempDir().File("locked/ro.txt", "ro");
        File.SetAttributes(dir.Combine("locked/ro.txt"), FileAttributes.ReadOnly);

        // Act
        dir.Dispose();

        // Assert
        Directory.Exists(dir.Path).Should().BeFalse();
    }

    [Fact]
    public void Dispose_is_idempotent_and_tolerates_a_folder_already_deleted()
    {
        // Arrange
        var dir = new TempDir();
        Directory.Delete(dir.Path);

        // Act
        var act = () =>
        {
            dir.Dispose();
            dir.Dispose();
        };

        // Assert
        act.Should().NotThrow();
        Directory.Exists(dir.Path).Should().BeFalse();
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("a/../../outside.txt")]
    public void Rejects_paths_that_escape_the_folder(string path)
    {
        // Arrange
        using var dir = new TempDir();

        // Act
        var act = () => dir.File(path, "x");

        // Assert
        act.Should().Throw<ArgumentException>().WithMessage("*escapes*");
    }

    [Fact]
    public void Rejects_absolute_paths()
    {
        // Arrange
        using var dir = new TempDir();
        var absolute = Path.Combine(Path.GetTempPath(), "outside.txt");

        // Act
        var act = () => dir.File(absolute, "x");

        // Assert
        act.Should().Throw<ArgumentException>().WithMessage("*relative*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_empty_paths(string path)
    {
        // Arrange
        using var dir = new TempDir();

        // Act
        var act = () => dir.Combine(path);

        // Assert
        act.Should().Throw<ArgumentException>();
    }
}
