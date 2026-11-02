using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Desktop.Tests;

public sealed class MainWindowTests
{
    private static MainWindow Create()
    {
        var services = Composition.BuildServices(NullLoggerFactory.Instance);
        var window = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>() };
        window.Closed += (_, _) => services.Dispose();
        return window;
    }

    private static MainWindow Open()
    {
        var window = Create();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Click(Window window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static T Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static bool Shows<TView>(Window window) => window.GetVisualDescendants().OfType<TView>().Any();

    [Fact]
    public Task Renders_chrome_on_library() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        var window = Create();

        // Act
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Assert
        Find<TextBlock>(window, "Logo").Text.Should().Be("ailocalpack");
        Find<Button>(window, "ActivityButton").IsVisible.Should().BeTrue();
        Find<Button>(window, "SettingsButton").IsVisible.Should().BeTrue();
        Find<Button>(window, "BackButton").IsVisible.Should().BeFalse();
        ToolTip.GetTip(Find<Button>(window, "SettingsButton")).Should().Be("Settings");
        Find<TextBlock>(window, "GameCount").Text.Should().Be("2 games");
        Shows<LibraryView>(window).Should().BeTrue();
        window.Close();
    });

    [Fact]
    public Task Clicking_settings_shows_settings_view() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        var window = Open();

        // Act
        Click(window, Find<Button>(window, "SettingsButton"));

        // Assert
        Shows<SettingsView>(window).Should().BeTrue();
        Shows<LibraryView>(window).Should().BeFalse();
        window.Close();
    });

    [Fact]
    public Task Clicking_a_game_shows_game_page_with_back_button() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        var window = Open();
        var game = Find<ItemsControl>(window, "StubGames").GetVisualDescendants().OfType<Button>().First();

        // Act
        Click(window, game);

        // Assert
        Shows<GamePageView>(window).Should().BeTrue();
        var back = Find<Button>(window, "BackButton");
        back.IsVisible.Should().BeTrue();
        back.Content.Should().Be("‹ Library");
        window.Close();
    });

    [Fact]
    public Task Clicking_back_returns_to_library() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        var window = Open();
        Click(window, Find<ItemsControl>(window, "StubGames").GetVisualDescendants().OfType<Button>().First());

        // Act
        Click(window, Find<Button>(window, "BackButton"));

        // Assert
        Shows<LibraryView>(window).Should().BeTrue();
        window.Close();
    });

    [Fact]
    public Task Escape_goes_back() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        var window = Open();
        Click(window, Find<Button>(window, "SettingsButton"));

        // Act
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        // Assert
        Shows<LibraryView>(window).Should().BeTrue();
        window.Close();
    });

    [Fact]
    public Task Minimum_size_fits_steam_deck_screen() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        var window = new MainWindow();

        // Act
        var (minWidth, minHeight) = (window.MinWidth, window.MinHeight);

        // Assert
        minWidth.Should().BeLessThanOrEqualTo(1280);
        minHeight.Should().BeLessThanOrEqualTo(800 - 60);
    });
}
