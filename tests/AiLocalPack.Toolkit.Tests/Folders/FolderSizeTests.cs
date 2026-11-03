using AiLocalPack.Toolkit.Folders;

namespace AiLocalPack.Toolkit.Tests.Folders;

public sealed class FolderSizeTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Counts_files_and_bytes_in_every_subfolder()
    {
        // Arrange
        _dir.File("a.txt", "12345").File("sub/b.txt", "123").File("sub/deeper/c.txt", "");

        // Act
        var size = FolderSize.Measure(_dir.Path, TestContext.Current.CancellationToken);

        // Assert
        size.Should().Be(new FolderSize(3, 8));
    }

    [Fact]
    public void Missing_folder_is_empty()
    {
        // Arrange
        var missing = _dir.Combine("gone");

        // Act
        var size = FolderSize.Measure(missing, TestContext.Current.CancellationToken);

        // Assert
        size.Should().Be(new FolderSize(0, 0));
    }

    [Fact]
    public void Cancelling_stops_the_count()
    {
        // Arrange
        _dir.File("a.txt", "1");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // Act
        var act = () => FolderSize.Measure(_dir.Path, cancelled.Token);

        // Assert
        act.Should().Throw<OperationCanceledException>();
    }
}
