using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests;

public sealed class DialogServiceTests
{
    private sealed class TestDialog : DialogViewModel
    {
        public int ClosedCount { get; private set; }

        public override void OnClosed() => ClosedCount++;
    }

    /// <summary>Uses Esc itself (like a dialog leaving a text field).</summary>
    private sealed class EscapeDialog : DialogViewModel
    {
        public override bool TryHandleEscape() => true;
    }

    [Fact]
    public void Show_makes_the_dialog_current()
    {
        // Arrange
        using var services = new ServiceCollection().BuildServiceProvider();
        var dialogs = new DialogService(services);

        // Act
        var dialog = dialogs.Show<TestDialog>();

        // Assert
        dialogs.Current.Should().BeSameAs(dialog);
    }

    [Fact]
    public void Showing_another_dialog_closes_the_first()
    {
        // Arrange
        using var services = new ServiceCollection().BuildServiceProvider();
        var dialogs = new DialogService(services);
        var first = dialogs.Show<TestDialog>();

        // Act
        dialogs.Show<TestDialog>();

        // Assert
        first.ClosedCount.Should().Be(1);
    }

    [Fact]
    public void Closing_a_dialog_that_is_not_shown_does_nothing()
    {
        // Arrange
        using var services = new ServiceCollection().BuildServiceProvider();
        var dialogs = new DialogService(services);
        var first = dialogs.Show<TestDialog>();
        var second = dialogs.Show<TestDialog>();

        // Act
        dialogs.Close(first);

        // Assert
        dialogs.Current.Should().BeSameAs(second);
        first.ClosedCount.Should().Be(1);
    }

    [Fact]
    public void Escape_closes_the_dialog_instead_of_going_back()
    {
        // Arrange
        using var services = TestServices.Build();
        var window = services.GetRequiredService<MainWindowViewModel>();
        window.OpenSettingsCommand.Execute(null);
        var dialogs = services.GetRequiredService<IDialogService>();
        dialogs.Show<TestDialog>();

        // Act
        window.EscapeCommand.Execute(null);

        // Assert
        dialogs.Current.Should().BeNull();
        window.CurrentPage.Should().BeOfType<SettingsViewModel>();
    }

    [Fact]
    public void Escape_used_by_the_dialog_keeps_it_open()
    {
        // Arrange
        using var services = TestServices.Build();
        var window = services.GetRequiredService<MainWindowViewModel>();
        var dialogs = services.GetRequiredService<IDialogService>();
        var dialog = dialogs.Show<EscapeDialog>();

        // Act
        window.EscapeCommand.Execute(null);

        // Assert
        dialogs.Current.Should().BeSameAs(dialog);
    }

    [Fact]
    public void Back_is_disabled_while_a_dialog_is_open()
    {
        // Arrange
        using var services = TestServices.Build();
        var window = services.GetRequiredService<MainWindowViewModel>();
        window.OpenSettingsCommand.Execute(null);

        // Act
        services.GetRequiredService<IDialogService>().Show<TestDialog>();

        // Assert
        window.HasDialog.Should().BeTrue();
        window.GoBackCommand.CanExecute(null).Should().BeFalse();
    }
}
