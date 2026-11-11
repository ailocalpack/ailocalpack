using AiLocalPack.Core.Activity;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Activity;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Install;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Tests.Install;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.Tests.Updates;
using AiLocalPack.Desktop.Updates;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Activity;

/// <summary>The Activity panel (design §5, task 38): sections, rows, actions, badge.</summary>
public sealed class ActivityPanelViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 15, 0, 0, TimeSpan.Zero);

    private static ServiceProvider Services(FakeClock? clock = null) =>
        TestServices.Build(s => s.AddSingleton<TimeProvider>(clock ?? new FakeClock(Now)), steam => steam.AddGame(10, "Alpha"));

    private static ActivityPanelViewModel Panel(IServiceProvider services) => services.GetRequiredService<ActivityPanelViewModel>();

    private static ActivityStore Store(IServiceProvider services) => services.GetRequiredService<ActivityStore>();

    private static ActivityEvent Event(string id, ActivityEventKind kind = ActivityEventKind.PackInstalled, TimeSpan? ago = null) =>
        ActivityEvent.Create(kind, Now - (ago ?? TimeSpan.FromMinutes(5))) with
        {
            Id = id,
            Game = GameId.Steam(10),
            GameName = "Alpha",
            PackId = "lanterns",
            PackTitle = "Фонарщики",
            Revision = 2,
        };

    [Fact]
    public void Empty_store_shows_all_quiet()
    {
        // Arrange
        using var services = Services();

        // Act
        var panel = Panel(services);

        // Assert
        (panel.IsEmpty, panel.HasBadge, panel.BellName).Should().Be((true, false, "Activity"));
    }

    [Fact]
    public void Decision_comes_first_with_an_amber_badge()
    {
        // Arrange
        using var services = Services();
        var panel = Panel(services);
        Store(services).Add(Event("installed"));

        // Act
        Store(services).Add(Event("broken", ActivityEventKind.PackIncompatible));

        // Assert
        panel.Should().BeEquivalentTo(new
        {
            Decisions = new[] { new { Id = "broken", Title = "The game updated; «Фонарщики» rev 2 is incompatible now", IsAttention = true, CanDecideLater = true } },
            IsBadgeAttention = true,
            BadgeText = "1",
            BellName = "Activity: 1 needs your decision",
        });
    }

    [Fact]
    public void Badge_counts_unread_events_without_decisions()
    {
        // Arrange
        using var services = Services();
        var panel = Panel(services);

        // Act
        Store(services).Add(Event("a"));
        Store(services).Add(Event("b"));

        // Assert
        (panel.IsBadgeAttention, panel.BadgeText, panel.BellName).Should().Be((false, "2", "Activity: 2"));
    }

    [Fact]
    public void Feed_is_grouped_by_day_with_older_days_hidden()
    {
        // Arrange
        using var services = Services();
        var store = Store(services);
        store.Add(Event("today"));
        store.Add(Event("yesterday", ago: TimeSpan.FromDays(1)));
        store.Add(Event("week", ago: TimeSpan.FromDays(4)));

        // Act
        var panel = Panel(services);

        // Assert
        (panel.Days.Select(d => d.Heading).ToArray(), panel.Days[0].NewText, panel.HasOlder, panel.ShowMoreText)
            .Should().BeEquivalentTo((new[] { "Today", "Yesterday" }, "1 new", true, "Show 1 more"));
    }

    [Fact]
    public void Show_more_adds_the_older_days()
    {
        // Arrange
        using var services = Services();
        Store(services).Add(Event("week", ago: TimeSpan.FromDays(4)));
        var panel = Panel(services);

        // Act
        panel.ShowOlderCommand.Execute(null);

        // Assert
        (panel.Days.Single().Heading, panel.HasOlder).Should().Be(("Wednesday, November 4", false));
    }

    [Fact]
    public void Read_all_keeps_the_decisions()
    {
        // Arrange
        using var services = Services();
        Store(services).Add(Event("installed"));
        Store(services).Add(Event("broken", ActivityEventKind.PackIncompatible));
        var panel = Panel(services);

        // Act
        panel.ReadAllCommand.Execute(null);

        // Assert
        (panel.HasUnread, panel.Decisions.Count, panel.Days.Single().Rows.Single().IsUnread).Should().Be((false, 1, false));
    }

    [Fact]
    public void Decide_later_moves_the_card_into_the_feed()
    {
        // Arrange
        using var services = Services();
        Store(services).Add(Event("broken", ActivityEventKind.PackIncompatible));
        var panel = Panel(services);

        // Act
        panel.Decisions.Single().DecideLaterCommand!.Execute(null);

        // Assert
        (panel.Decisions.Count, panel.Days.Single().Rows.Single().Id, panel.HasBadge).Should().Be((0, "broken", false));
    }

    [Fact]
    public void Hide_removes_a_feed_row()
    {
        // Arrange
        using var services = Services();
        Store(services).Add(Event("installed"));
        var panel = Panel(services);

        // Act
        panel.Days.Single().Rows.Single().HideCommand!.Execute(null);

        // Assert
        (panel.Days.Count, panel.IsEmpty).Should().Be((0, true));
    }

    [Fact]
    public void Opening_a_row_marks_it_read_and_opens_the_game_page()
    {
        // Arrange
        using var services = Services();
        Store(services).Add(Event("installed"));
        var panel = Panel(services);
        var closed = false;
        panel.CloseRequested += (_, _) => closed = true;

        // Act
        panel.Days.Single().Rows.Single().OpenCommand.Execute(null);

        // Assert
        (services.GetRequiredService<INavigationService>().Current is GamePageViewModel, Store(services).Feed.Single().IsRead, closed)
            .Should().Be((true, true, true));
    }

    [Fact]
    public void Settings_button_opens_general_settings()
    {
        // Arrange
        using var services = Services();
        var panel = Panel(services);

        // Act
        panel.OpenSettingsCommand.Execute(null);

        // Assert
        services.GetRequiredService<INavigationService>().Current.Should().BeOfType<SettingsViewModel>()
            .Which.SelectedSection.Id.Should().Be(SettingsSectionId.General);
    }

    [Fact]
    public void Incompatible_pack_without_a_newer_revision_offers_translation()
    {
        // Arrange
        using var services = Services();
        Store(services).Add(Event("broken", ActivityEventKind.PackIncompatible));

        // Act
        var row = Panel(services).Decisions.Single();

        // Assert
        (row.Detail, row.Actions.Single().Text).Should().Be(("No newer revision yet: translate the new strings with AI.", "Translate new strings"));
    }

    [Fact]
    public void Decision_set_aside_keeps_only_its_log()
    {
        // Arrange
        using var services = Services();
        Store(services).Add(Event("broken", ActivityEventKind.AutoReapplyFailed) with { CanRetry = true, Detail = "In use." });
        var panel = Panel(services);

        // Act
        panel.Decisions.Single().DecideLaterCommand!.Execute(null);

        // Assert
        panel.Days.Single().Rows.Single().Actions.Select(a => a.Text).Should().Equal("Log");
    }

    [Fact]
    public void Running_install_shows_its_step_and_percent()
    {
        // Arrange
        using var services = Services();
        var panel = Panel(services);

        // Act
        Store(services).SetTask(new ActivityTask("t", ActivityTaskKind.Install, GameId.Steam(10), "Alpha", "Installing «Фонарщики» · step 2 of 6", "Backing up", 0.4, Now));

        // Assert
        panel.Running.Single().Should().BeEquivalentTo(new { Title = "Installing «Фонарщики» · step 2 of 6", Step = "Backing up", Percent = 40.0, Time = "40 %" });
    }

    [Fact]
    public void Ended_task_leaves_running()
    {
        // Arrange
        using var services = Services();
        var panel = Panel(services);
        Store(services).SetTask(new ActivityTask("t", ActivityTaskKind.Install, GameId.Steam(10), "Alpha", "Installing", null, 0.4, Now));

        // Act
        Store(services).EndTask("t");

        // Assert
        panel.Running.Should().BeEmpty();
    }

    [Fact]
    public async Task Failed_automatic_reapply_offers_retry_and_log()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        await InstallAndPutBackAsync(bed);
        bed.Services.GetRequiredService<FakeProcesses>().Run(Path.Combine(PathNames.RealPath(bed.GameDir), "Game.exe"));

        // Act
        await bed.Services.GetRequiredService<InstalledPackMonitor>().CheckAllAsync(TestContext.Current.CancellationToken);

        // Assert
        Panel(bed.Services).Decisions.Single().Should().BeEquivalentTo(new
        {
            Title = "The game updated; re-applying «lanterns» failed",
            Detail = "The game is running. Close it and retry.",
            IsBad = true,
            Actions = new[] { new { Text = "Retry" }, new { Text = "Log" } },
        });
    }

    [Fact]
    public async Task Log_shows_the_saved_failure()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        await InstallAndPutBackAsync(bed);
        bed.Services.GetRequiredService<FakeProcesses>().Run(Path.Combine(PathNames.RealPath(bed.GameDir), "Game.exe"));
        await bed.Services.GetRequiredService<InstalledPackMonitor>().CheckAllAsync(TestContext.Current.CancellationToken);

        // Act
        Panel(bed.Services).Decisions.Single().Actions.Single(a => a.Text == "Log").Command.Execute(null);

        // Assert
        bed.Services.GetRequiredService<IDialogService>().Current.Should().BeOfType<InstallDetailsDialogViewModel>().Which.Should().BeEquivalentTo(new
        {
            Title = "Couldn't re-apply «lanterns»",
            Reason = "The game is running. Close it and retry.",
            HasLog = true,
            CanRetry = true,
        });
    }

    [Fact]
    public async Task Retry_after_the_game_closed_settles_the_decision()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        await InstallAndPutBackAsync(bed);
        var processes = bed.Services.GetRequiredService<FakeProcesses>();
        processes.Run(Path.Combine(PathNames.RealPath(bed.GameDir), "Game.exe"));
        await bed.Services.GetRequiredService<InstalledPackMonitor>().CheckAllAsync(TestContext.Current.CancellationToken);
        processes.ExitAll();
        var panel = Panel(bed.Services);

        // Act
        panel.Decisions.Single().Actions.Single(a => a.Text == "Retry").Command.Execute(null);
        await Wait.UntilAsync(() => panel.Days.SelectMany(d => d.Rows).Count() == 2);

        // Assert
        (panel.Decisions.Count, panel.Days.Single().Rows[0].Title).Should().Be((0, "Re-applied «lanterns»"));
    }

    /// <summary>Installs "lanterns" and puts the original file back, as a Steam file check does.</summary>
    private static async Task InstallAndPutBackAsync(InstallBed bed)
    {
        var path = Path.Combine(bed.GameDir, InstallBed.SystemFile);
        var original = File.ReadAllBytes(path);
        var pack = bed.Services.GetRequiredService<PackIndex>().Current!.Index!.PacksFor(InstallBed.Alpha, "ru").Single(p => p.Id == "lanterns");
        var outcome = await bed.Services.GetRequiredService<PackInstallService>().InstallAsync(bed.Game, pack, TestContext.Current.CancellationToken);
        outcome.Succeeded.Should().BeTrue();
        _ = Panel(bed.Services);
        File.WriteAllBytes(path, original);
    }

    /// <summary>The app updater found and downloaded 0.5.0 (which adds its feed event).</summary>
    private static async Task<AppUpdater> UpdateReadyAsync(IServiceProvider services)
    {
        services.GetRequiredService<FakeUpdateSource>().Offer = "0.5.0";
        var updater = services.GetRequiredService<AppUpdater>();
        await updater.CheckAsync(TestContext.Current.CancellationToken);
        await updater.Download;
        return updater;
    }

    [Fact]
    public async Task App_update_shows_in_the_feed_with_restart_now()
    {
        // Arrange
        using var services = Services();
        var panel = Panel(services);

        // Act
        await UpdateReadyAsync(services);

        // Assert
        var row = panel.Days.Single().Rows.Single();
        (row.Title, row.Detail, row.GameName, row.IsOk, row.Actions.Single().Text)
            .Should().Be(("AI Local Pack 0.5.0 is ready to install", "Downloaded in the background. Restart takes a few seconds.", "AI Local Pack", true, "Restart now"));
    }

    [Fact]
    public async Task Restart_now_restarts_into_the_update()
    {
        // Arrange
        using var services = Services();
        var panel = Panel(services);
        await UpdateReadyAsync(services);

        // Act
        panel.Days.Single().Rows.Single().Actions.Single().Command.Execute(null);

        // Assert
        (services.GetRequiredService<FakeUpdateSource>().Applied?.Version, services.GetRequiredService<FakeAppQuit>().Quits).Should().Be(("0.5.0", 1));
    }

    [Fact]
    public async Task Restart_now_during_an_install_says_why_in_a_toast()
    {
        // Arrange
        using var services = Services();
        var panel = Panel(services);
        await UpdateReadyAsync(services);
        Store(services).SetTask(new ActivityTask("i", ActivityTaskKind.Install, GameId.Steam(10), "Alpha", "Installing", null, null, Now));

        // Act
        panel.Days.Single().Rows.Single().Actions.Single().Command.Execute(null);

        // Assert
        (services.GetRequiredService<ToastService>().Items.Single().Text, services.GetRequiredService<FakeAppQuit>().Quits)
            .Should().Be(("A translation is being installed. Restart when it finishes.", 0));
    }

    [Fact]
    public void App_update_already_applied_has_no_restart()
    {
        // Arrange
        using var services = Services();
        var panel = Panel(services);

        // Act
        Store(services).Add(ActivityEvent.Create(ActivityEventKind.AppUpdateReady, Now) with { Id = "old", Version = "0.3.0" });

        // Assert
        panel.Days.Single().Rows.Single().Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task App_update_row_opens_settings_about()
    {
        // Arrange
        using var services = Services();
        var panel = Panel(services);
        await UpdateReadyAsync(services);

        // Act
        panel.Days.Single().Rows.Single().OpenCommand.Execute(null);

        // Assert
        services.GetRequiredService<INavigationService>().Current.Should().BeOfType<SettingsViewModel>()
            .Which.SelectedSection.Id.Should().Be(SettingsSectionId.About);
    }
}
