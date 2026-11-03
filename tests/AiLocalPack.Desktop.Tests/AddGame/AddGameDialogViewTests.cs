using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.AddGame;
using AiLocalPack.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.AddGame;

public sealed class AddGameDialogViewTests
{
    private static MainWindow Open(ServiceProvider services)
    {
        var window = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>(), Width = 1280, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static T Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [Fact]
    public Task Folder_button_shows_the_dialog_over_the_window() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = TestServices.Build();
        var window = Open(services);

        // Act
        Find<Button>(window, "AddFolderButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // Assert
        Find<Border>(window, "DialogScrim").IsVisible.Should().BeTrue();
        Find<StackPanel>(window, "PickStep").IsVisible.Should().BeTrue();
        Find<DockPanel>(window, "Main").IsEnabled.Should().BeFalse();
        window.Close();
    });

    [Fact]
    public Task Escape_closes_the_dialog() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = TestServices.Build();
        var window = Open(services);
        services.GetRequiredService<IDialogService>().Show<AddGameDialogViewModel>();
        Dispatcher.UIThread.RunJobs();

        // Act
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        // Assert
        Find<Border>(window, "DialogScrim").IsVisible.Should().BeFalse();
        window.Close();
    });

    [Fact]
    public Task Summary_of_an_analyzed_game_is_shown() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var games = new TempDir();
        games.RenPy("Wren", name: "Wren");
        // The real dispatcher: the analysis runs on a worker thread and the view must only be touched on the UI thread.
        using var services = TestServices.Build(s => s.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>());
        var window = Open(services);
        var dialog = services.GetRequiredService<IDialogService>().Show<AddGameDialogViewModel>();

        // Act
        await dialog.AnalyzeAsync(games.Combine("Wren"));
        Dispatcher.UIThread.RunJobs();

        // Assert
        Find<StackPanel>(window, "SummaryStep").IsVisible.Should().BeTrue();
        Find<TextBlock>(window, "GameName").Text.Should().Be("Wren");
        Find<TextBlock>(window, "EngineFact").Text.Should().Be("Ren'Py");
        Find<Button>(window, "PrimaryButton").Content.Should().Be("Add game");
        window.Close();
    });
}
