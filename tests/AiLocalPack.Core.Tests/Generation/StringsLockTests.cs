using AiLocalPack.Core.Generation;

namespace AiLocalPack.Core.Tests.Generation;

public sealed class StringsLockTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void A_held_lock_keeps_another_writer_out_until_the_timeout()
    {
        // Arrange
        var files = new GenerationFiles(_dir.Path);
        using var held = files.LockStrings();

        // Act
        var act = () => files.LockStrings(TimeSpan.FromMilliseconds(100));

        // Assert
        act.Should().Throw<IOException>().WithMessage("strings.json is busy*");
    }

    [Fact]
    public async Task A_waiting_writer_gets_the_lock_once_it_is_released()
    {
        // Arrange
        var files = new GenerationFiles(_dir.Path);
        var held = files.LockStrings();
        _ = Task.Delay(100, TestContext.Current.CancellationToken).ContinueWith(_ => held.Dispose(), TaskScheduler.Default);

        // Act
        var act = () => Task.Run(() => files.LockStrings(TimeSpan.FromSeconds(10)).Dispose(), TestContext.Current.CancellationToken);

        // Assert
        await act.Should().NotThrowAsync();
    }
}
