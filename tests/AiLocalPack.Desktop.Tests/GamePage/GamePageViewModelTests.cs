using AiLocalPack.Core.Games;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Tests.Library;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.GamePage;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.GamePage;

/// <summary>The game page's header and tabs (task 22) over the real service graph with a local registry.</summary>
public sealed class GamePageViewModelTests : IDisposable
{
    private readonly TestRegistry _registry = new();

    public void Dispose() => _registry.Dispose();

    /// <summary>
    /// Steam games 10 "Alpha", 20 "Beta" and 30 "Shooter" (Easy Anti-Cheat folder), build 1000, target language Russian,
    /// the registry as the test wrote it.
    /// </summary>
    private async Task<ServiceProvider> BuildAsync(Action<TestRegistry>? packs = null)
    {
        packs?.Invoke(_registry);
        _registry.Write();
        var services = TestServices.Build(steam: s => TestGames.AddShooter(s.AddGame(10, "Alpha").AddGame(20, "Beta")), registry: _registry.Path);
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
        await services.GetRequiredService<PackIndex>().RefreshAsync();
        return services;
    }

    private static Game GameOf(ServiceProvider services, uint appId) =>
        services.GetRequiredService<GameLibrary>().Catalog.Games.Single(g => g.Id == GameId.Steam(appId));

    private static GamePageViewModel Open(ServiceProvider services, uint appId, GamePageTab tab = GamePageTab.Default) =>
        services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(GameOf(services, appId), tab);

    [Fact]
    public async Task Game_with_a_pack_offers_installing_it()
    {
        // Arrange
        using var services = await BuildAsync(r => r.Pack(10, "ru", "lanterns", Core.Packs.PackStatus.Verified));

        // Act
        var page = Open(services, 10);

        // Assert
        (page.PrimaryText, page.PrimaryCaption).Should().Be(("Install translation", "«lanterns» · verified · made for your version"));
    }

    [Fact]
    public async Task Game_without_a_pack_has_no_header_action()
    {
        // Arrange
        using var services = await BuildAsync(r => r.Pack(10, "ru", "lanterns"));

        // Act
        var page = Open(services, 20);

        // Assert
        (page.Action.Kind, page.HasPrimaryAction).Should().Be((GamePrimaryActionKind.Generate, false));
    }

    [Fact]
    public async Task Blocked_game_has_no_primary_action()
    {
        // Arrange
        using var services = await BuildAsync(r => r.Pack(10, "ru", "lanterns").OptOut(10));

        // Act
        var page = Open(services, 10);

        // Assert
        (page.HasPrimaryAction, page.Action.Kind).Should().Be((false, GamePrimaryActionKind.None));
    }

    [Fact]
    public async Task Blocked_game_shows_no_packs_tab()
    {
        // Arrange
        using var services = await BuildAsync(r => r.OptOut(10));

        // Act
        var page = Open(services, 10);

        // Assert
        page.ShowTabs.Should().BeFalse();
    }

    [Fact]
    public async Task Game_without_a_pack_shows_the_empty_packs_tab()
    {
        // Arrange
        using var services = await BuildAsync();

        // Act
        var page = Open(services, 20);

        // Assert
        page.Packs.ShowEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task Page_opens_on_packs_by_default()
    {
        // Arrange
        using var services = await BuildAsync();

        // Act
        var page = Open(services, 10);

        // Assert
        page.Tabs.Where(t => t.IsSelected).Select(t => t.Tab).Should().Equal(GamePageTab.Packs);
    }

    [Fact]
    public async Task Page_opens_on_the_tab_it_is_asked_for()
    {
        // Arrange
        using var services = await BuildAsync();

        // Act
        var page = Open(services, 10, GamePageTab.Generation);

        // Assert
        (page.SelectedTab, page.IsGenerationTab).Should().Be((GamePageTab.Generation, true));
    }

    [Fact]
    public async Task Packs_tab_counts_the_packs_for_the_language()
    {
        // Arrange
        using var services = await BuildAsync(r => r.Pack(10, "ru", "a").Pack(10, "ru", "b").Pack(10, "de", "c"));

        // Act
        var page = Open(services, 10);

        // Assert
        page.Tabs.Select(t => (t.Tab, t.Counter)).Should().Equal(
            (GamePageTab.Packs, "2"), (GamePageTab.Audio, ""), (GamePageTab.Textures, ""), (GamePageTab.Generation, ""));
    }

    [Fact]
    public async Task Tab_bar_groups_localization_apart_from_the_ai_tool()
    {
        // Arrange
        using var services = await BuildAsync();

        // Act
        var page = Open(services, 10);

        // Assert
        (page.LocalizationTabs.Select(t => t.Label), page.ToolTabs.Select(t => t.Label)).Should().BeEquivalentTo(
            (new[] { "Text", "Voice", "Textures" }, new[] { "Create with AI" }), o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task Only_audio_and_textures_are_coming_soon()
    {
        // Arrange
        using var services = await BuildAsync();

        // Act
        var page = Open(services, 10);

        // Assert
        page.Tabs.Where(t => t.IsSoon).Select(t => t.Tab).Should().Equal(GamePageTab.Audio, GamePageTab.Textures);
    }

    [Fact]
    public async Task Coming_soon_tab_opens_its_illustration()
    {
        // Arrange
        using var services = await BuildAsync();
        var page = Open(services, 10);

        // Act
        page.Tabs.Single(t => t.Tab == GamePageTab.Textures).SelectCommand.Execute(null);

        // Assert
        (page.IsSoonTab, page.SoonTitle, page.SoonIcon).Should().Be((true, "Textures: coming soon", "IconImage"));
    }

    [Fact]
    public async Task Install_opens_the_packs_tab_when_no_pack_can_be_installed()
    {
        // Arrange: the pack's manifest is missing, so nothing can be checked
        using var services = await BuildAsync(r => r.Pack(10, "ru", "lanterns"));
        var page = Open(services, 10, GamePageTab.Audio);

        // Act
        page.PrimaryCommand.Execute(null);

        // Assert
        await Wait.UntilAsync(() => page.SelectedTab == GamePageTab.Packs);
        page.SelectedTab.Should().Be(GamePageTab.Packs);
    }

    [Fact]
    public async Task Generate_opens_the_generation_tab()
    {
        // Arrange
        using var services = await BuildAsync();
        var page = Open(services, 20);

        // Act
        page.PrimaryCommand.Execute(null);

        // Assert
        page.SelectedTab.Should().Be(GamePageTab.Generation);
    }

    [Fact]
    public async Task Chips_show_engine_store_and_grouped_build()
    {
        // Arrange
        using var services = await BuildAsync();
        var catalog = services.GetRequiredService<GameLibrary>().Catalog;
        catalog.Update(GameId.Steam(10), g => g with { Engine = "unity:mono", Version = GameVersion.FromBuildId("14882031") });

        // Act
        var page = Open(services, 10);

        // Assert
        (page.EngineText, page.StoreText, page.VersionText).Should().Be(("Unity (Mono)", "Steam", "build 14,882,031"));
    }

    [Theory]
    [InlineData("godot:4", "BrandGodot")]
    [InlineData("renpy", "BrandRenPy")]
    [InlineData("rpgmaker:mz", null)]
    public async Task Engine_chip_shows_the_engine_mark_when_there_is_one(string engine, string? brandKey)
    {
        // Arrange
        using var services = await BuildAsync();
        var catalog = services.GetRequiredService<GameLibrary>().Catalog;
        catalog.Update(GameId.Steam(10), g => g with { Engine = engine });

        // Act
        var page = Open(services, 10);

        // Assert
        page.EngineBrandKey.Should().Be(brandKey);
    }

    private static Game ManualWithFiles() => TestGames.Manual("Wren") with
    {
        Version = GameVersion.FromFileHashes([new("Game.exe", new string('b', 64))]),
    };

    [Fact]
    public async Task Manual_game_shows_a_short_hash_of_its_files()
    {
        // Arrange
        using var services = await BuildAsync();

        // Act
        var page = services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(ManualWithFiles());

        // Assert
        page.VersionText.Should().MatchRegex("^files hash [0-9a-f]{8}$");
    }

    [Fact]
    public async Task Manual_game_chips_say_it_was_added_by_hand()
    {
        // Arrange
        using var services = await BuildAsync();

        // Act
        var page = services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(ManualWithFiles());

        // Assert
        (page.StoreText, page.VersionTip).Should().Be(("Added manually", "Game version: hash of 1 file"));
    }

    [Fact]
    public async Task Renamed_game_shows_its_new_name()
    {
        // Arrange
        using var services = await BuildAsync();
        var page = Open(services, 10);

        // Act
        services.GetRequiredService<GameLibrary>().Catalog.Update(GameId.Steam(10), g => g with { Name = "Alpha Remastered" });

        // Assert
        (page.Name, page.Title).Should().Be(("Alpha Remastered", "Alpha Remastered"));
    }

    [Fact]
    public async Task Closed_page_stops_following_the_catalog()
    {
        // Arrange
        using var services = await BuildAsync();
        var page = Open(services, 10);
        page.OnClosed();

        // Act
        services.GetRequiredService<GameLibrary>().Catalog.Update(GameId.Steam(10), g => g with { Name = "Alpha Remastered" });

        // Assert
        page.Name.Should().Be("Alpha");
    }

    [Fact]
    public async Task New_pack_list_updates_the_action()
    {
        // Arrange
        using var services = await BuildAsync();
        var page = Open(services, 10);
        _registry.Pack(10, "ru", "lanterns").Write();

        // Act
        await services.GetRequiredService<PackIndex>().RefreshAsync();

        // Assert
        page.Action.Kind.Should().Be(GamePrimaryActionKind.InstallBestPack);
    }

    [Fact]
    public async Task Changing_the_target_language_updates_the_action()
    {
        // Arrange
        using var services = await BuildAsync(r => r.Pack(10, "de", "laternen"));
        var page = Open(services, 10);

        // Act
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "de" });

        // Assert
        page.Action.Kind.Should().Be(GamePrimaryActionKind.InstallBestPack);
    }

    [Fact]
    public async Task Pack_list_not_loaded_yet_offers_nothing()
    {
        // Arrange
        using var services = TestServices.Build(steam: s => s.AddGame(10, "Alpha"));

        // Act
        var page = Open(services, 10);

        // Assert
        (page.IsStatusUnknown, page.HasPrimaryAction).Should().Be((true, false));
    }

    [Fact]
    public async Task Anti_cheat_game_explains_the_block_instead_of_the_tabs()
    {
        // Arrange
        using var services = await BuildAsync();

        // Act
        var page = Open(services, 30);

        // Assert
        (page.IsBlocked, page.ShowTabs, page.BlockedText).Should().Be((
            true,
            false,
            "Easy Anti-Cheat was detected. Modifying game files can get your account banned, so AI Local Pack doesn't install or create translations for games with anti-cheat."));
    }

    [Fact]
    public async Task Opted_out_game_says_the_publisher_asked()
    {
        // Arrange
        using var services = await BuildAsync(r => r.OptOut(10));

        // Act
        var page = Open(services, 10);

        // Assert
        page.BlockedText.Should().StartWith("The publisher asked not to distribute fan translations for this game");
    }

    [Fact]
    public async Task Registry_whitelist_lifts_the_block()
    {
        // Arrange
        using var services = await BuildAsync(r => r.AllowAntiCheat(30));

        // Act
        var page = Open(services, 30);

        // Assert
        (page.IsBlocked, page.ShowTabs, page.Action.Kind).Should().Be((false, true, GamePrimaryActionKind.Generate));
    }

    [Fact]
    public async Task Blocked_game_never_shows_the_sticky_bar()
    {
        // Arrange
        using var services = await BuildAsync();
        var page = Open(services, 30);

        // Act
        page.IsTabBarStuck = true;

        // Assert
        page.ShowStickyBar.Should().BeFalse();
    }

    [Fact]
    public async Task Opt_out_arriving_with_a_new_pack_list_blocks_the_open_page()
    {
        // Arrange
        using var services = await BuildAsync();
        var page = Open(services, 10);
        _registry.OptOut(10).Write();

        // Act
        await services.GetRequiredService<PackIndex>().RefreshAsync();

        // Assert
        (page.IsBlocked, page.ShowTabs).Should().Be((true, false));
    }

    [Fact]
    public async Task Anti_cheat_the_registry_lists_blocks_the_game()
    {
        // Arrange
        using var services = await BuildAsync(r => r.AntiCheat(20, "battleye"));

        // Act
        var page = Open(services, 20);

        // Assert
        (page.IsBlocked, page.Status.AntiCheatName).Should().Be((true, "BattlEye"));
    }

    [Fact]
    public async Task Registry_whitelist_lifts_the_detected_anti_cheat()
    {
        // Arrange
        using var services = await BuildAsync(r => r.AllowAntiCheat(30));

        // Act
        var page = Open(services, 30);

        // Assert
        page.IsBlocked.Should().BeFalse();
    }

    [Fact]
    public async Task New_registry_marker_blocks_a_game_checked_before()
    {
        // Arrange
        using var services = await BuildAsync();
        var library = services.GetRequiredService<GameLibrary>();
        File.WriteAllText(Path.Combine(GameOf(services, 20).InstallPath, "AcmeGuard.sys"), "x");
        _registry.AntiCheatRules.Add(new Core.AntiCheat.AntiCheatRule("acme-guard", "Acme Guard", ["AcmeGuard.sys"]));
        _registry.Write();
        await services.GetRequiredService<PackIndex>().RefreshAsync();

        // Act
        await library.RefreshAsync();

        // Assert
        (GameOf(services, 20).AntiCheat, Open(services, 20).IsBlocked).Should().Be(("acme-guard", true));
    }

    [Fact]
    public async Task Report_opens_the_registry_issue_with_the_detected_file()
    {
        // Arrange
        using var services = await BuildAsync();
        var page = Open(services, 30);

        // Act
        await page.ReportCommand.ExecuteAsync(null);

        // Assert
        var body = Uri.UnescapeDataString(services.GetRequiredService<FakeShell>().OpenedUrls.Single().Query);
        body.Should().Contain("title=Block report: Shooter (steam:30)").And.Contain("**Detected file:** `EasyAntiCheat/`");
    }

    [Fact]
    public async Task Report_of_an_opted_out_game_names_the_flag()
    {
        // Arrange
        using var services = await BuildAsync(r => r.OptOut(10));
        var page = Open(services, 10);

        // Act
        await page.ReportCommand.ExecuteAsync(null);

        // Assert
        Uri.UnescapeDataString(services.GetRequiredService<FakeShell>().OpenedUrls.Single().Query)
            .Should().Contain("**Blocked because:** the registry's opt-out flag");
    }

    [Fact]
    public async Task Report_of_a_game_that_is_not_blocked_opens_nothing()
    {
        // Arrange
        using var services = await BuildAsync();
        var page = Open(services, 10);

        // Act
        await page.ReportCommand.ExecuteAsync(null);

        // Assert
        services.GetRequiredService<FakeShell>().OpenedUrls.Should().BeEmpty();
    }
}
