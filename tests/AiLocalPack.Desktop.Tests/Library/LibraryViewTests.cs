using AiLocalPack.Core.Games;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Library;

public sealed class LibraryViewTests
{
    private static MainWindow Open(ServiceProvider services, double width = 1280, double height = 800)
    {
        var window = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>(), Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static ServiceProvider WithGames(int count) =>
        TestServices.Build(steam: s =>
        {
            for (var i = 1; i <= count; i++)
            {
                s.AddGame((uint)i, $"Game {i:D2}");
            }
        });

    private static T Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static List<Button> Cards(Window window) =>
        [.. Find<ItemsControl>(window, "Games").GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("gameCard"))];

    [Fact]
    public Task Renders_a_card_per_game() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = WithGames(9);

        // Act
        var window = Open(services);

        // Assert
        Cards(window).Should().HaveCount(9);
        window.Close();
    });

    [Fact]
    public Task Shows_six_columns_at_1280_by_800() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = WithGames(9);

        // Act
        var window = Open(services, 1280, 800);

        // Assert
        Find<ItemsControl>(window, "Games").GetVisualDescendants().OfType<CardGrid>().Single().Columns.Should().Be(6);
        var cards = Cards(window);
        cards[0].Bounds.Width.Should().BeGreaterThanOrEqualTo(176);
        ((Control)cards[6].GetVisualParent()!).Bounds.Top.Should().BeGreaterThan(((Control)cards[5].GetVisualParent()!).Bounds.Top);
        window.Close();
    });

    [Fact]
    public Task Covers_are_two_by_three() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = WithGames(1);

        // Act
        var window = Open(services);

        // Assert
        var cover = Cards(window)[0].GetVisualDescendants().OfType<AspectBox>().Single();
        cover.Bounds.Height.Should().BeApproximately(cover.Bounds.Width * 1.5, 0.5);
        window.Close();
    });

    [Fact]
    public Task Ctrl_f_focuses_the_search() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = WithGames(3);
        var window = Open(services);
        Cards(window)[0].Focus();

        // Act
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        // Assert
        Find<TextBox>(window, "SearchBox").IsFocused.Should().BeTrue();
        window.Close();
    });

    [Fact]
    public Task Typing_a_search_filters_the_cards() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = WithGames(12);
        var window = Open(services);

        // Act
        Find<TextBox>(window, "SearchBox").Text = "game 1";
        Dispatcher.UIThread.RunJobs();

        // Assert
        // "Game 10", "Game 11", "Game 12" ("Game 01" doesn't contain "game 1").
        Cards(window).Should().HaveCount(3);
        window.Close();
    });

    [Fact]
    public Task Shows_skeletons_and_the_banner_before_the_first_scan() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = TestServices.Build();

        // Act
        var window = Open(services);

        // Assert
        Find<Border>(window, "ScanBanner").IsVisible.Should().BeTrue();
        Find<ItemsControl>(window, "Skeletons").IsVisible.Should().BeTrue();
        Find<ItemsControl>(window, "Skeletons").ItemCount.Should().Be(LibraryViewModel.SkeletonCount);
        Find<StackPanel>(window, "EmptyState").IsVisible.Should().BeFalse();
        window.Close();
    });

    [Fact]
    public Task Shows_steam_not_found_when_the_scan_finds_nothing() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = TestServices.Build();
        var window = Open(services);

        // Act
        services.GetRequiredService<GameLibrary>().Refresh();
        Dispatcher.UIThread.RunJobs();

        // Assert
        Find<StackPanel>(window, "EmptyState").IsVisible.Should().BeTrue();
        Find<Border>(window, "ScanBanner").IsVisible.Should().BeFalse();
        Find<ItemsControl>(window, "Skeletons").IsVisible.Should().BeFalse();
        window.Close();
    });

    [Fact]
    public Task Shows_nothing_found_for_a_search_without_matches() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = WithGames(3);
        var window = Open(services);

        // Act
        Find<TextBox>(window, "SearchBox").Text = "zelda";
        Dispatcher.UIThread.RunJobs();

        // Assert
        Find<StackPanel>(window, "NoResults").IsVisible.Should().BeTrue();
        Find<ItemsControl>(window, "Games").IsVisible.Should().BeFalse();
        window.Close();
    });

    [Fact]
    public Task Blocked_game_shows_the_anti_cheat_badge() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = WithGames(1);
        var catalog = services.GetRequiredService<GameCatalog>();
        catalog.Update(catalog.Games[0].Id, g => g with { AntiCheat = "battleye" });

        // Act
        var window = Open(services);

        // Assert
        var card = Cards(window).Single();
        card.Classes.Should().Contain("blocked");
        card.GetVisualDescendants().OfType<TextBlock>().Should().Contain(t => t.Text == "Anti-cheat" && t.IsEffectivelyVisible);
        window.Close();
    });
}
