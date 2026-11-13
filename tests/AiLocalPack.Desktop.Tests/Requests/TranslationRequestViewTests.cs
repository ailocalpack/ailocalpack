using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Requests;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Requests;
using AiLocalPack.Desktop.Tests.Library;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.GamePage;
using AiLocalPack.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Requests;

/// <summary>The Text tab's request (ask, card), the request dialog and the start form's strip rendered headless (task 74).</summary>
public sealed class TranslationRequestViewTests : IDisposable
{
    private static readonly GameId Alpha = GameId.Steam(10);

    private readonly TestRegistry _registry = new();

    public void Dispose() => _registry.Dispose();

    private async Task<ServiceProvider> BuildAsync(Action<TestRegistry>? registry = null)
    {
        registry?.Invoke(_registry);
        _registry.Write();
        var services = TestServices.Build(steam: s => s.AddGame(10, "Alpha"), registry: _registry.Path);
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
        await services.GetRequiredService<PackIndex>().RefreshAsync();
        return services;
    }

    private static MainWindow Open(ServiceProvider services, GamePageTab tab = GamePageTab.Packs)
    {
        var window = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>(), Width = 1280, Height = 800 };
        window.Show();
        var game = services.GetRequiredService<GameLibrary>().Catalog.Find(Alpha)!;
        services.GetRequiredService<INavigationService>().NavigateTo(services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(game, tab));
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static T? Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible);

    [Fact]
    public Task Empty_text_tab_draws_the_ask() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var services = await BuildAsync(r => r.Request(10, "ru", 12));

        // Act
        var window = Open(services);

        // Assert
        (Find<Button>(window, "RequestAsk") is not null, Find<Button>(window, "EmptyCreateWithAi") is not null, Find<Border>(window, "RequestCard"))
            .Should().Be((true, true, null));
        window.Close();
    });

    [Fact]
    public Task Own_request_draws_the_card() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var services = await BuildAsync(r => r.Request(10, "ru", 3));
        services.GetRequiredService<TranslationRequestStore>().Ask(new RequestKey(Alpha, "ru"), FakeRequestDiscussions.Url, DateTimeOffset.UtcNow);

        // Act
        var window = Open(services);

        // Assert
        (Find<TextBlock>(window, "RequestCardTitle")?.Text, Find<Button>(window, "RequestWithdraw") is not null, Find<Panel>(window, "NoPacks"))
            .Should().Be(("Requested: Русский", true, null));
        window.Close();
    });

    [Fact]
    public Task Request_dialog_draws_the_note_and_send() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var services = await BuildAsync();
        var window = Open(services);

        // Act
        Find<Button>(window, "RequestAsk")!.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // Assert
        (Find<TextBox>(window, "RequestNote") is not null, Find<Button>(window, "SendButton") is not null, Find<TextBlock>(window, "RequestGame")?.Text)
            .Should().Be((true, true, "Alpha"));
        window.Close();
    });

    [Fact]
    public Task Start_form_draws_the_request_strip_and_checkbox() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var services = await BuildAsync(r => r.Request(10, "ru", 13, new RegistryRequestClaim("alice", RequestClaimState.Working, GenerationStage.Translate, 45, DateTimeOffset.UtcNow)));

        // Act
        var window = Open(services, GamePageTab.Generation);

        // Assert
        (Find<Border>(window, "GenRequestStrip") is not null, Find<CheckBox>(window, "TakeRequest")?.IsChecked).Should().Be((true, false));
        window.Close();
    });
}
