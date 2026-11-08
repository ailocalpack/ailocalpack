using AiLocalPack.Desktop.Background;

namespace AiLocalPack.Desktop.Tests.Background;

/// <summary>One app per data folder: a second start shows the running app's window (task 39).</summary>
public sealed class SingleInstanceTests
{
    [Fact]
    public void Second_start_is_refused()
    {
        // Arrange
        using var data = new TempDir();
        using var first = SingleInstance.TryAcquire(data.Path, show: false);

        // Act
        var second = SingleInstance.TryAcquire(data.Path, show: false);

        // Assert
        (first is not null, second is null).Should().Be((true, true));
    }

    [Fact]
    public async Task Second_start_asks_the_first_to_show_its_window()
    {
        // Arrange
        using var data = new TempDir();
        using var first = SingleInstance.TryAcquire(data.Path)!;
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ShowRequested += (_, _) => shown.TrySetResult();

        // Act
        SingleInstance.TryAcquire(data.Path);

        // Assert
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void App_can_start_again_after_the_first_quit()
    {
        // Arrange
        using var data = new TempDir();
        SingleInstance.TryAcquire(data.Path, show: false)!.Dispose();

        // Act
        using var again = SingleInstance.TryAcquire(data.Path, show: false);

        // Assert
        again.Should().NotBeNull();
    }

    [Fact]
    public void Close_to_tray_only_for_the_player_closing_with_the_tray_on()
    {
        // Arrange
        var cases = new[] { (true, true, false), (false, true, false), (true, false, false), (true, true, true) };

        // Act
        var hides = cases.Select(c => CloseToTray.ShouldHide(c.Item1, c.Item2, c.Item3));

        // Assert
        hides.Should().Equal(true, false, false, false);
    }
}
