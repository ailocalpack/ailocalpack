using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.Views;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Library;

/// <summary>The library screen with statuses (task 21), rendered headless.</summary>
public sealed class LibraryStatusViewTests : IDisposable
{
    private readonly TestRegistry _registry = new();

    public void Dispose() => _registry.Dispose();

    private static MainWindow Open(ServiceProvider services)
    {
        var window = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>(), Width = 1280, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static T Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private ServiceProvider Build(Action<TestRegistry> packs)
    {
        packs(_registry);
        _registry.Write();
        var services = TestServices.Build(steam: s => s.AddGame(10, "Alpha").AddGame(20, "Beta"), registry: _registry.Path);
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
        services.GetRequiredService<PackIndex>().RefreshAsync().GetAwaiter().GetResult();
        return services;
    }

    [Fact]
    public Task Chips_show_each_status_with_its_count() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = Build(r => r.Pack(10, "ru", "full"));

        // Act
        var window = Open(services);

        // Assert
        var chips = Find<ItemsControl>(window, "FilterChips").GetVisualDescendants().OfType<Button>()
            .Select(b => string.Join(' ', b.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)));
        chips.Should().Equal("All 2", "Installed 0", "Pack available 1", "No pack 1", "Needs update 0", "Generating 0");
        window.Close();
    });

    [Fact]
    public Task Card_shows_its_badge() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = Build(r => r.Pack(10, "ru", "full"));

        // Act
        var window = Open(services);

        // Assert
        var badges = Find<ItemsControl>(window, "Games").GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("statusBadge") && b.IsVisible)
            .Select(b => b.GetVisualDescendants().OfType<TextBlock>().Single().Text);
        badges.Should().BeEquivalentTo("Pack available", "No pack");
        window.Close();
    });

    [Fact]
    public Task Language_selector_shows_the_setting() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = Build(_ => { });

        // Act
        var window = Open(services);

        // Assert
        Find<ComboBox>(window, "LanguageBox").SelectedItem.Should().BeOfType<ViewModels.Settings.ChoiceOption>()
            .Which.Value.Should().Be("ru");
        window.Close();
    });

    [Fact]
    public Task Fresh_registry_shows_no_banner() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = Build(_ => { });

        // Act
        var window = Open(services);

        // Assert
        Find<Border>(window, "RegistryBanner").IsVisible.Should().BeFalse();
        window.Close();
    });

    [Fact]
    public Task Unreachable_registry_shows_the_banner_with_retry() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = TestServices.Build(steam: s => s.AddGame(10, "Alpha"));
        services.GetRequiredService<PackIndex>().RefreshAsync().GetAwaiter().GetResult();

        // Act
        var window = Open(services);

        // Assert
        (Find<Border>(window, "RegistryBanner").IsVisible, Find<Button>(window, "RetryRegistry").IsEffectivelyEnabled).Should().Be((true, true));
        window.Close();
    });
}
