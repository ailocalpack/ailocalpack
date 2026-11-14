using AiLocalPack.Core.Games;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Steam;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.GamePage;
using AiLocalPack.Desktop.ViewModels.Library;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Library;

/// <summary>The games' own languages (task 80): read from Steam, the library's "In game", the Text tab's box, the start form's warning.</summary>
public sealed class OfficialLanguagesTests : IDisposable
{
    private readonly TestRegistry _registry = new();

    public void Dispose() => _registry.Dispose();

    /// <summary>
    /// Steam games 10 "Alpha" (Russian interface and subtitles in Steam's app info, Steam starts it in English) and 20
    /// "Beta" (not in the app info), target language Russian.
    /// </summary>
    private async Task<ServiceProvider> BuildAsync(
        Action<TestRegistry>? packs = null, FakeSteamStore? store = null, string alphaLanguage = "english")
    {
        packs?.Invoke(_registry);
        _registry.Write();
        var services = TestServices.Build(
            services =>
            {
                if (store is not null)
                {
                    services.AddSingleton(store);
                }
            },
            steam: s =>
            {
                s.AddGame(10, "Alpha", language: alphaLanguage).AddGame(20, "Beta");
                new SteamAppInfoWriter()
                    .AddApp(10, "Alpha", new AppInfoLanguage("english", FullAudio: true), new AppInfoLanguage("russian", Subtitles: true))
                    .WriteTo(s.Root);
            },
            registry: _registry.Path);
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
        await services.GetRequiredService<PackIndex>().RefreshAsync();
        return services;
    }

    private static LibraryViewModel Library(ServiceProvider services) => services.GetRequiredService<IPageFactory>().Create<LibraryViewModel>();

    private static GameCardViewModel Card(ServiceProvider services, string name) => Library(services).Cards.Single(c => c.Name == name);

    private static GamePageViewModel Page(ServiceProvider services, uint appId, GamePageTab tab = GamePageTab.Default) =>
        services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(
            services.GetRequiredService<GameLibrary>().Catalog.Games.Single(g => g.Id == GameId.Steam(appId)), tab);

    private static FakeSteamStore StoreWith(uint appId, params OfficialLanguage[] languages)
    {
        var store = new FakeSteamStore();
        store.Answers[appId] = new SteamStoreLanguagesResult(SteamStoreLookup.Found, languages);
        return store;
    }

    [Fact]
    public async Task A_game_steam_lists_with_the_language_is_in_game()
    {
        // Arrange
        using var services = await BuildAsync();

        // Act
        var card = Card(services, "Alpha");

        // Assert
        (card.StatusKind, card.BadgeText, card.ContextLine, card.HasQuickAction).Should().Be((LocalizationStatusKind.InGame, "In game", "interface, subtitles", false));
    }

    [Fact]
    public async Task An_in_game_card_with_registry_packs_counts_them()
    {
        // Arrange
        using var services = await BuildAsync(r => r.Pack(10, "ru", "lanterns"));

        // Act
        var card = Card(services, "Alpha");

        // Assert
        (card.StatusKind, card.ContextLine).Should().Be((LocalizationStatusKind.InGame, "+1 translation"));
    }

    [Fact]
    public async Task The_in_game_filter_keeps_only_those_games()
    {
        // Arrange
        using var services = await BuildAsync();
        var library = Library(services);

        // Act
        library.SelectedFilter = LibraryFilter.InGame;

        // Assert
        library.VisibleCards.Select(c => c.Name).Should().Equal("Alpha");
    }

    [Fact]
    public async Task A_game_missing_from_steams_app_info_is_looked_up_in_the_store()
    {
        // Arrange
        var store = StoreWith(20, new OfficialLanguage("ru", true, false, false));

        // Act
        using var services = await BuildAsync(store: store);

        // Assert
        (Card(services, "Beta").StatusKind, Card(services, "Beta").ContextLine).Should().Be((LocalizationStatusKind.InGame, "text"));
    }

    [Fact]
    public async Task A_store_answer_is_not_asked_again_on_the_next_scan()
    {
        // Arrange
        var store = StoreWith(20, new OfficialLanguage("de", true, false, false));
        using var services = await BuildAsync(store: store);
        var official = services.GetRequiredService<GameOfficialLanguages>();

        // Act
        services.GetRequiredService<GameLibrary>().Refresh();
        await official.WhenIdle;

        // Assert
        store.Asked.Should().Equal(20u);
    }

    [Fact]
    public async Task Too_many_requests_stops_the_store_lookups_until_the_next_scan()
    {
        // Arrange
        var store = new FakeSteamStore();

        // Act
        using var services = await BuildAsync(store: store);

        // Assert
        (store.Asked.Count, Card(services, "Beta").StatusKind).Should().Be((1, LocalizationStatusKind.NoPack));
    }

    [Fact]
    public async Task The_text_tab_says_the_game_has_the_language_and_how_to_switch_to_it_in_steam()
    {
        // Arrange
        using var services = await BuildAsync();

        // Act
        var packs = Page(services, 10).Packs;

        // Assert
        (packs.ShowOfficial, packs.OfficialTitle, packs.OfficialHint).Should().Be((
            true,
            "Русский is in the game itself: interface, subtitles.",
            "Steam starts it in English: pick Русский in the game’s Properties in Steam → General → Language, or in the game’s own settings."));
    }

    [Fact]
    public async Task The_text_tab_points_to_the_games_settings_when_steam_already_starts_it_in_the_language()
    {
        // Arrange
        using var services = await BuildAsync(alphaLanguage: "russian");

        // Act
        var packs = Page(services, 10).Packs;

        // Assert
        packs.OfficialHint.Should().Be("Steam already starts it in Русский. If the text still shows another language, pick Русский in the game’s own settings.");
    }

    [Fact]
    public async Task Open_in_steam_opens_the_games_library_page()
    {
        // Arrange
        using var services = await BuildAsync();
        var packs = Page(services, 10).Packs;

        // Act
        packs.OpenInSteamCommand.Execute(null);

        // Assert
        services.GetRequiredService<FakeShell>().OpenedUrls.Should().Equal(new Uri("steam://nav/games/details/10"));
    }

    [Fact]
    public async Task The_text_tab_does_not_offer_to_ask_for_a_language_the_game_has()
    {
        // Arrange
        using var services = await BuildAsync();

        // Act
        var packs = Page(services, 10).Packs;

        // Assert
        (packs.ShowNoPacks, packs.ShowRequestAsk).Should().Be((true, false));
    }

    [Fact]
    public async Task The_text_tab_of_a_game_without_the_language_shows_no_box()
    {
        // Arrange
        using var services = await BuildAsync();

        // Act
        var packs = Page(services, 20).Packs;

        // Assert
        packs.ShowOfficial.Should().BeFalse();
    }

    [Fact]
    public async Task The_header_plays_the_game_in_its_own_language()
    {
        // Arrange
        using var services = await BuildAsync();

        // Act
        var page = Page(services, 10);

        // Assert
        (page.PrimaryText, page.PrimaryCaption).Should().Be(("Play", "via Steam · in Русский, the game’s own"));
    }

    [Fact]
    public async Task The_start_form_warns_that_the_game_has_the_language()
    {
        // Arrange
        using var services = await BuildAsync();

        // Act
        var checks = Page(services, 10, GamePageTab.Generation).Generation.Checks;

        // Assert
        checks[0].Should().Match<GenerationCheckViewModel>(c => c.IsWarning
            && c.Text == "Русский is already in the game (interface, subtitles): your AI translation replaces its text");
    }

    [Fact]
    public async Task The_start_form_has_no_warning_for_a_game_without_the_language()
    {
        // Arrange
        using var services = await BuildAsync();

        // Act
        var checks = Page(services, 20, GamePageTab.Generation).Generation.Checks;

        // Assert
        checks.Should().NotContain(c => c.Text.Contains("already in the game", StringComparison.Ordinal));
    }
}
