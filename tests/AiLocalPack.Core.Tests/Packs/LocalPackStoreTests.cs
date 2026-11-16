using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Packs;

/// <summary>Packs generated on this machine: registry layout, build report apart (task 51).</summary>
public sealed class LocalPackStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly GameId Game = GameId.Steam(620);

    private readonly TempDir _dir = new();
    private readonly LocalPackStore _store;

    public LocalPackStoreTests()
    {
        _store = new LocalPackStore(AppPaths.UnderHome(_dir.Combine("home")));
    }

    public void Dispose() => _dir.Dispose();

    private static PackManifest Manifest(string id = "claude-code") => new(
        id,
        Game,
        "ru",
        "Claude Code translation",
        [new PackAuthor(LocalPackStore.PlaceholderAuthor)],
        [new PackRevision(1, T0, LocalPackStore.PlaceholderAuthor)],
        PackStatus.Alpha,
        new PackAgent("claude-code", "2.1.319", "claude-opus-5-5"),
        [new PackBuild("1000", new Dictionary<string, string> { ["data/Items.json"] = new('a', 64) })],
        new PackRecipe("rpgmaker", "1.0.0"),
        null,
        null,
        [],
        ["noto-sans"],
        new PackCoverage(2, 3),
        T0,
        T0);

    private static StringTable Table => new("ru", new Dictionary<string, string> { ["a"] = "Зелье", ["b"] = "Эфир" });

    private static LocalPackReport Report(string job = "job-1") => new(
        job,
        T0,
        TestRunResult.Skipped(TestRunSkip.OtherPlatform, "Game.exe is a Windows program"),
        new LocalPackFonts(true, "noto-sans", ["Fixture Sans"], [0x416]),
        [new LocalPackWarning("b", "whitespace", "Leading or trailing whitespace differs.", " Ether", "Эфир")],
        [new LocalPackString("c", "Hello")],
        1,
        InGameLanguage: "en");

    [Fact]
    public void A_saved_pack_passes_the_registry_checks()
    {
        // Arrange
        var pack = _store.Save(Manifest(), Table, Report());

        // Act
        var validation = PackValidator.Validate(pack.Folder, TestContext.Current.CancellationToken);

        // Assert
        validation.Problems.Should().BeEmpty();
    }

    [Fact]
    public void A_saved_pack_sits_in_the_registry_layout()
    {
        // Act
        var pack = _store.Save(Manifest(), Table, Report());

        // Assert
        pack.Folder.Should().Be(Path.Combine(_dir.Combine("home"), "data", "local-packs", "games", "steam-620", "ru", "claude-code"));
    }

    [Fact]
    public void The_listed_pack_has_the_saved_report()
    {
        // Arrange
        var saved = _store.Save(Manifest(), Table, Report());

        // Act
        var listed = _store.List().Single();

        // Assert
        listed.Report.Should().BeEquivalentTo(saved.Report);
    }

    [Fact]
    public void The_report_counts_the_pack_size()
    {
        // Act
        var pack = _store.Save(Manifest(), Table, Report());

        // Assert
        pack.Report.SizeBytes.Should().Be(Directory.EnumerateFiles(pack.Folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length));
    }

    [Fact]
    public void A_taken_id_gets_a_number()
    {
        // Arrange
        _store.Save(Manifest("claude-code"), Table, Report("job-1"));
        _store.Save(Manifest("claude-code-2"), Table, Report("job-2"));

        // Act
        var id = _store.FreeId(Game, "ru", "claude-code");

        // Assert
        id.Should().Be("claude-code-3");
    }

    [Fact]
    public void A_free_id_is_kept()
    {
        // Arrange
        _store.Save(Manifest("claude-code"), Table, Report());

        // Act
        var id = _store.FreeId(Game, "de", "claude-code");

        // Assert
        id.Should().Be("claude-code");
    }

    [Fact]
    public void The_pack_of_a_job_is_found()
    {
        // Arrange
        _store.Save(Manifest("claude-code"), Table, Report("job-1"));
        _store.Save(Manifest("claude-code-2"), Table, Report("job-2"));

        // Act
        var pack = _store.FindByJob(Game, "job-2");

        // Assert
        pack!.Manifest.Id.Should().Be("claude-code-2");
    }

    [Fact]
    public void Saving_again_replaces_the_pack()
    {
        // Arrange
        _store.Save(Manifest(), Table, Report());
        var smaller = new StringTable("ru", new Dictionary<string, string> { ["a"] = "Зелье" });

        // Act
        _store.Save(Manifest(), smaller, Report());

        // Assert
        new { Count = _store.List().Count, Valid = PackValidator.Validate(_store.PackFolder(Game, "ru", "claude-code"), TestContext.Current.CancellationToken).IsValid }.Should().Be(new { Count = 1, Valid = true });
    }

    [Fact]
    public void A_deleted_pack_is_gone_with_its_report()
    {
        // Arrange
        _store.Save(Manifest(), Table, Report());

        // Act
        _store.Delete(Game, "ru", "claude-code");

        // Assert
        new { Folder = Directory.Exists(_store.PackFolder(Game, "ru", "claude-code")), Report = File.Exists(_store.ReportPath(Game, "ru", "claude-code")) }
            .Should().Be(new { Folder = false, Report = false });
    }

    [Fact]
    public void A_pack_with_a_broken_report_is_left_out()
    {
        // Arrange
        _store.Save(Manifest("claude-code"), Table, Report("job-1"));
        _store.Save(Manifest("codex"), Table, Report("job-2"));
        File.WriteAllText(_store.ReportPath(Game, "ru", "codex"), "{ not json");

        // Act
        var listed = _store.List();

        // Assert
        listed.Select(p => p.Manifest.Id).Should().Equal("claude-code");
    }

    [Fact]
    public void A_report_of_a_newer_format_is_left_out()
    {
        // Arrange
        _store.Save(Manifest(), Table, Report());
        var path = _store.ReportPath(Game, "ru", "claude-code");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal));

        // Act
        var listed = _store.List();

        // Assert
        listed.Should().BeEmpty();
    }

    [Fact]
    public void Nothing_is_listed_before_the_first_pack()
    {
        // Act
        var listed = _store.List();

        // Assert
        listed.Should().BeEmpty();
    }

    [Fact]
    public void Only_the_asked_games_packs_are_listed()
    {
        // Arrange
        _store.Save(Manifest(), Table, Report());
        _store.Save(Manifest() with { Game = GameId.Steam(700) }, Table, Report("job-2"));

        // Act
        var listed = _store.List(GameId.Steam(700));

        // Assert
        listed.Select(p => p.Report.Job).Should().Equal("job-2");
    }

    [Fact]
    public void Tried_mark_reads_back()
    {
        // Arrange
        _store.Save(Manifest(), Table, Report());
        _store.UpdateReport(Game, "ru", "claude-code", r => r with { Tried = T0.AddHours(1) });

        // Act
        var pack = _store.Find(Game, "ru", "claude-code");

        // Assert
        (pack!.Report.Tried, pack.Report.IsTried).Should().Be((T0.AddHours(1), true));
    }

    [Fact]
    public void Share_dismissal_reads_back()
    {
        // Arrange
        _store.Save(Manifest(), Table, Report());
        _store.UpdateReport(Game, "ru", "claude-code", r => r with { ShareDismissed = true });

        // Act
        var pack = _store.Find(Game, "ru", "claude-code");

        // Assert
        pack!.Report.ShareDismissed.Should().BeTrue();
    }

    [Fact]
    public void A_merged_pull_request_reads_back()
    {
        // Arrange
        var publication = new LocalPackPublication("ailocalpack/registry", "claude-code", 1, 7, new Uri("https://github.com/ailocalpack/registry/pull/7"), "octo/registry", "pack/steam-620/ru/claude-code/rev-1", T0, PullRequestState.Merged, T0.AddHours(2));
        _store.Save(Manifest(), Table, Report());
        _store.UpdateReport(Game, "ru", "claude-code", r => r with { Published = publication });

        // Act
        var pack = _store.Find(Game, "ru", "claude-code");

        // Assert
        pack!.Report.Published.Should().Be(publication);
    }

    [Fact]
    public void A_publication_without_a_state_reads_as_open()
    {
        // Arrange
        _store.Save(Manifest(), Table, Report());
        _store.UpdateReport(Game, "ru", "claude-code", r => r with { Published = new LocalPackPublication("ailocalpack/registry", "claude-code", 1, 7, new Uri("https://github.com/ailocalpack/registry/pull/7"), "octo/registry", "b", T0) });

        // Act
        var json = File.ReadAllText(_store.ReportPath(Game, "ru", "claude-code"));

        // Assert
        (json.Contains("\"state\"", StringComparison.Ordinal), _store.Find(Game, "ru", "claude-code")!.Report.Published!.State).Should().Be((false, PullRequestState.Open));
    }

    [Fact]
    public void Report_change_of_a_missing_pack_changes_nothing()
    {
        // Act
        var changed = _store.UpdateReport(Game, "ru", "claude-code", r => r with { ShareDismissed = true });

        // Assert
        (changed, File.Exists(_store.ReportPath(Game, "ru", "claude-code"))).Should().Be(((LocalPack?)null, false));
    }

    [Fact]
    public void New_pack_is_not_tried()
    {
        // Arrange
        _store.Save(Manifest(), Table, Report());

        // Act
        var pack = _store.Find(Game, "ru", "claude-code");

        // Assert
        (pack!.Report.IsTried, pack.Report.ShareDismissed).Should().Be((false, false));
    }

    [Fact]
    public void Find_refuses_a_name_that_is_no_pack_id()
    {
        // Arrange
        _store.Save(Manifest(), Table, Report());

        // Act
        var pack = _store.Find(Game, "ru", "../ru/claude-code");

        // Assert
        pack.Should().BeNull();
    }

    [Fact]
    public void The_draft_number_and_the_crash_round_trip_in_the_report()
    {
        // Arrange
        var crash = new LocalPackCrash(T0.AddHours(1), TimeSpan.FromSeconds(4.2), 3);
        _store.Save(Manifest(), Table, Report() with { Draft = 3, Crash = crash });

        // Act
        var report = _store.Find(Game, "ru", "claude-code")!.Report;

        // Assert
        (report.DraftNumber, report.Crash).Should().Be((3, crash));
    }

    [Fact]
    public void The_test_runs_loading_time_watch_and_screenshots_round_trip_in_the_report()
    {
        // Arrange
        var run = new TestRunResult(
            TestRunOutcome.Passed,
            Detail: "still running",
            Ran: TimeSpan.FromSeconds(58),
            Ready: TimeSpan.FromSeconds(24),
            Watch: TimeSpan.FromSeconds(58),
            Shots: [new TestRunShot(TestRunShotKind.Original, "/shots/original.png"), new TestRunShot(TestRunShotKind.Translated, "/shots/translated.png")]);
        _store.Save(Manifest(), Table, Report() with { TestRun = run });

        // Act
        var saved = _store.Find(Game, "ru", "claude-code")!.Report.TestRun;

        // Assert
        new { saved.Ready, saved.Watch, Shots = saved.Shots!.Select(s => (s.Kind, s.Path)).ToList() }.Should().BeEquivalentTo(new
        {
            Ready = (TimeSpan?)TimeSpan.FromSeconds(24),
            Watch = (TimeSpan?)TimeSpan.FromSeconds(58),
            Shots = new List<(TestRunShotKind, string)> { (TestRunShotKind.Original, "/shots/original.png"), (TestRunShotKind.Translated, "/shots/translated.png") },
        });
    }

    [Fact]
    public void The_players_answer_to_the_screenshot_round_trips_in_the_report()
    {
        // Arrange
        var confirmed = new LocalPackConfirmation(T0.AddHours(2), Visible: false);
        _store.Save(Manifest(), Table, Report() with { Confirmed = confirmed });

        // Act
        var report = _store.Find(Game, "ru", "claude-code")!.Report;

        // Assert
        report.Confirmed.Should().Be(confirmed);
    }

    [Fact]
    public void A_pack_without_a_draft_number_is_the_first_draft()
    {
        // Arrange
        _store.Save(Manifest(), Table, Report());

        // Act
        var report = _store.Find(Game, "ru", "claude-code")!.Report;

        // Assert
        (report.Draft, report.DraftNumber).Should().Be(((int?)null, 1));
    }

    [Fact]
    public void A_snapshot_restored_over_a_later_build_brings_the_pack_back()
    {
        // Arrange
        _store.Save(Manifest(), Table, Report());
        var snapshot = _dir.Combine("drafts/1");
        _store.Snapshot(Game, "ru", "claude-code", snapshot);
        _store.Save(Manifest(), new StringTable("ru", new Dictionary<string, string> { ["a"] = "Другое" }), Report() with { Draft = 2 });

        // Act
        _store.Restore(snapshot);

        // Assert
        var pack = _store.Find(Game, "ru", "claude-code")!;
        (pack.Report.DraftNumber, pack.Manifest.Coverage).Should().Be((1, new PackCoverage(2, 3)));
    }

    [Fact]
    public void Restoring_changes_the_report_as_asked()
    {
        // Arrange
        _store.Save(Manifest(), Table, Report());
        var snapshot = _dir.Combine("drafts/1");
        _store.Snapshot(Game, "ru", "claude-code", snapshot);

        // Act
        var pack = _store.Restore(snapshot, r => r with { Created = T0.AddDays(1) });

        // Assert
        pack!.Report.Created.Should().Be(T0.AddDays(1));
    }

    [Fact]
    public void Snapshot_of_a_missing_pack_says_so()
    {
        // Act
        var taken = _store.Snapshot(Game, "ru", "nothing", _dir.Combine("drafts/9"));

        // Assert
        taken.Should().BeFalse();
    }

    [Fact]
    public void An_unreadable_snapshot_restores_nothing()
    {
        // Arrange
        Directory.CreateDirectory(_dir.Combine("drafts/1"));

        // Act
        var pack = _store.Restore(_dir.Combine("drafts/1"));

        // Assert
        pack.Should().BeNull();
    }

    [Fact]
    public void The_report_of_a_snapshot_is_read_without_restoring_it()
    {
        // Arrange
        _store.Save(Manifest(), Table, Report() with { Draft = 2 });
        _store.Snapshot(Game, "ru", "claude-code", _dir.Combine("drafts/2"));

        // Act
        var report = LocalPackStore.SnapshotReportOf(_dir.Combine("drafts/2"));

        // Assert
        report!.DraftNumber.Should().Be(2);
    }
}
