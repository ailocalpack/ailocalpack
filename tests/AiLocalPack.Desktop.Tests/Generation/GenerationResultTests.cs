using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Tests.Install;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.GamePage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Desktop.Tests.Generation;

/// <summary>
/// The Generation tab's result (task 55, design §6d) over a real RPG Maker game: the local pack of the last completed
/// job, Install &amp; play, Discard, New translation, the share strip once the player tried the pack in game.
/// </summary>
public sealed class GenerationResultTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 20, 0, 0, TimeSpan.Zero);

    /// <summary>The bed with a fake clock and a fast session poll, so a game session can start and end in a test.</summary>
    private static Task<InstallBed> BedAsync(FakeClock clock) => InstallBed.CreateAsync(configure: services =>
    {
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(sp => new GameSessions(
            sp.GetRequiredService<GameLibrary>().Catalog,
            sp.GetRequiredService<IProcessList>(),
            sp.GetRequiredService<IProcessStarter>(),
            sp.GetRequiredService<OsEnvironment>(),
            clock,
            NullLogger<GameSessions>.Instance) { PollInterval = TimeSpan.FromMilliseconds(10) });
    });

    private static GamePageViewModel Page(InstallBed bed, GamePageTab tab = GamePageTab.Default) =>
        bed.Services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(bed.Game, tab);

    private static GenerationTabViewModel Tab(InstallBed bed) => Page(bed, GamePageTab.Generation).Generation;

    private static Task Until(Func<bool> condition) => Wait.UntilAsync(condition);

    /// <summary>Install &amp; play, then the game runs and closes again.</summary>
    private static async Task PlayAndCloseAsync(InstallBed bed, GenerationTabViewModel tab, FakeClock clock)
    {
        var processes = bed.Services.GetRequiredService<FakeProcesses>();
        await tab.Result!.PlayCommand.ExecuteAsync(null);
        await Until(() => processes.OpenedUrls.Count > 0);
        processes.Run(Path.Combine(PathNames.RealPath(bed.GameDir), "Game.exe"));
        var sessions = bed.Services.GetRequiredService<GameSessions>();
        await Until(() => sessions.Find(InstallBed.Alpha)?.Phase == GameSessionPhase.Running);
        processes.ExitAll();
        clock.Advance(TimeSpan.FromSeconds(11));
        await Until(() => sessions.Find(InstallBed.Alpha) is null);
    }

    [Fact]
    public async Task Completed_job_with_a_local_pack_shows_the_result()
    {
        // Arrange
        using var bed = await BedAsync(new FakeClock(Now));
        bed.LocalPack();

        // Act
        var tab = Tab(bed);

        // Assert
        (tab.ShowResult, tab.ShowForm, tab.ShowProgress, tab.Steps.All(s => s.IsDone)).Should().Be((true, false, false, true));
    }

    [Fact]
    public async Task No_result_without_the_job_folder()
    {
        // Arrange
        using var bed = await BedAsync(new FakeClock(Now));
        bed.LocalPack(withJob: false);

        // Act
        var tab = Tab(bed);

        // Assert
        (tab.ShowResult, tab.ShowForm).Should().Be((false, true));
    }

    [Fact]
    public async Task Result_tiles_describe_the_build_report()
    {
        // Arrange
        using var bed = await BedAsync(new FakeClock(Now));
        bed.LocalPack(untranslated: [new LocalPackString("menu.debug", "Debug")]);

        // Act
        var tiles = Tab(bed).Result!.Tiles;

        // Assert
        tiles.Select(t => (t.Value, t.Detail)).Should().Equal(
            (CoverageValue(bed), "1 string stays untranslated"),
            ("Passed", "The translated game ran 15 s without closing"),
            ("Noto Sans swapped in", "“Fixture Sans” lack letters of the translation"),
            ("No warnings", "Lengths, placeholders and tags are fine"));
    }

    [Fact]
    public async Task Skipped_test_run_says_why()
    {
        // Arrange
        using var bed = await BedAsync(new FakeClock(Now));
        bed.LocalPack(testRun: TestRunResult.Skipped(TestRunSkip.OtherPlatform, "Game.exe is a Windows program"));

        // Act
        var tile = Tab(bed).Result!.Tiles[1];

        // Assert
        (tile.Value, tile.Detail, tile.Tone).Should().Be(("Skipped", "The game is built for another system", ResultTone.Info));
    }

    [Fact]
    public async Task Validator_warnings_are_counted_by_rule_and_listed()
    {
        // Arrange
        using var bed = await BedAsync(new FakeClock(Now));
        bed.LocalPack(warnings:
        [
            new LocalPackWarning("a", "whitespace", "Spaces differ.", " A", "А"),
            new LocalPackWarning("b", "whitespace", "Spaces differ.", " B", "Б"),
            new LocalPackWarning("c", "line-breaks", "Line breaks differ.", "C\n", "В"),
        ]);
        var result = Tab(bed).Result!;

        // Act
        result.ToggleWarningsCommand.Execute(null);

        // Assert
        (result.Tiles[3].Value, result.Tiles[3].Detail, result.Warnings.Count, result.ShowWarnings)
            .Should().Be(("3 warnings", "spaces: 2 · line breaks: 1", 3, true));
    }

    [Fact]
    public async Task Untried_result_offers_install_and_play_and_discard()
    {
        // Arrange
        using var bed = await BedAsync(new FakeClock(Now));
        bed.LocalPack();

        // Act
        var result = Tab(bed).Result!;

        // Assert
        (result.PlayText, result.IsPlayPrimary, result.ShowDiscard, result.ShowInPacksAction, result.ShowShare)
            .Should().Be(("Install & play", true, true, false, false));
    }

    [Fact]
    public async Task Install_and_play_installs_the_pack_and_launches_the_game()
    {
        // Arrange
        using var bed = await BedAsync(new FakeClock(Now));
        bed.LocalPack();
        var tab = Tab(bed);

        // Act
        await tab.Result!.PlayCommand.ExecuteAsync(null);

        // Assert
        (bed.Read(InstallBed.SystemFile).Contains("RU ", StringComparison.Ordinal),
            bed.Services.GetRequiredService<FakeProcesses>().OpenedUrls.Select(u => u.ToString()).ToList(),
            tab.Result!.IsInstalled,
            tab.Result.ShowDiscard).Should().BeEquivalentTo((true, new List<string> { "steam://rungameid/10" }, true, false));
    }

    [Fact]
    public async Task Header_offers_install_and_play_for_the_result()
    {
        // Arrange
        using var bed = await BedAsync(new FakeClock(Now));
        bed.LocalPack();

        // Act
        var page = Page(bed);

        // Assert
        (page.SelectedTab, page.PrimaryText, page.PrimaryCaption).Should().Be((GamePageTab.Generation, "Install & play", "Your translation · 100% of strings · test passed"));
    }

    [Fact]
    public async Task Closing_the_game_after_playing_with_the_pack_marks_it_tried()
    {
        // Arrange
        var clock = new FakeClock(Now);
        using var bed = await BedAsync(clock);
        bed.LocalPack();
        var tab = Tab(bed);

        // Act
        await PlayAndCloseAsync(bed, tab, clock);

        // Assert
        await Until(() => tab.Result?.IsTried == true);
        bed.Services.GetRequiredService<LocalPackStore>().Find(InstallBed.Alpha, "ru", "claude-code")!.Report.Tried.Should().NotBeNull();
    }

    [Fact]
    public async Task Tried_result_offers_sharing_play_again_and_show_in_packs()
    {
        // Arrange
        var clock = new FakeClock(Now);
        using var bed = await BedAsync(clock);
        bed.LocalPack();
        var tab = Tab(bed);

        // Act
        await PlayAndCloseAsync(bed, tab, clock);

        // Assert
        await Until(() => tab.Result?.IsTried == true);
        (tab.Result!.ShowShare, tab.Result.PlayText, tab.Result.IsPlayPrimary, tab.Result.ShowInPacksAction, tab.Result.ShowDiscard)
            .Should().Be((true, "Play again", false, true, false));
    }

    [Fact]
    public async Task Not_now_hides_the_share_strip_for_good()
    {
        // Arrange
        using var bed = await BedAsync(new FakeClock(Now));
        bed.LocalPack(tried: Now);
        var tab = Tab(bed);

        // Act
        tab.Result!.NotNowCommand.Execute(null);

        // Assert
        await Until(() => Tab(bed).Result?.ShowShare == false);
        bed.Services.GetRequiredService<LocalPackStore>().Find(InstallBed.Alpha, "ru", "claude-code")!.Report.ShareDismissed.Should().BeTrue();
    }

    [Fact]
    public async Task Discard_deletes_the_pack_and_the_working_copy()
    {
        // Arrange
        using var bed = await BedAsync(new FakeClock(Now));
        var pack = bed.LocalPack();
        var tab = Tab(bed);
        var folder = bed.Services.GetRequiredService<GenerationService>().ResultOf(InstallBed.Alpha)!.Folder;

        // Act
        await tab.DiscardAsync(pack);

        // Assert
        (Directory.Exists(pack.Folder), Directory.Exists(folder), tab.ShowForm).Should().Be((false, false, true));
    }

    [Fact]
    public async Task Discard_asks_first()
    {
        // Arrange
        using var bed = await BedAsync(new FakeClock(Now));
        var pack = bed.LocalPack();
        var tab = Tab(bed);

        // Act
        tab.Result!.DiscardCommand.Execute(null);

        // Assert
        (bed.Services.GetRequiredService<Dialogs.IDialogService>().Current is Dialogs.ConfirmDialogViewModel, Directory.Exists(pack.Folder)).Should().Be((true, true));
    }

    [Fact]
    public async Task Discard_of_the_installed_pack_is_refused()
    {
        // Arrange
        using var bed = await BedAsync(new FakeClock(Now));
        var pack = bed.LocalPack();
        await bed.Services.GetRequiredService<Desktop.Install.PackInstallService>().InstallLocalAsync(bed.Game, pack, TestContext.Current.CancellationToken);
        var tab = Tab(bed);

        // Act
        await tab.DiscardAsync(pack);

        // Assert
        (Directory.Exists(pack.Folder), tab.ActionError).Should().Be((true, "The translation is installed: uninstall it first."));
    }

    [Fact]
    public async Task New_translation_keeps_the_pack_and_deletes_the_working_copy()
    {
        // Arrange
        using var bed = await BedAsync(new FakeClock(Now));
        var pack = bed.LocalPack();
        var tab = Tab(bed);
        var folder = bed.Services.GetRequiredService<GenerationService>().ResultOf(InstallBed.Alpha)!.Folder;

        // Act
        await tab.NewTranslationAsync();

        // Assert
        (Directory.Exists(pack.Folder), Directory.Exists(folder), tab.ShowForm).Should().Be((true, false, true));
    }

    [Fact]
    public async Task Show_in_packs_selects_the_pack_in_mine()
    {
        // Arrange
        using var bed = await BedAsync(new FakeClock(Now));
        bed.LocalPack(tried: Now);
        var page = Page(bed, GamePageTab.Generation);

        // Act
        page.Generation.Result!.ShowInPacksCommand.Execute(null);

        // Assert
        (page.SelectedTab, page.Packs.SelectedMine?.Pack?.Manifest.Id).Should().Be((GamePageTab.Packs, "claude-code"));
    }

    [Fact]
    public async Task Header_offers_play_once_the_result_is_installed_in_another_language()
    {
        // Arrange: the page's target language is English, the pack Russian.
        using var bed = await BedAsync(new FakeClock(Now));
        bed.Services.GetRequiredService<Core.Settings.SettingsStore>().Update(s => s with { GameLanguage = "en" });
        var pack = bed.LocalPack();
        await bed.Services.GetRequiredService<Desktop.Install.PackInstallService>().InstallLocalAsync(bed.Game, pack, TestContext.Current.CancellationToken);

        // Act
        var page = Page(bed);

        // Assert
        (page.PrimaryText, page.PrimaryCaption).Should().Be(("Play", "via Steam · with «Claude Code translation»"));
    }

    private static string CoverageValue(InstallBed bed)
    {
        var coverage = bed.Services.GetRequiredService<LocalPackStore>().Find(InstallBed.Alpha, "ru", "claude-code")!.Manifest.Coverage!;
        return $"{coverage.Translated:N0} / {coverage.Total:N0}";
    }
}
