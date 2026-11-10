using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Tests.Install;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.GamePage;
using AiLocalPack.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Generation;

/// <summary>The result card and Packs → Mine rendered headless at 1280×800 (task 55).</summary>
public sealed class GenerationResultViewTests
{
    private static (MainWindow Window, GamePageViewModel Page) Open(InstallBed bed, GamePageTab tab)
    {
        var window = new MainWindow { DataContext = bed.Services.GetRequiredService<MainWindowViewModel>(), Width = 1280, Height = 800 };
        window.Show();
        var page = bed.Services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(bed.Game, tab);
        bed.Services.GetRequiredService<INavigationService>().NavigateTo(page);
        Dispatcher.UIThread.RunJobs();
        return (window, page);
    }

    private static T Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [Fact]
    public Task Result_card_shows_the_tiles_and_install_and_play() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack();

        // Act
        var (window, _) = Open(bed, GamePageTab.Generation);

        // Assert
        (Find<Border>(window, "ResultCard").IsEffectivelyVisible,
            Find<ItemsControl>(window, "ResultTiles").ItemCount,
            Find<Button>(window, "ResultPlay").IsEffectivelyEnabled,
            Find<Button>(window, "ResultDiscard").IsEffectivelyVisible,
            Find<Border>(window, "ShareStrip").IsVisible)
            .Should().Be((true, 4, true, true, false));
        window.Close();
    });

    [Fact]
    public Task Tried_result_shows_the_share_strip() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack(tried: new DateTimeOffset(2026, 11, 9, 21, 0, 0, TimeSpan.Zero));

        // Act
        var (window, _) = Open(bed, GamePageTab.Generation);

        // Assert
        (Find<Border>(window, "ShareStrip").IsEffectivelyVisible, Find<Button>(window, "SharePublish").IsEffectivelyEnabled, Find<Button>(window, "ResultShowInPacks").IsEffectivelyVisible)
            .Should().Be((true, true, true));
        window.Close();
    });

    [Fact]
    public Task Mine_shows_the_local_pack_with_its_details() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack();

        // Act
        var (window, _) = Open(bed, GamePageTab.Packs);

        // Assert
        (Find<ItemsControl>(window, "MineList").ItemCount, Find<TextBlock>(window, "MineTitle").Text, Find<TextBlock>(window, "MineCoverage").Text,
            Find<Button>(window, "MineDelete").IsEffectivelyEnabled)
            .Should().Be((1, "Claude Code translation", "100% of strings", true));
        window.Close();
    });
}
