using AiLocalPack.Desktop.Services;

namespace AiLocalPack.Desktop.Tests;

public sealed class ToastServiceTests
{
    [Fact]
    public void Toast_with_the_same_key_replaces_the_shown_one()
    {
        // Arrange
        var toasts = new ToastService();
        toasts.Show(new ToastViewModel(ToastTone.Ok, "first", [], "game"));

        // Act
        toasts.Show(new ToastViewModel(ToastTone.Bad, "second", [], "game"));

        // Assert
        toasts.Items.Select(t => t.Text).Should().Equal("second");
    }

    [Fact]
    public void Oldest_toast_goes_beyond_the_limit()
    {
        // Arrange
        var toasts = new ToastService();

        // Act
        for (var i = 1; i <= ToastService.MaxShown + 1; i++)
        {
            toasts.Show(new ToastViewModel(ToastTone.Info, $"t{i}", []));
        }

        // Assert
        toasts.Items.Select(t => t.Text).Should().Equal("t2", "t3", "t4");
    }

    [Fact]
    public void Action_closes_the_toast_then_runs()
    {
        // Arrange
        var toasts = new ToastService();
        var ran = false;
        var toast = new ToastViewModel(ToastTone.Ok, "done", [new ToastAction("Undo", () => ran = true)]);
        toasts.Show(toast);

        // Act
        toast.Actions.Single().Command.Execute(null);

        // Assert
        (ran, toasts.Items.Count).Should().Be((true, 0));
    }
}
