using AiLocalPack.Core.Games;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Tests.Settings;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.GamePage;
using AiLocalPack.Desktop.Views;
using AiLocalPack.Toolkit.Generation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Generation;

/// <summary>The Generation tab's start form rendered headless at 1280×800 (task 52).</summary>
public sealed class GenerationTabViewTests
{
    private static ServiceProvider Build(Action<FakeAgentHost, FakeEstimates>? arrange = null)
    {
        var services = TestServices.Build(steam: s => s.AddGame(10, "Alpha"));
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
        services.GetRequiredService<FakeAgentHost>().Probes["claude"] = FakeAgentHost.SignedIn("claude");
        arrange?.Invoke(services.GetRequiredService<FakeAgentHost>(), services.GetRequiredService<FakeEstimates>());
        return services;
    }

    private static (MainWindow Window, GamePageViewModel Page) Open(ServiceProvider services)
    {
        var window = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>(), Width = 1280, Height = 800 };
        window.Show();
        var game = services.GetRequiredService<GameLibrary>().Catalog.Games.Single(g => g.Id == GameId.Steam(10));
        var page = services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(game, GamePageTab.Generation);
        services.GetRequiredService<INavigationService>().NavigateTo(page);
        Dispatcher.UIThread.RunJobs();
        return (window, page);
    }

    private static T Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [Fact]
    public Task Start_form_shows_steps_tiles_agents_and_checks() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = Build();

        // Act
        var (window, _) = Open(services);

        // Assert
        (Find<ItemsControl>(window, "GenerationSteps").ItemCount,
            Find<TextBlock>(window, "StringsValue").Text,
            Find<ItemsControl>(window, "AgentCards").ItemCount,
            Find<ItemsControl>(window, "GenerationChecks").ItemCount,
            Find<Button>(window, "StartGeneration").IsEffectivelyEnabled)
            .Should().Be((5, "1,200", 2, 4, true));
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task Start_off_shows_the_reason_under_it() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = Build((_, estimates) => estimates.Estimate = (_, _) => new TaskCompletionSource<GenerationSizeEstimate>().Task);

        // Act
        var (window, _) = Open(services);

        // Assert
        (Find<Button>(window, "StartGeneration").IsEffectivelyEnabled, Find<TextBlock>(window, "StartBlockText").Text, Find<TextBlock>(window, "StartBlockText").IsEffectivelyVisible)
            .Should().Be((false, "Estimating the size…", true));
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task Start_switches_the_tab_to_the_progress_card() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var services = Build();
        var (window, page) = Open(services);

        // Act
        await page.Generation.StartCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        // Assert
        (Find<Border>(window, "StartForm").IsVisible, Find<Border>(window, "ProgressCard").IsVisible).Should().Be((false, true));
        window.Close();
    });

    [Fact]
    public Task Start_form_fits_the_steam_deck_width() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = Build();

        // Act
        var (window, _) = Open(services);

        // Assert
        Find<Border>(window, "StartForm").Bounds.Width.Should().BeLessThanOrEqualTo(1280 - 48);
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task Tap_on_an_agent_card_chooses_it() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = Build((agents, _) => agents.Probes["codex"] = FakeAgentHost.SignedIn("codex"));
        var (window, page) = Open(services);
        var codex = Find<ItemsControl>(window, "AgentCards").GetVisualDescendants().OfType<Border>()
            .First(b => b.Classes.Contains("genAgent") && b.DataContext is GenerationAgentCard { AgentId: "codex" });

        // Act
        codex.RaiseEvent(new Avalonia.Input.TappedEventArgs(Avalonia.Input.InputElement.TappedEvent, null!) { Source = codex });

        // Assert
        page.Generation.SelectedAgent!.AgentId.Should().Be("codex");
        window.Close();
        return Task.CompletedTask;
    });
}
