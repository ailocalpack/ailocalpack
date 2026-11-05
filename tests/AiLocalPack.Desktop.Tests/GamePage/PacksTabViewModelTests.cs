using System.Security.Cryptography;
using System.Text;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Tests.Library;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.GamePage;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.GamePage;

/// <summary>The Packs tab (task 24) over the real service graph, a local registry with manifests and a Steam game with files.</summary>
public sealed class PacksTabViewModelTests : IDisposable
{
    private const string IndexUrl = "https://registry.test/index.json";

    /// <summary>The content of <c>data/a.json</c> in game 10 "Alpha".</summary>
    private const string AlphaData = "alpha data";

    private static readonly string OtherHash = new('b', 64);

    private readonly TestRegistry _registry = new();
    private readonly FakeHttpHandler _http = new();

    public void Dispose() => _registry.Dispose();

    private static string AlphaHash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(AlphaData)));

    /// <summary>
    /// Three Russian packs of game 10 (build 1000): «lanterns» verified, made for 1000 (compatible); «full» alpha, made
    /// for 900 with another <c>data/a.json</c> (probably compatible); «old» alpha, made for 800 with a file the game
    /// doesn't have (incompatible). Plus one German pack.
    /// </summary>
    private static void ThreePacks(TestRegistry r) => r
        .Pack(10, "ru", "lanterns", PackStatus.Verified, likes: 42, buildId: "1000", updated: Date(10, 1), translated: 9)
        .Manifest(10, "ru", "lanterns", "1000", new Dictionary<string, string> { ["data/a.json"] = AlphaHash }, revisions: 3)
        .Pack(10, "ru", "full", likes: 5, buildId: "900", updated: Date(10, 28), translated: 10)
        .Manifest(10, "ru", "full", "900", new Dictionary<string, string> { ["data/a.json"] = OtherHash })
        .Pack(10, "ru", "old", likes: 1, buildId: "800", updated: Date(3, 3), translated: 6)
        .Manifest(10, "ru", "old", "800", new Dictionary<string, string> { ["data/gone.json"] = OtherHash })
        .Pack(10, "de", "komplett", buildId: "1000")
        .Manifest(10, "de", "komplett", "1000", new Dictionary<string, string> { ["data/a.json"] = AlphaHash });

    private static DateTimeOffset Date(int month, int day) => new(2026, month, day, 12, 0, 0, TimeSpan.Zero);

    private static void Games(SteamFixture steam)
    {
        steam.AddGame(10, "Alpha").AddGame(20, "Beta");
        var data = Path.Combine(steam.Root, "steamapps", "common", "Alpha", "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "a.json"), AlphaData);
    }

    /// <summary>Steam games 10 "Alpha" (with <c>data/a.json</c>) and 20 "Beta", build 1000, target Russian, the local registry.</summary>
    private async Task<ServiceProvider> BuildAsync(Action<TestRegistry>? packs = null)
    {
        packs?.Invoke(_registry);
        _registry.Write();
        var services = TestServices.Build(steam: Games, registry: _registry.Path);
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
        await services.GetRequiredService<PackIndex>().RefreshAsync();
        return services;
    }

    /// <summary>The registry over https (fake): the index and manifests answer as the test routes them.</summary>
    private ServiceProvider BuildOverHttps()
    {
        var services = TestServices.Build(
            services =>
            {
                services.AddSingleton(sp => new RegistryIndexClient(
                    _http.CreateClient(),
                    sp.GetRequiredService<AppPaths>(),
                    options: new RegistryIndexClientOptions(TimeSpan.FromSeconds(5), Attempts: 1)));
                services.AddSingleton(_ => new PackManifestClient(_http.CreateClient()));
            },
            steam: Games,
            registry: IndexUrl);
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
        return services;
    }

    private void GoOffline() =>
        _http.OnAsync(IndexUrl, (_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("No such host is known.")));

    private static GamePageViewModel Open(ServiceProvider services, uint appId, GamePageTab tab = GamePageTab.Default) =>
        services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(
            services.GetRequiredService<GameLibrary>().Catalog.Games.Single(g => g.Id == GameId.Steam(appId)), tab);

    /// <summary>The Packs tab of game 10 once its manifests are fetched and checked.</summary>
    private static async Task<PacksTabViewModel> PacksAsync(ServiceProvider services, uint appId = 10)
    {
        var packs = Open(services, appId).Packs;
        await packs.WhenIdle;
        return packs;
    }

    private static async Task<PackRowViewModel> SelectAsync(PacksTabViewModel packs, string id)
    {
        packs.Selected = packs.Rows.Single(r => r.Pack.Id == id);
        await packs.WhenIdle;
        return packs.Selected;
    }

    private static IEnumerable<string> Ids(PacksTabViewModel packs) => packs.Rows.Select(r => r.Pack.Id);

    // ---------- List and sort ----------

    [Fact]
    public async Task List_shows_the_target_languages_packs_best_first()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);

        // Act
        var packs = await PacksAsync(services);

        // Assert
        Ids(packs).Should().Equal("lanterns", "full", "old");
    }

    [Fact]
    public async Task Newest_sort_puts_the_latest_update_first()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);
        var packs = await PacksAsync(services);

        // Act
        packs.SelectedSort = packs.SortOptions.Single(o => o.Value == PackSort.Newest);

        // Assert
        Ids(packs).Should().Equal("full", "lanterns", "old");
    }

    [Fact]
    public async Task Coverage_sort_puts_the_fullest_pack_first()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);
        var packs = await PacksAsync(services);

        // Act
        packs.SelectedSort = packs.SortOptions.Single(o => o.Value == PackSort.Coverage);

        // Assert
        Ids(packs).Should().Equal("full", "lanterns", "old");
    }

    [Fact]
    public async Task Sorting_keeps_the_selected_pack()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);
        var packs = await PacksAsync(services);
        await SelectAsync(packs, "old");

        // Act
        packs.SelectedSort = packs.SortOptions.Single(o => o.Value == PackSort.Newest);

        // Assert
        packs.Selected!.Pack.Id.Should().Be("old");
    }

    [Fact]
    public async Task First_pack_is_selected()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);

        // Act
        var packs = await PacksAsync(services);

        // Assert
        (packs.Selected!.Pack.Id, packs.Selected.IsSelected).Should().Be(("lanterns", true));
    }

    [Fact]
    public async Task Toolbar_counts_the_packs_of_the_language()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);

        // Act
        var packs = await PacksAsync(services);

        // Assert
        packs.CountText.Should().Be("Русский: 3 packs");
    }

    // ---------- Language ----------

    [Fact]
    public async Task Switching_language_lists_that_languages_packs()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);
        var packs = await PacksAsync(services);

        // Act
        packs.LanguageOptions.Single(l => l.Tag == "de").SelectCommand.Execute(null);

        // Assert
        (packs.Language, string.Join(",", Ids(packs))).Should().Be(("de", "komplett"));
    }

    [Fact]
    public async Task Switching_language_does_not_change_the_setting()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);
        var packs = await PacksAsync(services);

        // Act
        packs.LanguageOptions.Single(l => l.Tag == "de").SelectCommand.Execute(null);

        // Assert
        services.GetRequiredService<SettingsStore>().Current.GameLanguage.Should().Be("ru");
    }

    [Fact]
    public async Task Language_popover_lists_languages_with_packs_first_with_their_counts()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);

        // Act
        var packs = await PacksAsync(services);

        // Assert
        packs.LanguageOptions.Take(3).Select(l => (l.Tag, l.CountText)).Should().Equal(("ru", "3"), ("de", "1"), (packs.LanguageOptions[2].Tag, "—"));
    }

    [Fact]
    public async Task Language_popover_marks_the_shown_language()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);

        // Act
        var packs = await PacksAsync(services);

        // Assert
        packs.LanguageOptions.Where(l => l.IsCurrent).Select(l => l.Tag).Should().Equal("ru");
    }

    [Fact]
    public async Task Language_search_matches_the_english_name()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);
        var packs = await PacksAsync(services);

        // Act
        packs.LanguageQuery = "germ";

        // Assert
        packs.LanguageOptions.Select(l => l.Tag).Should().Equal("de");
    }

    [Fact]
    public async Task Language_search_without_a_match_says_so()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);
        var packs = await PacksAsync(services);

        // Act
        packs.LanguageQuery = "klingon";

        // Assert
        packs.HasNoLanguageMatch.Should().BeTrue();
    }

    [Fact]
    public async Task List_follows_the_translate_into_setting()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);
        var packs = await PacksAsync(services);

        // Act
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "de" });

        // Assert
        (packs.Language, string.Join(",", Ids(packs))).Should().Be(("de", "komplett"));
    }

    [Fact]
    public async Task Language_picked_on_the_tab_stays_when_the_setting_changes()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);
        var packs = await PacksAsync(services);
        packs.LanguageOptions.Single(l => l.Tag == "fr").SelectCommand.Execute(null);

        // Act
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "de" });

        // Assert
        packs.Language.Should().Be("fr");
    }

    // ---------- Empty and unknown ----------

    [Fact]
    public async Task Language_without_packs_shows_the_empty_state()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);
        var packs = await PacksAsync(services);

        // Act
        packs.LanguageOptions.Single(l => l.Tag == "fr").SelectCommand.Execute(null);

        // Assert
        (packs.ShowEmpty, packs.ShowList, packs.EmptyTitle).Should().Be((true, false, "No translation into Français yet"));
    }

    [Fact]
    public async Task Empty_state_lists_the_other_languages_with_packs()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);
        var packs = await PacksAsync(services);

        // Act
        packs.LanguageOptions.Single(l => l.Tag == "fr").SelectCommand.Execute(null);

        // Assert
        packs.OtherLanguages.Select(l => l.NameWithCount).Should().Equal("Русский (3)", "Deutsch (1)");
    }

    [Fact]
    public async Task Game_without_packs_shows_the_empty_state()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);

        // Act
        var packs = await PacksAsync(services, 20);

        // Assert
        (packs.ShowEmpty, packs.HasOtherLanguages).Should().Be((true, false));
    }

    [Fact]
    public async Task Empty_state_generate_opens_the_generation_tab()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);
        var page = Open(services, 20);

        // Act
        page.Packs.OpenGenerationCommand.Execute(null);

        // Assert
        page.SelectedTab.Should().Be(GamePageTab.Generation);
    }

    [Fact]
    public async Task Without_a_pack_list_the_tab_says_it_is_loading()
    {
        // Arrange
        using var services = TestServices.Build(steam: Games);

        // Act
        var packs = await PacksAsync(services);

        // Assert
        (packs.IsUnknown, packs.ShowEmpty, packs.ShowList).Should().Be((true, false, false));
    }

    // ---------- Lazy manifest and compatibility ----------

    [Fact]
    public async Task Manifests_are_not_fetched_while_another_tab_is_shown()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);

        // Act
        var page = Open(services, 10, GamePageTab.Generation);
        await page.Packs.WhenIdle;

        // Assert
        page.Packs.Rows.Select(r => r.State).Should().AllBeEquivalentTo(PackCheckState.NotChecked);
    }

    [Fact]
    public async Task Showing_the_tab_fetches_and_checks_every_pack()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);
        var page = Open(services, 10, GamePageTab.Generation);

        // Act
        page.SelectedTab = GamePageTab.Packs;
        await page.Packs.WhenIdle;

        // Assert
        page.Packs.Rows.Select(r => (r.Pack.Id, r.CompatText)).Should().Equal(
            ("lanterns", "compatible"), ("full", "probably compatible"), ("old", "incompatible"));
    }

    [Fact]
    public async Task Pack_made_for_the_players_build_is_compatible()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);

        // Act
        var row = (await PacksAsync(services)).Selected!;

        // Assert
        (row.CompatTone, row.CompatTitle, row.CompatExplanation).Should().Be(
            ("ok", "Compatible with your version", "Made for build 1,000, the one you have."));
    }

    [Fact]
    public async Task Pack_with_changed_files_is_probably_compatible_and_installable()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);
        var packs = await PacksAsync(services);

        // Act
        var row = await SelectAsync(packs, "full");

        // Assert
        (row.CompatTone, row.CompatExplanation, row.CanInstall).Should().Be((
            "warn",
            "Made for build 900, you have 1,000. Changed files: 1 of 1; strings the update added stay untranslated.",
            true));
    }

    [Fact]
    public async Task Incompatible_pack_disables_install_with_the_reason()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);
        var packs = await PacksAsync(services);

        // Act
        var row = await SelectAsync(packs, "old");

        // Assert
        (row.CanInstall, row.InstallCommand.CanExecute(null), row.InstallHint, row.ShowTranslateMissing).Should().Be((
            false, false, "Install is unavailable. You can translate the missing strings with AI instead.", true));
    }

    [Fact]
    public async Task Incompatible_pack_explains_the_missing_files()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);
        var packs = await PacksAsync(services);

        // Act
        var row = await SelectAsync(packs, "old");

        // Assert
        (row.CompatTitle, row.CompatExplanation, row.IsDimmed).Should().Be((
            "Incompatible with your version", "Files it changes are missing from the game folder (1), e.g. data/gone.json.", true));
    }

    [Fact]
    public async Task Translate_with_ai_opens_the_generation_tab()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);
        var page = Open(services, 10);
        await page.Packs.WhenIdle;
        var row = await SelectAsync(page.Packs, "old");

        // Act
        row.TranslateMissingCommand.Execute(null);

        // Assert
        page.SelectedTab.Should().Be(GamePageTab.Generation);
    }

    [Fact]
    public async Task Details_show_what_the_manifest_says()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);

        // Act
        var row = (await PacksAsync(services)).Selected!;

        // Assert
        (row.AuthorText, row.AgentText, row.CoverageText, row.ReportsText, row.RevisionCount, row.RevisionText).Should().Be(
            ("alice", "Claude Code", "90% of strings", "works for 0 · doesn't work for 0", "3", "rev 1"));
    }

    [Fact]
    public async Task Revision_history_lists_the_newest_first()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);
        var row = (await PacksAsync(services)).Selected!;

        // Act
        row.ToggleRevisionsCommand.Execute(null);

        // Assert
        (row.IsRevisionsOpen, string.Join(" | ", row.RevisionLines.Select(l => l[..5]))).Should().Be((true, "rev 3 | rev 2 | rev 1"));
    }

    [Fact]
    public async Task Compatible_pack_can_be_installed()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);

        // Act
        var row = (await PacksAsync(services)).Selected!;

        // Assert
        (row.CanInstall, row.InstallCommand.CanExecute(null), row.HasInstallHint).Should().Be((true, true, false));
    }

    [Fact]
    public async Task Install_asks_the_install_flow()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);
        var packs = await PacksAsync(services);
        RegistryPack? requested = null;
        packs.InstallRequested += (_, pack) => requested = pack;

        // Act
        packs.Selected!.InstallCommand.Execute(null);

        // Assert
        requested!.Id.Should().Be("lanterns");
    }

    [Fact]
    public async Task Pack_without_a_discussion_has_no_reviews_link()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);

        // Act
        var row = (await PacksAsync(services)).Selected!;

        // Assert
        row.HasDiscussion.Should().BeFalse();
    }

    [Fact]
    public async Task Reviews_link_opens_the_discussion()
    {
        // Arrange
        var discussion = new Uri("https://github.com/ailocalpack/registry/discussions/7");
        using var services = await BuildAsync(r => r
            .Pack(10, "ru", "lanterns", discussion: discussion)
            .Manifest(10, "ru", "lanterns", "1000", new Dictionary<string, string> { ["data/a.json"] = AlphaHash }));
        var row = (await PacksAsync(services)).Selected!;

        // Act
        row.OpenDiscussionCommand.Execute(null);

        // Assert
        services.GetRequiredService<FakeShell>().OpenedUrls.Should().Equal(discussion);
    }

    [Fact]
    public async Task Missing_manifest_shows_the_error_with_retry()
    {
        // Arrange
        using var services = await BuildAsync(r => r.Pack(10, "ru", "lanterns"));

        // Act
        var row = (await PacksAsync(services)).Selected!;

        // Assert
        (row.State, row.FailureText, row.CanRetry, row.CanInstall).Should().Be((
            PackCheckState.Failed,
            "The registry has no manifest for this pack. It may have been removed since the list was saved.",
            true,
            false));
    }

    [Fact]
    public async Task Retry_fetches_the_manifest_again()
    {
        // Arrange
        using var services = await BuildAsync(r => r.Pack(10, "ru", "lanterns"));
        var packs = await PacksAsync(services);
        _registry.Manifest(10, "ru", "lanterns", "1000", new Dictionary<string, string> { ["data/a.json"] = AlphaHash });

        // Act
        packs.Selected!.RetryCommand.Execute(null);
        await packs.WhenIdle;

        // Assert
        packs.Selected.CompatText.Should().Be("compatible");
    }

    [Fact]
    public async Task Damaged_manifest_offers_no_retry()
    {
        // Arrange
        using var services = await BuildAsync(r => r
            .Pack(10, "ru", "lanterns")
            .WriteFile("games/steam-10/ru/lanterns/manifest.json", """{ "schemaVersion": 1, "id": "lanterns" }"""));

        // Act
        var row = (await PacksAsync(services)).Selected!;

        // Assert
        (row.FailureText, row.CanRetry).Should().Be((
            "The pack's manifest in the registry is damaged, so the pack can't be checked or installed.", false));
    }

    [Fact]
    public async Task Newer_manifest_asks_to_update_the_app()
    {
        // Arrange
        using var services = await BuildAsync(r => r
            .Pack(10, "ru", "lanterns")
            .WriteFile("games/steam-10/ru/lanterns/manifest.json", """{ "schemaVersion": 2, "id": "lanterns" }"""));

        // Act
        var row = (await PacksAsync(services)).Selected!;

        // Assert
        row.FailureText.Should().Be("The pack is in a newer format. Update the app to install it.");
    }

    [Fact]
    public async Task Game_update_checks_the_packs_again()
    {
        // Arrange
        using var services = await BuildAsync(ThreePacks);
        var page = Open(services, 10);
        await page.Packs.WhenIdle;
        var catalog = services.GetRequiredService<GameLibrary>().Catalog;

        // Act
        catalog.Update(GameId.Steam(10), g => g with { Version = GameVersion.FromBuildId("900") });
        await page.Packs.WhenIdle;

        // Assert
        page.Packs.Rows.Single(r => r.Pack.Id == "full").CompatText.Should().Be("compatible");
    }

    [Fact]
    public async Task Blocked_game_fetches_no_manifests()
    {
        // Arrange
        using var services = await BuildAsync(r =>
        {
            ThreePacks(r);
            r.OptOut(10);
        });

        // Act
        var packs = await PacksAsync(services);

        // Assert
        packs.Rows.Select(r => r.State).Should().AllBeEquivalentTo(PackCheckState.NotChecked);
    }

    // ---------- Offline ----------

    [Fact]
    public async Task Saved_list_shows_the_offline_banner_with_its_date()
    {
        // Arrange
        _registry.Pack(10, "ru", "lanterns");
        _http.OnGet(IndexUrl, _registry.Json());
        using var services = BuildOverHttps();
        var index = services.GetRequiredService<PackIndex>();
        var fresh = await index.RefreshAsync();
        GoOffline();
        await index.RefreshAsync();

        // Act
        var packs = await PacksAsync(services);

        // Assert
        var saved = fresh.FetchedAt!.Value.ToLocalTime().ToString("g", System.Globalization.CultureInfo.GetCultureInfo("en"));
        (packs.IsStale, packs.OfflineText).Should().Be((
            true, $"Can't reach the registry. Showing the list saved on {saved}; installing is unavailable until you're back online."));
    }

    [Fact]
    public async Task Offline_disables_install_with_the_reason()
    {
        // Arrange
        _registry.Pack(10, "ru", "lanterns");
        _http.OnGet(IndexUrl, _registry.Json());
        _http.OnGet(
            "https://registry.test/games/steam-10/ru/lanterns/manifest.json",
            PackManifestSerializer.Write(TestRegistry.ManifestModel(10, "ru", "lanterns", "1000", new Dictionary<string, string> { ["data/a.json"] = AlphaHash })));
        using var services = BuildOverHttps();
        var index = services.GetRequiredService<PackIndex>();
        await index.RefreshAsync();
        GoOffline();
        await index.RefreshAsync();

        // Act
        var row = (await PacksAsync(services)).Selected!;

        // Assert
        (row.CompatText, row.CanInstall, row.InstallHint).Should().Be((
            "compatible", false, "Install becomes available once the registry is reachable."));
    }

    [Fact]
    public async Task Retry_on_the_banner_fetches_the_list_again()
    {
        // Arrange
        _registry.Pack(10, "ru", "lanterns");
        _http.OnGet(IndexUrl, _registry.Json());
        using var services = BuildOverHttps();
        var index = services.GetRequiredService<PackIndex>();
        await index.RefreshAsync();
        GoOffline();
        await index.RefreshAsync();
        var page = Open(services, 10);
        _http.OnGet(IndexUrl, _registry.Json());

        // Act
        await page.Packs.RetryRegistryCommand.ExecuteAsync(null);

        // Assert
        page.Packs.IsStale.Should().BeFalse();
    }

}
