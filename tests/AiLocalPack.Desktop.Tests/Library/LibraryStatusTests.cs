using AiLocalPack.Core.Library;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.GamePage;
using AiLocalPack.Desktop.ViewModels.Library;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Library;

/// <summary>Library statuses, filter chips, <c>Important first</c>, the language selector and the registry banner (task 21).</summary>
public sealed class LibraryStatusTests : IDisposable
{
    private const string IndexUrl = "https://registry.test/index.json";

    private readonly TestRegistry _registry = new();
    private readonly FakeHttpHandler _http = new();

    public void Dispose() => _registry.Dispose();

    private static LibraryViewModel Create(ServiceProvider services) =>
        services.GetRequiredService<IPageFactory>().Create<LibraryViewModel>();

    private static GameCardViewModel CardOf(LibraryViewModel vm, string name) => vm.Cards.Single(c => c.Name == name);

    private static IEnumerable<string> Names(LibraryViewModel vm) => vm.VisibleCards.Select(c => c.Name);

    /// <summary>Steam games 10 "Alpha", 20 "Beta", 30 "Gamma" (build 1000) and a local registry with what the test added.</summary>
    private async Task<ServiceProvider> BuildAsync(Action<SteamFixture>? steam = null)
    {
        _registry.Write();
        var services = TestServices.Build(
            steam: steam ?? (s => s.AddGame(10, "Alpha").AddGame(20, "Beta").AddGame(30, "Gamma")),
            registry: _registry.Path);
        await services.GetRequiredService<PackIndex>().RefreshAsync();
        return services;
    }

    /// <summary>The registry over https (fake), so a failed fetch falls back to the saved copy.</summary>
    private ServiceProvider BuildOverHttps() =>
        TestServices.Build(
            services => services.AddSingleton(sp => new RegistryIndexClient(
                _http.CreateClient(),
                sp.GetRequiredService<AppPaths>(),
                options: new RegistryIndexClientOptions(TimeSpan.FromSeconds(5), Attempts: 1))),
            steam: s => s.AddGame(10, "Alpha"),
            registry: IndexUrl);

    private void GoOffline() =>
        _http.OnAsync(IndexUrl, (_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("No such host is known.")));

    [Fact]
    public async Task Game_with_a_pack_in_the_language_shows_pack_available()
    {
        // Arrange
        _registry.Pack(10, "ru", "full");
        using var services = await BuildAsync();
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });

        // Act
        var vm = Create(services);

        // Assert
        (CardOf(vm, "Alpha").StatusKind, CardOf(vm, "Alpha").BadgeText).Should().Be((LocalizationStatusKind.PackAvailable, "Pack available"));
    }

    [Fact]
    public async Task Game_without_a_pack_shows_no_pack()
    {
        // Arrange
        _registry.Pack(10, "ru", "full");
        using var services = await BuildAsync();
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });

        // Act
        var vm = Create(services);

        // Assert
        CardOf(vm, "Beta").StatusKind.Should().Be(LocalizationStatusKind.NoPack);
    }

    [Fact]
    public async Task Switching_the_language_changes_the_badges()
    {
        // Arrange
        _registry.Pack(10, "ru", "full").Pack(20, "de", "komplett");
        using var services = await BuildAsync();
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
        var vm = Create(services);

        // Act
        vm.SelectedLanguage = vm.LanguageOptions.Single(o => (string?)o.Value == "de");

        // Assert
        (CardOf(vm, "Alpha").StatusKind, CardOf(vm, "Beta").StatusKind)
            .Should().Be((LocalizationStatusKind.NoPack, LocalizationStatusKind.PackAvailable));
    }

    [Fact]
    public async Task Switching_the_language_saves_the_setting()
    {
        // Arrange
        using var services = await BuildAsync();
        var vm = Create(services);

        // Act
        vm.SelectedLanguage = vm.LanguageOptions.Single(o => (string?)o.Value == "pt-BR");

        // Assert
        services.GetRequiredService<SettingsStore>().Current.GameLanguage.Should().Be("pt-BR");
    }

    [Fact]
    public async Task Language_changed_in_settings_moves_the_selector_and_the_badges()
    {
        // Arrange
        _registry.Pack(20, "de", "komplett");
        using var services = await BuildAsync();
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
        var vm = Create(services);

        // Act
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "de" });

        // Assert
        ((string?)vm.SelectedLanguage.Value, CardOf(vm, "Beta").StatusKind).Should().Be(("de", LocalizationStatusKind.PackAvailable));
    }

    [Fact]
    public async Task System_language_is_preselected_by_default()
    {
        // Arrange
        using var services = await BuildAsync();

        // Act
        var vm = Create(services);

        // Assert
        vm.SelectedLanguage.Value.Should().BeNull();
    }

    [Fact]
    public async Task Chips_count_the_games_of_each_status()
    {
        // Arrange
        _registry.Pack(10, "ru", "full").Pack(20, "ru", "full");
        using var services = await BuildAsync();
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });

        // Act
        var vm = Create(services);

        // Assert
        vm.FilterChips.Select(c => (c.Label, c.Count)).Should().Equal(
            ("All", 3), ("Installed", 0), ("Pack available", 2), ("No pack", 1), ("Needs update", 0), ("Generating", 0));
    }

    [Fact]
    public async Task Chip_counts_ignore_the_search()
    {
        // Arrange
        _registry.Pack(10, "ru", "full").Pack(20, "ru", "full");
        using var services = await BuildAsync();
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
        var vm = Create(services);

        // Act
        vm.Query = "alpha";

        // Assert
        vm.FilterChips.Single(c => c.Filter == LibraryFilter.PackAvailable).Count.Should().Be(2);
    }

    [Fact]
    public async Task Filter_shows_only_games_of_its_status()
    {
        // Arrange
        _registry.Pack(10, "ru", "full").Pack(20, "ru", "full");
        using var services = await BuildAsync();
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
        var vm = Create(services);

        // Act
        vm.FilterChips.Single(c => c.Filter == LibraryFilter.NoPack).SelectCommand.Execute(null);

        // Assert
        (Names(vm).ToList(), vm.FilterChips.Single(c => c.IsSelected).Filter).Should().BeEquivalentTo((new[] { "Gamma" }, LibraryFilter.NoPack));
    }

    [Fact]
    public async Task Filter_and_search_apply_together()
    {
        // Arrange
        _registry.Pack(10, "ru", "full").Pack(20, "ru", "full");
        using var services = await BuildAsync();
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
        var vm = Create(services);
        vm.SelectedFilter = LibraryFilter.PackAvailable;

        // Act
        vm.Query = "bet";

        // Assert
        Names(vm).Should().Equal("Beta");
    }

    [Fact]
    public async Task Filter_without_matches_says_so()
    {
        // Arrange
        using var services = await BuildAsync();
        var vm = Create(services);

        // Act
        vm.SelectedFilter = LibraryFilter.Installed;

        // Assert
        (vm.HasNoResults, vm.NoResultsText).Should().Be((true, "No games match this filter. Game not in the library? Add its folder."));
    }

    [Fact]
    public async Task Search_and_filter_without_matches_name_both()
    {
        // Arrange
        using var services = await BuildAsync();
        var vm = Create(services);
        vm.SelectedFilter = LibraryFilter.Installed;

        // Act
        vm.Query = "zelda";

        // Assert
        vm.NoResultsText.Should().Be("No games match “zelda” with this filter. Game not in the library? Add its folder.");
    }

    [Fact]
    public async Task Reset_clears_the_search_and_the_filter()
    {
        // Arrange
        using var services = await BuildAsync();
        var vm = Create(services);
        vm.SelectedFilter = LibraryFilter.Installed;
        vm.Query = "zelda";

        // Act
        vm.ClearSearchCommand.Execute(null);

        // Assert
        (vm.Query, vm.SelectedFilter, vm.VisibleCards.Count).Should().Be((string.Empty, LibraryFilter.All, 3));
    }

    [Fact]
    public async Task Important_first_puts_pack_available_before_no_pack_and_blocked_last()
    {
        // Arrange
        _registry.Pack(30, "ru", "full").OptOut(10);
        using var services = await BuildAsync();
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });

        // Act
        var vm = Create(services);

        // Assert
        Names(vm).Should().Equal("Gamma", "Beta", "Alpha");
    }

    [Fact]
    public async Task Important_first_breaks_ties_by_last_played()
    {
        // Arrange
        using var services = await BuildAsync(s => s
            .AddGame(10, "Alpha", lastPlayed: 1_700_000_000)
            .AddGame(20, "Beta")
            .AddGame(30, "Gamma", lastPlayed: 1_760_000_000));

        // Act
        var vm = Create(services);

        // Assert
        Names(vm).Should().Equal("Gamma", "Alpha", "Beta");
    }

    [Fact]
    public async Task Opted_out_game_is_blocked_as_unavailable()
    {
        // Arrange
        _registry.OptOut(10);
        using var services = await BuildAsync();

        // Act
        var card = CardOf(Create(services), "Alpha");

        // Assert
        (card.IsBlocked, card.BadgeText, card.ContextLine).Should().Be((true, "Unavailable", "Studio opted out"));
    }

    [Fact]
    public async Task Pack_list_arriving_later_updates_the_cards()
    {
        // Arrange
        _registry.Pack(10, "ru", "full").Write();
        using var services = TestServices.Build(steam: s => s.AddGame(10, "Alpha"), registry: _registry.Path);
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
        var vm = Create(services);

        // Act
        await services.GetRequiredService<PackIndex>().RefreshAsync();

        // Assert
        CardOf(vm, "Alpha").StatusKind.Should().Be(LocalizationStatusKind.PackAvailable);
    }

    [Fact]
    public async Task Before_the_pack_list_arrives_cards_have_no_badge()
    {
        // Arrange
        _registry.Write();
        using var services = TestServices.Build(steam: s => s.AddGame(10, "Alpha"), registry: _registry.Path);

        // Act
        var vm = Create(services);

        // Assert
        (CardOf(vm, "Alpha").ShowBadge, vm.ShowRegistryBanner).Should().Be((false, false));
    }

    [Fact]
    public async Task Fresh_pack_list_shows_no_banner()
    {
        // Arrange
        using var services = await BuildAsync();

        // Act
        var vm = Create(services);

        // Assert
        (vm.ShowRegistryBanner, vm.IsRegistryStale).Should().Be((false, false));
    }

    [Fact]
    public async Task Offline_shows_the_saved_list_with_its_date_and_faded_badges()
    {
        // Arrange
        _registry.Pack(10, "ru", "full");
        _http.OnGet(IndexUrl, _registry.Json());
        using var services = BuildOverHttps();
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
        var index = services.GetRequiredService<PackIndex>();
        var fresh = await index.RefreshAsync();
        GoOffline();
        var vm = Create(services);

        // Act
        await index.RefreshAsync();

        // Assert
        var saved = fresh.FetchedAt!.Value.ToLocalTime().ToString("g", System.Globalization.CultureInfo.GetCultureInfo("en"));
        (vm.ShowRegistryBanner, vm.RegistryBannerText, CardOf(vm, "Alpha").StatusKind, CardOf(vm, "Alpha").IsStale).Should().Be(
            (true, $"Can't reach the pack registry. Showing data saved on {saved}.", LocalizationStatusKind.PackAvailable, true));
    }

    [Fact]
    public async Task Offline_without_a_saved_list_says_statuses_come_later()
    {
        // Arrange
        GoOffline();
        using var services = BuildOverHttps();
        var vm = Create(services);

        // Act
        await services.GetRequiredService<PackIndex>().RefreshAsync();

        // Assert
        (vm.RegistryBannerText, CardOf(vm, "Alpha").ShowBadge).Should().Be(
            ("Can't reach the pack registry. Pack statuses appear once it answers.", false));
    }

    [Fact]
    public async Task Newer_index_format_asks_to_update_the_app()
    {
        // Arrange
        _http.OnGet(IndexUrl, """{ "schemaVersion": 99, "generated": "2026-11-04T12:00:00Z", "games": {} }""");
        using var services = BuildOverHttps();
        var vm = Create(services);

        // Act
        await services.GetRequiredService<PackIndex>().RefreshAsync();

        // Assert
        vm.RegistryBannerText.Should().Be("The pack registry uses a newer format. Update the app to see the current packs.");
    }

    [Fact]
    public async Task Retry_fetches_again_and_hides_the_banner_when_back_online()
    {
        // Arrange
        GoOffline();
        using var services = BuildOverHttps();
        await services.GetRequiredService<PackIndex>().RefreshAsync();
        var vm = Create(services);
        _http.OnGet(IndexUrl, _registry.Json());

        // Act
        await vm.RetryRegistryCommand.ExecuteAsync(null);

        // Assert
        (vm.ShowRegistryBanner, CardOf(vm, "Alpha").StatusKind).Should().Be((false, LocalizationStatusKind.NoPack));
    }

    [Fact]
    public async Task Quick_action_opens_the_game_page()
    {
        // Arrange
        _registry.Pack(10, "ru", "full");
        using var services = await BuildAsync();
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
        var vm = Create(services);
        var navigation = services.GetRequiredService<INavigationService>();

        // Act: the pack has no manifest, so Install can't pick it and opens the page instead
        CardOf(vm, "Alpha").QuickActionCommand.Execute(null);

        // Assert
        await Wait.UntilAsync(() => navigation.Current is GamePageViewModel);
        navigation.Current.Should().BeOfType<GamePageViewModel>().Which.Game.Name.Should().Be("Alpha");
    }

    [Fact]
    public async Task Install_quick_action_opens_the_packs_tab_when_no_pack_can_be_installed()
    {
        // Arrange
        _registry.Pack(10, "ru", "full");
        using var services = await BuildAsync();
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
        var vm = Create(services);
        var navigation = services.GetRequiredService<INavigationService>();

        // Act
        CardOf(vm, "Alpha").QuickActionCommand.Execute(null);

        // Assert
        await Wait.UntilAsync(() => navigation.Current is GamePageViewModel);
        navigation.Current.Should().BeOfType<GamePageViewModel>().Which.SelectedTab.Should().Be(GamePageTab.Packs);
    }

    [Fact]
    public async Task Generate_quick_action_opens_the_generation_tab()
    {
        // Arrange
        _registry.Pack(10, "ru", "full");
        using var services = await BuildAsync();
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
        var vm = Create(services);
        var navigation = services.GetRequiredService<INavigationService>();

        // Act
        CardOf(vm, "Beta").QuickActionCommand.Execute(null);

        // Assert
        navigation.Current.Should().BeOfType<GamePageViewModel>().Which.SelectedTab.Should().Be(GamePageTab.Generation);
    }

    [Fact]
    public void Chips_are_hidden_without_games()
    {
        // Arrange
        using var services = TestServices.Build();
        services.GetRequiredService<Desktop.Library.GameLibrary>().Refresh();

        // Act
        var vm = Create(services);

        // Assert
        vm.ShowFilters.Should().BeFalse();
    }
}
