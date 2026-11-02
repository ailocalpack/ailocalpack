using System.Text;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Storage;

public sealed class AtomicFileTests
{
    [Fact]
    public void Creates_the_file_and_its_parent_folders()
    {
        // Arrange
        using var dir = new TempDir();
        var path = dir.Combine("a/b/file.json");

        // Act
        AtomicFile.WriteAllBytes(path, "new"u8.ToArray());

        // Assert
        File.ReadAllText(path).Should().Be("new");
    }

    [Fact]
    public void Replaces_an_existing_file()
    {
        // Arrange
        using var dir = new TempDir().File("file.json", "old");
        var path = dir.Combine("file.json");

        // Act
        AtomicFile.WriteAllBytes(path, "new"u8.ToArray());

        // Assert
        File.ReadAllText(path).Should().Be("new");
        Directory.GetFiles(dir.Path).Should().ContainSingle();
    }

    [Fact]
    public void An_exception_in_the_middle_of_writing_leaves_the_old_file_intact()
    {
        // Arrange
        using var dir = new TempDir().File("file.json", "old content");
        var path = dir.Combine("file.json");

        // Act
        var act = () => AtomicFile.Write(path, stream =>
        {
            stream.Write(Encoding.UTF8.GetBytes("half writ"));
            throw new IOException("disk full");
        });

        // Assert
        act.Should().Throw<IOException>().WithMessage("disk full");
        File.ReadAllText(path).Should().Be("old content");
    }

    [Fact]
    public void A_failed_write_leaves_no_temp_file_behind()
    {
        // Arrange
        using var dir = new TempDir().File("file.json", "old");
        var path = dir.Combine("file.json");

        // Act
        var act = () => AtomicFile.Write(path, _ => throw new InvalidOperationException("boom"));

        // Assert
        act.Should().Throw<InvalidOperationException>();
        Directory.GetFiles(dir.Path).Should().ContainSingle().Which.Should().Be(path);
    }

    [Fact]
    public void A_failed_first_write_creates_no_file()
    {
        // Arrange
        using var dir = new TempDir();
        var path = dir.Combine("file.json");

        // Act
        var act = () => AtomicFile.Write(path, _ => throw new InvalidOperationException("boom"));

        // Assert
        act.Should().Throw<InvalidOperationException>();
        Directory.GetFiles(dir.Path).Should().BeEmpty();
    }
}
