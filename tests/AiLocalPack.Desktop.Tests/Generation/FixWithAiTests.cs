using AiLocalPack.Core.Activity;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Activity;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Tests.Install;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.GamePage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Desktop.Tests.Generation;

/// <summary>
/// Fix with AI (task 75, design §6e) over a real RPG Maker game: a crash right after the start marks the installed
/// draft and offers a fix (Activity, header, prefilled request), and the conversation under the result sends requests
/// and shows the agent's steps, answers, new drafts and plans.
/// </summary>
public sealed class FixWithAiTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 20, 0, 0, TimeSpan.Zero);

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

    private static GamePageViewModel Page(InstallBed bed) =>
        bed.Services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(bed.Game, GamePageTab.Generation);

    private static Task Until(Func<bool> condition) => Wait.UntilAsync(condition);

    private static FakeFixRunners Fixes(InstallBed bed) => bed.Services.GetRequiredService<FakeFixRunners>();

    /// <summary>Install &amp; play, then the game closes at once (a crash).</summary>
    private static async Task CrashAsync(InstallBed bed, GenerationTabViewModel tab, FakeClock clock)
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

    private static async Task<(InstallBed Bed, GamePageViewModel Page, FakeClock Clock)> CrashedAsync()
    {
        var clock = new FakeClock(Now);
        var bed = await BedAsync(clock);
        bed.LocalPack();
        var page = Page(bed);
        await CrashAsync(bed, page.Generation, clock);
        await Until(() => page.Generation.Fix?.ShowCrashBanner == true);

        // The crash's request comes prefilled with the session once the conversation reads the crash again.
        await Until(() => page.Generation.Fix?.Attachments.Any(a => a.Attachment.Kind == Core.Generation.FixAttachmentKind.Session) == true);
        return (bed, page, clock);
    }

    [Fact]
    public async Task A_game_that_closes_right_after_it_started_marks_the_installed_draft_crashed()
    {
        // Act
        var (bed, _, _) = await CrashedAsync();
        using var _bed = bed;

        // Assert
        var report = bed.Services.GetRequiredService<LocalPackStore>().Find(InstallBed.Alpha, "ru", "claude-code")!.Report;
        (report.Crash is not null, report.IsTried).Should().Be((true, false));
    }

    [Fact]
    public async Task A_crash_is_a_decision_in_activity()
    {
        // Act
        var (bed, _, _) = await CrashedAsync();
        using var _bed = bed;

        // Assert
        await Until(() => bed.Services.GetRequiredService<ActivityStore>().Decisions.Any(e => e.Kind == ActivityEventKind.TranslationCrashed));
    }

    [Fact]
    public async Task After_a_crash_the_header_offers_a_fix_with_ai()
    {
        // Act
        var (bed, page, _) = await CrashedAsync();
        using var _bed = bed;

        // Assert
        await Until(() => page.Action.Kind == GamePrimaryActionKind.FixWithAi);
    }

    [Fact]
    public async Task After_a_crash_the_request_is_prefilled_with_the_crash_and_its_attachments()
    {
        // Act
        var (bed, page, _) = await CrashedAsync();
        using var _bed = bed;

        // Assert
        var fix = page.Generation.Fix!;
        (fix.Text, string.Join(",", fix.Attachments.Select(a => a.Attachment.Kind)))
            .Should().Be(("The game crashes right after the first loading screen.", "Session,DraftChanges"));
    }

    [Fact]
    public async Task Not_a_crash_counts_the_session_as_played_so_the_draft_can_be_published()
    {
        // Arrange
        var (bed, page, _) = await CrashedAsync();
        using var _bed = bed;
        var line = page.Generation.Fix!.Items.OfType<FixSystemItem>().Single(i => i.HasNotCrash);

        // Act
        line.NotCrashCommand!.Execute(null);

        // Assert
        var report = bed.Services.GetRequiredService<LocalPackStore>().Find(InstallBed.Alpha, "ru", "claude-code")!.Report;
        (report.Crash is null, report.IsTried).Should().Be((true, true));
    }

    [Fact]
    public async Task Not_a_crash_settles_the_crash_decision()
    {
        // Arrange
        var (bed, page, _) = await CrashedAsync();
        using var _bed = bed;
        var activity = bed.Services.GetRequiredService<ActivityStore>();
        await Until(() => activity.Decisions.Any(e => e.Kind == ActivityEventKind.TranslationCrashed));

        // Act
        page.Generation.Fix!.NotCrashCommand.Execute(null);

        // Assert
        await Until(() => activity.Decisions.All(e => e.Kind != ActivityEventKind.TranslationCrashed));
    }

    [Fact]
    public async Task Not_a_crash_clears_the_untouched_prefilled_request_and_shows_the_draft_played()
    {
        // Arrange
        var (bed, page, _) = await CrashedAsync();
        using var _bed = bed;
        var fix = page.Generation.Fix!;

        // Act
        fix.NotCrashCommand.Execute(null);

        // Assert
        await Until(() => fix.Items.OfType<FixSystemItem>().Any(i => i.Text == "You played Draft 1"));
        (fix.Text, fix.Attachments.Count, fix.ShowCrashBanner).Should().Be((string.Empty, 0, false));
    }

    [Fact]
    public async Task Sending_a_request_shows_it_and_the_agents_answer()
    {
        // Arrange
        var (bed, page, _) = await CrashedAsync();
        using var _bed = bed;
        var fix = page.Generation.Fix!;

        // Act
        await fix.SendCommand.ExecuteAsync(null);

        // Assert
        await Until(() => fix.Items.OfType<FixAgentItem>().Any(a => a.Text == "Done."));
        (fix.Items.OfType<FixPlayerItem>().Single().Text, fix.Text, Fixes(bed).Requests.Single().Attachments.Count)
            .Should().Be(("The game crashes right after the first loading screen.", string.Empty, 2));
    }

    [Fact]
    public async Task Sending_a_request_settles_the_crash_decision()
    {
        // Arrange
        var (bed, page, _) = await CrashedAsync();
        using var _bed = bed;
        var activity = bed.Services.GetRequiredService<ActivityStore>();
        await Until(() => activity.Decisions.Any(e => e.Kind == ActivityEventKind.TranslationCrashed));

        // Act
        await page.Generation.Fix!.SendCommand.ExecuteAsync(null);

        // Assert
        await Until(() => activity.Decisions.All(e => e.Kind != ActivityEventKind.TranslationCrashed));
    }

    [Fact]
    public async Task While_the_agent_works_the_thread_shows_its_steps_and_stop()
    {
        // Arrange
        var (bed, page, _) = await CrashedAsync();
        using var _bed = bed;
        Fixes(bed).Hold = true;
        var fix = page.Generation.Fix!;

        // Act
        _ = fix.SendCommand.ExecuteAsync(null);

        // Assert
        await Until(() => fix.IsRunning && fix.Items.OfType<FixAgentItem>().Any(a => a.IsWorking && a.Steps.Any(s => s.Text == "Looked through the strings")));
        (fix.StopCommand.CanExecute(null), fix.ShowSend).Should().Be((true, false));
        fix.StopCommand.Execute(null);
        await Until(() => !fix.IsRunning);
    }

    [Fact]
    public async Task A_new_draft_shows_its_change_card_with_install_and_play()
    {
        // Arrange
        var (bed, page, clock) = await CrashedAsync();
        using var _bed = bed;
        var store = bed.Services.GetRequiredService<LocalPackStore>();
        Fixes(bed).Answer = (id, _) =>
        {
            store.UpdateReport(InstallBed.Alpha, "ru", "claude-code", r => r with { Draft = 2, Created = clock.GetUtcNow().AddMinutes(1), Crash = null });
            return new FixTurn(id, FixTurnKind.Agent, clock.GetUtcNow(), "Fixed the separators.", [], ["put_translations", "app:build"], [], FixTurnOutcome.Changed,
                Change: new FixChange(2, 3, 0, false, TestRunOutcome.Passed, [new FixStringChange("a", "Fire | Ice", "Огонь | Лёд", "Огонь и лёд")]));
        };
        var fix = page.Generation.Fix!;

        // Act
        await fix.SendCommand.ExecuteAsync(null);

        // Assert
        await Until(() => fix.Items.OfType<FixAgentItem>().Any(a => a.Change is not null));
        var card = fix.Items.OfType<FixAgentItem>().Single(a => a.Change is not null).Change!;
        (card.Title, card.InstallText, card.CanInstall, card.Samples.Single().After).Should().Be(("Draft 2 is ready", "Install & play Draft 2", true, "Огонь и лёд"));
    }

    [Fact]
    public async Task A_plan_waits_with_apply_and_change_the_request()
    {
        // Arrange
        var (bed, page, clock) = await CrashedAsync();
        using var _bed = bed;
        Fixes(bed).Answer = (id, _) => new FixTurn(id, FixTurnKind.Agent, clock.GetUtcNow(), "That touches 60 strings.", [], [], [], FixTurnOutcome.Planned,
            Plan: new FixPlan(60, 2, false, []));
        var fix = page.Generation.Fix!;
        await fix.SendCommand.ExecuteAsync(null);
        await Until(() => fix.Items.OfType<FixAgentItem>().Any(a => a.Plan is not null));
        var plan = fix.Items.OfType<FixAgentItem>().Single(a => a.Plan is not null).Plan!;

        // Act
        plan.ApplyCommand.Execute(null);

        // Assert
        await Until(() => Fixes(bed).Applied.Count == 1);
        (plan.IsWaiting, plan.Lines[0], plan.Lines[1]).Should().Be((true, "Translations to change: 60", "Glossary entries to change: 2"));
    }

    [Fact]
    public async Task The_result_card_folds_once_the_game_was_played()
    {
        // Act
        var (bed, page, _) = await CrashedAsync();
        using var _bed = bed;

        // Assert
        await Until(() => page.Generation.IsResultFolded && page.Generation.Result?.ShowDetails == false);
    }

    [Fact]
    public async Task A_rebuilt_draft_of_the_installed_pack_installs_without_asking()
    {
        // Arrange
        var (bed, page, clock) = await CrashedAsync();
        using var _bed = bed;
        var store = bed.Services.GetRequiredService<LocalPackStore>();
        var draft = store.UpdateReport(InstallBed.Alpha, "ru", "claude-code", r => r with { Draft = 2, Created = clock.GetUtcNow().AddMinutes(1), Crash = null })!;
        clock.Advance(TimeSpan.FromMinutes(2));

        // Act
        await bed.Services.GetRequiredService<Desktop.Install.InstallFlow>().InstallLocalAsync(bed.Game, draft, play: false);

        // Assert
        await Until(() => bed.Services.GetRequiredService<LocalPacks>().IsInstalled(store.Find(InstallBed.Alpha, "ru", "claude-code")!));
        bed.Services.GetRequiredService<IDialogService>().Current.Should().BeNull();
    }

    [Fact]
    public async Task An_uninstall_rebuilds_the_conversation_when_it_starts_and_ends_not_at_every_file()
    {
        // Arrange
        using var bed = await BedAsync(new FakeClock(Now));
        var service = bed.Services.GetRequiredService<Desktop.Install.PackInstallService>();
        await service.InstallLocalAsync(bed.Game, bed.LocalPack(), TestContext.Current.CancellationToken);
        var fix = Page(bed).Generation.Fix!;
        var reports = 0;
        service.Changed += (_, _) => Interlocked.Increment(ref reports);
        var rebuilds = 0;
        fix.Items.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                Interlocked.Increment(ref rebuilds);
            }
        };

        // Act
        await service.UninstallAsync(bed.Game, TestContext.Current.CancellationToken);

        // Assert
        (reports > 3, rebuilds).Should().Be((true, 3), "the start, the record removed and the end; not the files restored");
    }

    [Fact]
    public void The_crash_decision_names_the_translation_and_how_long_the_game_ran()
    {
        // Arrange
        var activity = ActivityEvent.Create(ActivityEventKind.TranslationCrashed, Now) with { PackTitle = "Claude Code translation", Detail = "4" };
        var localizer = Localizer.Current;

        // Act
        var (title, detail) = (ActivityTexts.Title(activity, localizer), ActivityTexts.Detail(activity, null, localizer));

        // Assert
        (title, detail).Should().Be(("The game closed right after it started", "With «Claude Code translation» installed, after 4 s. Ask the agent to fix it."));
    }

    [Fact]
    public async Task A_request_the_agent_cant_take_stays_in_the_composer_with_the_reason()
    {
        // Arrange
        var (bed, page, _) = await CrashedAsync();
        using var _bed = bed;
        Fixes(bed).Refuse = new InvalidOperationException("The agent \"fake\" is unknown to this version.");
        var fix = page.Generation.Fix!;

        // Act
        await fix.SendCommand.ExecuteAsync(null);

        // Assert
        await Until(() => fix.HasProblem);
        (fix.Text, fix.Attachments.Count, fix.ProblemText)
            .Should().Be(("The game crashes right after the first loading screen.", 2, "The agent can't be started: The agent \"fake\" is unknown to this version."));
    }
}
