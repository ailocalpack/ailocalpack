using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Ratings;

/// <summary>The rating block and the rating prompt rendered headless (task 63).</summary>
public sealed class RatingViewTests
{
    private static async Task<MainWindow> OpenAsync(RatingBed bed)
    {
        var window = new MainWindow { DataContext = bed.Services.GetRequiredService<MainWindowViewModel>(), Width = 1280, Height = 800 };
        window.Show();
        var page = bed.Page();
        bed.Services.GetRequiredService<INavigationService>().NavigateTo(page);
        await page.Packs.WhenIdle;
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static T? Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible);

    [Fact]
    public Task The_block_shows_like_works_and_doesnt_work() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var bed = await RatingBed.CreateAsync();
        bed.Played();

        // Act
        var window = await OpenAsync(bed);

        // Assert
        (Find<Button>(window, "LikeButton") is not null, Find<Button>(window, "WorksButton") is not null, Find<Button>(window, "DoesNotWorkButton") is not null,
            Find<TextBox>(window, "RatingComment") is not null).Should().Be((true, true, true, true));
        window.Close();
    });

    [Fact]
    public Task The_block_is_not_drawn_right_after_installing() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var bed = await RatingBed.CreateAsync();

        // Act
        var window = await OpenAsync(bed);

        // Assert
        Find<Border>(window, "RatingBlock").Should().BeNull();
        window.Close();
    });

    [Fact]
    public Task The_prompt_strip_asks_about_the_installed_pack() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var bed = await RatingBed.CreateAsync();
        bed.Played();

        // Act
        var window = await OpenAsync(bed);

        // Assert
        (Find<TextBlock>(window, "RatingPromptText")?.Text, Find<Button>(window, "RateWorks") is not null, Find<Button>(window, "RateLater") is not null)
            .Should().Be(("How is «full»? Your answer helps other players.", true, true));
        window.Close();
    });
}
