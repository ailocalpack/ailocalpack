using AiLocalPack.Core.Games;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Tests.Library;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.GamePage;

/// <summary>The game page rendered headless (task 22).</summary>
public sealed class GamePageViewTests : IDisposable
{
    private readonly TestRegistry _registry = new();

    public void Dispose() => _registry.Dispose();

    private ServiceProvider Build(Action<TestRegistry>? packs = null)
    {
        packs?.Invoke(_registry);
        _registry.Write();
        var services = TestServices.Build(steam: s => s.AddGame(10, "Alpha").AddGame(20, "Beta"), registry: _registry.Path);
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
        services.GetRequiredService<PackIndex>().RefreshAsync().GetAwaiter().GetResult();
        return services;
    }

    /// <summary>The main window showing the game page of <paramref name="appId"/>; a low <paramref name="height"/> makes it scroll.</summary>
    private static MainWindow OpenGame(ServiceProvider services, uint appId, double height = 800)
    {
        // MinHeight off so a short window makes the page scroll.
        var window = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>(), Width = 1280, MinHeight = 0, Height = height };
        window.Show();
        var game = services.GetRequiredService<GameLibrary>().Catalog.Games.Single(g => g.Id == GameId.Steam(appId));
        services.GetRequiredService<INavigationService>().NavigateTo(services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(game));
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static T Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static Button TabButton(Window window, string label) =>
        Find<Border>(window, "TabBar").GetVisualDescendants().OfType<Button>()
            .Single(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == label));

    private static string ButtonText(Button button) =>
        string.Join(' ', button.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

    [Fact]
    public Task Game_with_a_pack_shows_install_with_its_caption() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = Build(r => r.Pack(10, "ru", "lanterns"));

        // Act
        var window = OpenGame(services, 10);

        // Assert
        (ButtonText(Find<Button>(window, "PrimaryAction")), Find<TextBlock>(window, "PrimaryCaption").Text)
            .Should().Be(("Install best pack", "«lanterns» · alpha · made for your version"));
        window.Close();
    });

    [Fact]
    public Task Game_without_a_pack_shows_generate() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = Build(r => r.Pack(10, "ru", "lanterns"));

        // Act
        var window = OpenGame(services, 20);

        // Assert
        ButtonText(Find<Button>(window, "PrimaryAction")).Should().Be("Generate translation with AI");
        window.Close();
    });

    [Fact]
    public Task Blocked_game_shows_no_primary_action() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = Build(r => r.OptOut(10));

        // Act
        var window = OpenGame(services, 10);

        // Assert
        Find<Button>(window, "PrimaryAction").IsEffectivelyVisible.Should().BeFalse();
        window.Close();
    });

    [Fact]
    public Task Coming_soon_tab_looks_muted_but_stays_enabled() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = Build();

        // Act
        var window = OpenGame(services, 10);

        // Assert
        var audio = TabButton(window, "Audio");
        (audio.Classes.Contains("soon"), audio.IsEffectivelyEnabled).Should().Be((true, true));
        window.Close();
    });

    [Fact]
    public Task Coming_soon_tab_opens_the_illustration() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = Build();
        var window = OpenGame(services, 10);

        // Act
        TabButton(window, "Audio").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // Assert
        (Find<StackPanel>(window, "SoonPanel").IsEffectivelyVisible, Find<TextBlock>(window, "SoonTitle").Text)
            .Should().Be((true, "Audio: coming soon"));
        window.Close();
    });

    [Fact]
    public Task Sticky_bar_is_hidden_at_the_top() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = Build(r => r.Pack(10, "ru", "lanterns"));

        // Act
        var window = OpenGame(services, 10, height: 160);

        // Assert
        Find<Border>(window, "StickyBar").IsVisible.Should().BeFalse();
        window.Close();
    });

    [Fact]
    public Task Sticky_bar_appears_once_the_header_scrolls_away() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = Build(r => r.Pack(10, "ru", "lanterns"));
        var window = OpenGame(services, 10, height: 160);
        var scroller = Find<ScrollViewer>(window, "Scroller");

        // Act
        scroller.Offset = new Vector(0, Find<Border>(window, "TabBar").Bounds.Top + 10);
        Dispatcher.UIThread.RunJobs();

        // Assert
        (Find<Border>(window, "StickyBar").IsVisible, ButtonText(Find<Button>(window, "StickyPrimaryAction")))
            .Should().Be((true, "Install best pack"));
        window.Close();
    });
}
