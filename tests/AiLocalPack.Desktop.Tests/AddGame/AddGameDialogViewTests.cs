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

    [Fact]
    public Task Folder_with_several_games_shows_the_overview_and_the_review() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var games = new TempDir();
        games.RenPy("Indie/Wren", name: "Wren").RenPy("Indie/Moss", name: "Moss").File("Indie/_CommonRedist/vcredist_x64.exe", "MZ");
        using var services = TestServices.Build(s => s.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>());
        var window = Open(services);
        var dialog = services.GetRequiredService<IDialogService>().Show<AddGameDialogViewModel>();
        await dialog.AnalyzeAsync(games.Combine("Indie"));
        Dispatcher.UIThread.RunJobs();
        var overview = (Find<StackPanel>(window, "OverviewStep").IsVisible, Find<TextBlock>(window, "OverviewTitle").Text);

        // Act
        Find<Button>(window, "StartButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // Assert
        overview.Should().Be((true, "Found 2 games in “Indie”"));
        Find<StackPanel>(window, "SummaryStep").IsVisible.Should().BeTrue();
        Find<ItemsControl>(window, "ReviewProgress").IsVisible.Should().BeTrue();
        Find<TextBlock>(window, "GameName").Text.Should().Be("Moss");
        Find<TextBlock>(window, "StepText").Text.Should().Be("Game 1 of 2");
        Find<Button>(window, "OtherFolderButton").IsVisible.Should().BeFalse();
        window.Close();
    });

    [Fact]
    public Task Overview_covers_are_not_clickable() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var games = new TempDir();
        games.RenPy("Indie/Wren", name: "Wren").RenPy("Indie/Moss", name: "Moss");
        using var services = TestServices.Build(s => s.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>());
        var window = Open(services);
        var dialog = services.GetRequiredService<IDialogService>().Show<AddGameDialogViewModel>();

        // Act
        await dialog.AnalyzeAsync(games.Combine("Indie"));
        Dispatcher.UIThread.RunJobs();

        // Assert
        var strip = Find<ItemsControl>(window, "CoverStrip");
        (strip.ItemCount, strip.GetVisualDescendants().OfType<Button>().Count()).Should().Be((2, 0));
        window.Close();
    });
}
