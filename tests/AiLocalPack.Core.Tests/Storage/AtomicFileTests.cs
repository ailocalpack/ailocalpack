using System.Text;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Storage;

public sealed class AtomicFileTests
{
    public static bool HostIsNotWindows => !OperatingSystem.IsWindows();

    [Fact(SkipWhen = nameof(HostIsNotWindows), Skip = "Only Windows refuses a rename over a file a reader has open")]
    public void Replaces_a_file_once_a_reader_lets_it_go()
    {
        // Arrange
        using var dir = new TempDir().File("file.json", "old");
        var path = dir.Combine("file.json");
        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Its own thread, not a pool timer: with the pool busy running other tests a timer fired after the 2 s of retries.
        var release = new Thread(() =>
        {
            Thread.Sleep(150);
            reader.Dispose();
        }) { IsBackground = true };
        release.Start();

        // Act
        AtomicFile.WriteAllBytes(path, "new"u8.ToArray());

        // Assert
        File.ReadAllText(path).Should().Be("new");
    }

    [Fact(SkipWhen = nameof(HostIsNotWindows), Skip = "Only Windows refuses a rename over a file a reader has open")]
    public void A_reader_that_never_lets_go_fails_the_write_and_keeps_the_old_file()
    {
        // Arrange
        using var dir = new TempDir().File("file.json", "old");
        var path = dir.Combine("file.json");
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Act
        var act = () => AtomicFile.WriteAllBytes(path, "new"u8.ToArray());

        // Assert
        act.Should().Throw<Exception>().Where(e => e is IOException || e is UnauthorizedAccessException);
        File.ReadAllText(path).Should().Be("old");
    }

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
