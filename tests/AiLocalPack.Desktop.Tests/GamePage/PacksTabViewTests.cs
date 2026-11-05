using System.Security.Cryptography;
using System.Text;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Tests.Library;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.GamePage;

/// <summary>The Packs tab rendered headless (task 24).</summary>
public sealed class PacksTabViewTests : IDisposable
{
    private const string AlphaData = "alpha data";

    private readonly TestRegistry _registry = new();

    public void Dispose() => _registry.Dispose();

    private static string AlphaHash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(AlphaData)));

    /// <summary>Game 10 "Alpha" with <c>data/a.json</c>; «lanterns» made for its build, «old» touching a file it lacks.</summary>
    private ServiceProvider Build()
    {
        _registry
            .Pack(10, "ru", "lanterns", PackStatus.Verified, likes: 42)
            .Manifest(10, "ru", "lanterns", "1000", new Dictionary<string, string> { ["data/a.json"] = AlphaHash })
            .Pack(10, "ru", "old", buildId: "800")
            .Manifest(10, "ru", "old", "800", new Dictionary<string, string> { ["data/gone.json"] = new string('b', 64) })
            .Write();
        var services = TestServices.Build(
            steam: s =>
            {
                s.AddGame(10, "Alpha").AddGame(20, "Beta");
                var data = Path.Combine(s.Root, "steamapps", "common", "Alpha", "data");
                Directory.CreateDirectory(data);
                File.WriteAllText(Path.Combine(data, "a.json"), AlphaData);
            },
            registry: _registry.Path);
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
        services.GetRequiredService<PackIndex>().RefreshAsync().GetAwaiter().GetResult();
        return services;
    }

    private static async Task<(MainWindow Window, GamePageViewModel Page)> OpenAsync(ServiceProvider services, uint appId)
    {
        var window = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>(), Width = 1280, Height = 800 };
        window.Show();
        var game = services.GetRequiredService<GameLibrary>().Catalog.Games.Single(g => g.Id == GameId.Steam(appId));
        var page = services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(game);
        services.GetRequiredService<INavigationService>().NavigateTo(page);
        await page.Packs.WhenIdle;
        Dispatcher.UIThread.RunJobs();
        return (window, page);
    }

    private static T Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [Fact]
    public Task List_shows_a_row_per_pack() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var services = Build();

        // Act
        var (window, _) = await OpenAsync(services, 10);

        // Assert
        Find<ItemsControl>(window, "PackList").GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("packRow")).Should().Be(2);
        window.Close();
    });

    [Fact]
    public Task Details_show_the_selected_packs_compatibility() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var services = Build();

        // Act
        var (window, _) = await OpenAsync(services, 10);

        // Assert
        (Find<TextBlock>(window, "DetailsTitle").Text, Find<TextBlock>(window, "CompatTitle").Text, Find<Button>(window, "InstallButton").IsEffectivelyEnabled)
            .Should().Be(("lanterns", "Compatible with your version", true));
        window.Close();
    });

    [Fact]
    public Task Incompatible_pack_shows_install_disabled_with_the_reason() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var services = Build();
        var (window, page) = await OpenAsync(services, 10);

        // Act
        page.Packs.Selected = page.Packs.Rows.Single(r => r.Pack.Id == "old");
        Dispatcher.UIThread.RunJobs();

        // Assert
        (Find<Button>(window, "InstallButton").IsEffectivelyEnabled, Find<TextBlock>(window, "InstallHint").Text)
            .Should().Be((false, "Install is unavailable. You can translate the missing strings with AI instead."));
        window.Close();
    });

    [Fact]
    public Task Game_without_packs_shows_the_empty_state() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var services = Build();

        // Act
        var (window, _) = await OpenAsync(services, 20);

        // Assert
        (Find<Panel>(window, "NoPacks").IsEffectivelyVisible, Find<Grid>(window, "PackSplit").IsEffectivelyVisible).Should().Be((true, false));
        window.Close();
    });
}
