using AiLocalPack.Agents.Processes;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>The test run: the untranslated game measured first, then the translation watched; outcomes, the rebuilt files laid over the copy and taken off again.</summary>
public sealed class GameTestRunnerTests : IDisposable
{
    private static readonly TestRunOptions Short = new()
    {
        MinWatch = TimeSpan.FromMilliseconds(100),
        MaxWatch = TimeSpan.FromMilliseconds(150),
        MaxReady = TimeSpan.FromMilliseconds(120),
        Sample = TimeSpan.FromMilliseconds(10),
        QuietSpan = TimeSpan.FromMilliseconds(20),
        MinReady = TimeSpan.Zero,
        NoPictureAfter = TimeSpan.FromMilliseconds(50),
        ShotEvery = TimeSpan.FromMilliseconds(10),
        Margin = TimeSpan.Zero,
        Factor = 1,
    };

    /// <summary>
    /// <see cref="Short"/> for a game that must read as loaded: it still watches <see cref="TestRunOptions.MinWatch"/>
    /// once loaded, but a busy machine that delays the samples gets seconds, not 150 ms, to see it load.
    /// </summary>
    private static readonly TestRunOptions Loading = Short with { MaxWatch = TimeSpan.FromSeconds(5), MaxReady = TimeSpan.FromSeconds(5) };

    private static readonly Game Manual = new(
        GameId.Fingerprint(new string('a', 64)), "Lantern", GameSource.Manual, "/games/lantern", null, null, null, "none", null, true);

    private static readonly Game Steam = Manual with { Id = GameId.Steam(620), Source = GameSource.Steam };

    private static readonly string[] BuildFiles = ["data/a.json", "fonts/new.ttf"];

    private readonly TempDir _dir = new();

    public GameTestRunnerTests()
    {
        _dir.File("job/game/Game.sh", "#!/bin/sh\nexec ./game\n")
            .File("job/game/data/a.json", "original")
            .File("job/build/data/a.json", "translated")
            .File("job/build/fonts/new.ttf", "font");
    }

    public void Dispose() => _dir.Dispose();

    private string Job => _dir.Combine("job");

    private string Copy => _dir.Combine("job/game");

    private string CopyFile(string relative) => Path.Combine(Copy, relative.Replace('/', Path.DirectorySeparatorChar));

    private Task<TestRunResult> RunAsync(
        FakeGameProcesses processes,
        Game? game = null,
        OsKind os = OsKind.Linux,
        TestRunOptions? options = null,
        IGameProbe? probe = null,
        TestRunBaselines? baselines = null,
        TimeSpan? atLeast = null,
        bool quick = false) =>
        new GameTestRunner(processes, os, options ?? Short, probe, baselines).RunAsync(
            new TestRunRequest(game ?? Manual, Copy, _dir.Combine("job/build"), BuildFiles, Job, _ => { }) { AtLeast = atLeast, ShotsFolder = _dir.Combine("shots"), Quick = quick },
            TestContext.Current.CancellationToken);

    private static byte[] SteamStubPe()
    {
        var data = new byte[0x80 + 24 + 0xF0 + 40];
        data[0] = (byte)'M';
        data[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(data, 0x3C);
        "PE\0\0"u8.ToArray().CopyTo(data, 0x80);
        BitConverter.GetBytes((ushort)1).CopyTo(data, 0x80 + 6);
        BitConverter.GetBytes((ushort)0xF0).CopyTo(data, 0x80 + 20);
        ".bind"u8.ToArray().CopyTo(data, 0x80 + 24 + 0xF0);
        return data;
    }

    [Fact]
    public async Task A_game_still_running_when_the_watch_ends_passes()
    {
        // Arrange
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning, FakeGame.KeepsRunning);

        // Act
        var result = await RunAsync(processes);

        // Assert
        new { result.Outcome, Starts = processes.Started.Count }.Should().Be(new { Outcome = TestRunOutcome.Passed, Starts = 3 });
    }

    [Fact]
    public async Task The_untranslated_copy_runs_first_then_the_translation_twice()
    {
        // Arrange
        var seen = new List<string>();
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning, FakeGame.KeepsRunning) { OnStart = _ => seen.Add(File.ReadAllText(CopyFile("data/a.json"))) };

        // Act
        await RunAsync(processes);

        // Assert
        seen.Should().Equal("original", "translated", "translated");
    }

    [Fact]
    public async Task A_crash_on_the_second_launch_fails_the_test_run()
    {
        // Arrange
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning, FakeGame.KeepsRunning, FakeGame.Closes(-1073740791));

        // Act
        var result = await RunAsync(processes);

        // Assert
        new { result.Outcome, SecondLaunch = result.Detail!.StartsWith("on its second launch", StringComparison.Ordinal) }
            .Should().Be(new { Outcome = TestRunOutcome.Crashed, SecondLaunch = true });
    }

    [Fact]
    public async Task A_crash_on_the_first_launch_doesnt_start_the_game_again()
    {
        // Arrange
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning, FakeGame.Closes(3));

        // Act
        await RunAsync(processes);

        // Assert
        processes.Started.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_second_launch_sees_what_the_first_one_wrote()
    {
        // Arrange
        var launches = 0;
        var seen = new List<bool>();
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning, FakeGame.KeepsRunning, FakeGame.KeepsRunning)
        {
            OnStart = _ =>
            {
                seen.Add(File.Exists(CopyFile("cache.json")));
                if (++launches == 2)
                {
                    File.WriteAllText(CopyFile("cache.json"), "compiled");
                }
            },
        };

        // Act
        await RunAsync(processes);

        // Assert
        seen.Should().Equal(false, false, true);
    }

    [Fact]
    public async Task A_file_the_game_adds_to_the_copy_is_deleted_afterwards()
    {
        // Arrange
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning, FakeGame.KeepsRunning)
        {
            OnStart = _ => _dir.File("job/game/saves/slot1.sav", "save"),
        };

        // Act
        await RunAsync(processes);

        // Assert
        new { Save = File.Exists(CopyFile("saves/slot1.sav")), Folder = Directory.Exists(CopyFile("saves")) }
            .Should().Be(new { Save = false, Folder = false });
    }

    [Fact]
    public async Task A_copy_file_the_game_rewrites_is_taken_back_from_the_game_folder()
    {
        // Arrange
        var game = Manual with { InstallPath = _dir.Combine("installed") };
        _dir.File("installed/books/compiled.json", "english").File("job/game/books/compiled.json", "english");
        var written = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_dir.Combine("installed/books/compiled.json"), written);
        File.SetLastWriteTimeUtc(CopyFile("books/compiled.json"), written);
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning, FakeGame.KeepsRunning)
        {
            OnStart = _ => File.WriteAllText(CopyFile("books/compiled.json"), "русский, длиннее"),
        };

        // Act
        await RunAsync(processes, game: game);

        // Assert
        File.ReadAllText(CopyFile("books/compiled.json")).Should().Be("english");
    }

    [Fact]
    public async Task A_small_file_the_game_rewrites_is_put_back()
    {
        // Arrange
        _dir.File("job/game/settings.ini", "as shipped");
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning, FakeGame.KeepsRunning)
        {
            OnStart = _ => File.WriteAllText(CopyFile("settings.ini"), "written by the game"),
        };

        // Act
        await RunAsync(processes);

        // Assert
        File.ReadAllText(CopyFile("settings.ini")).Should().Be("as shipped");
    }

    [Fact]
    public async Task A_large_file_the_game_rewrites_without_a_backup_is_left_and_logged()
    {
        // Arrange
        _dir.File("job/game/world.dat", new byte[GameWriteGuard.MaxSmallFile + 1]);
        var log = new List<string>();
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning, FakeGame.KeepsRunning)
        {
            OnStart = _ => File.WriteAllText(CopyFile("world.dat"), "written by the game"),
        };

        // Act
        await new GameTestRunner(processes, OsKind.Linux, Short).RunAsync(
            new TestRunRequest(Manual, Copy, _dir.Combine("job/build"), BuildFiles, Job, log.Add) { ShotsFolder = _dir.Combine("shots") },
            TestContext.Current.CancellationToken);

        // Assert
        log.Should().Contain(line => line.Contains("had no backup", StringComparison.Ordinal) && line.Contains("world.dat", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_file_the_game_is_known_to_write_is_saved_first_and_taken_back_though_the_game_folder_changed()
    {
        // Arrange
        var game = Manual with { InstallPath = _dir.Combine("installed") };
        _dir.File("installed/books/compiled.json", "the player's own session").File("job/game/books/compiled.json", "english");
        var baselines = new TestRunBaselines(_dir.Combine("baselines"));
        baselines.AddWrites(game.Id, ["books/compiled.json"]);
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning, FakeGame.KeepsRunning, FakeGame.KeepsRunning)
        {
            OnStart = _ => File.WriteAllText(CopyFile("books/compiled.json"), "русский, длиннее"),
        };

        // Act
        await RunAsync(processes, game: game, baselines: baselines);

        // Assert
        File.ReadAllText(CopyFile("books/compiled.json")).Should().Be("english");
    }

    [Fact]
    public async Task A_file_taken_back_from_the_game_folder_is_saved_for_the_runs_after_the_game_folder_changes()
    {
        // Arrange
        var game = Manual with { InstallPath = _dir.Combine("installed") };
        _dir.File("installed/books/compiled.json", "english").File("job/game/books/compiled.json", "english");
        var written = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_dir.Combine("installed/books/compiled.json"), written);
        File.SetLastWriteTimeUtc(CopyFile("books/compiled.json"), written);
        Action<ProcessSpec> rewrite = _ => File.WriteAllText(CopyFile("books/compiled.json"), "русский, длиннее");
        await RunAsync(new FakeGameProcesses(FakeGame.KeepsRunning, FakeGame.KeepsRunning, FakeGame.KeepsRunning) { OnStart = rewrite }, game: game);
        File.WriteAllText(_dir.Combine("installed/books/compiled.json"), "the player's own session");
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning, FakeGame.KeepsRunning, FakeGame.KeepsRunning) { OnStart = rewrite };

        // Act
        await RunAsync(processes, game: game);

        // Assert
        File.ReadAllText(CopyFile("books/compiled.json")).Should().Be("english");
    }

    [Fact]
    public async Task The_copy_files_a_game_rewrites_are_remembered_for_its_next_runs()
    {
        // Arrange
        var baselines = new TestRunBaselines(_dir.Combine("baselines"));
        _dir.File("job/game/config/config.json", "{}");
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning, FakeGame.KeepsRunning, FakeGame.KeepsRunning)
        {
            OnStart = _ =>
            {
                File.WriteAllText(CopyFile("config/config.json"), "{\"seen\":true}");
                File.WriteAllText(CopyFile("log.txt"), "a new log");
            },
        };

        // Act
        await RunAsync(processes, baselines: baselines);

        // Assert
        baselines.Writes(Manual.Id).Should().Equal("config/config.json");
    }

    [Fact]
    public async Task Closing_early_while_the_untranslated_copy_keeps_running_is_a_crash()
    {
        // Arrange
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning, FakeGame.Closes(3, "TypeError: font"));

        // Act
        var result = await RunAsync(processes);

        // Assert
        new { result.Outcome, result.ExitCode, NamesError = result.Detail!.Contains("TypeError: font", StringComparison.Ordinal) }
            .Should().Be(new { Outcome = TestRunOutcome.Crashed, ExitCode = (int?)3, NamesError = true });
    }

    [Fact]
    public async Task A_game_that_closes_untranslated_is_skipped_without_trying_the_translation()
    {
        // Arrange
        var processes = new FakeGameProcesses(FakeGame.Closes(1));

        // Act
        var result = await RunAsync(processes);

        // Assert
        new { result.Outcome, result.Skip, Starts = processes.Started.Count }
            .Should().Be(new { Outcome = TestRunOutcome.Skipped, Skip = (TestRunSkip?)TestRunSkip.ClosesUntranslated, Starts = 1 });
    }

    [Fact]
    public async Task After_the_run_the_copy_is_as_before()
    {
        // Arrange
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning, FakeGame.Closes(3));

        // Act
        await RunAsync(processes);

        // Assert
        new
        {
            Data = File.ReadAllText(CopyFile("data/a.json")),
            AddedFont = File.Exists(CopyFile("fonts/new.ttf")),
            Overlay = Directory.Exists(Path.Combine(Job, "test-run")),
        }.Should().Be(new { Data = "original", AddedFont = false, Overlay = false });
    }

    [Fact]
    public async Task The_games_error_output_goes_to_the_test_run_log()
    {
        // Arrange
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning, FakeGame.Closes(3, "TypeError: font"));

        // Act
        await RunAsync(processes);

        // Assert
        (await File.ReadAllTextAsync(Path.Combine(Job, "logs", "test-run.log"), TestContext.Current.CancellationToken)).Should().Contain("[err] TypeError: font");
    }

    [Fact]
    public async Task A_measured_untranslated_copy_isnt_run_again()
    {
        // Arrange
        var baselines = new TestRunBaselines(_dir.Combine("baselines"));
        await RunAsync(new FakeGameProcesses(), baselines: baselines);
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning);

        // Act
        await RunAsync(processes, baselines: baselines);

        // Assert
        processes.Started.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_crash_names_where_its_output_parted_from_the_untranslated_games()
    {
        // Arrange
        var processes = new FakeGameProcesses(
            FakeGame.Prints("[10-00-01] read items.json", "[10-00-02] read follower_wheel.json", "[10-00-03] read monsters.json"),
            FakeGame.Closes(-1073740791) with { Output = ["[11-30-01] read items.json", "[11-30-02] read follower_wheel.json"] });

        // Act
        var result = await RunAsync(processes);

        // Assert
        new { result.OutputDiff!.LastShared, Next = result.OutputDiff.OriginalNext.Single() }
            .Should().Be(new { LastShared = (string?)"[11-30-02] read follower_wheel.json", Next = "[10-00-03] read monsters.json" });
    }

    [Fact]
    public async Task A_crash_keeps_its_last_lines_of_standard_output()
    {
        // Arrange
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning, FakeGame.Closes(3) with { Output = ["loading", "read follower_wheel.json"] });

        // Act
        var result = await RunAsync(processes);

        // Assert
        result.LastOutput.Should().Equal("loading", "read follower_wheel.json");
    }

    [Fact]
    public async Task The_untranslated_games_output_is_kept_with_its_baseline()
    {
        // Arrange
        var baselines = new TestRunBaselines(_dir.Combine("baselines"));

        // Act
        await RunAsync(new FakeGameProcesses(FakeGame.Prints("read items.json")), baselines: baselines);

        // Assert
        baselines.ReadOutput(Manual.Id).Should().Equal("read items.json");
    }

    [Fact]
    public async Task A_baseline_measured_without_its_output_is_measured_again()
    {
        // Arrange
        var baselines = new TestRunBaselines(_dir.Combine("baselines"));
        await RunAsync(new FakeGameProcesses(), baselines: baselines);
        File.Delete(baselines.OutputPath(Manual.Id));
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning);

        // Act
        await RunAsync(processes, baselines: baselines);

        // Assert
        processes.Started.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_quick_run_launches_the_translation_once()
    {
        // Arrange
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning, FakeGame.KeepsRunning);

        // Act
        var result = await RunAsync(processes, quick: true);

        // Assert
        new { result.Outcome, Starts = processes.Started.Count, Quick = result.Detail!.StartsWith("quick run", StringComparison.Ordinal) }
            .Should().Be(new { Outcome = TestRunOutcome.Passed, Starts = 2, Quick = true });
    }

    [Fact]
    public async Task A_quick_run_still_tells_a_crash()
    {
        // Arrange
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning, FakeGame.Closes(3));

        // Act
        var result = await RunAsync(processes, quick: true);

        // Assert
        result.Outcome.Should().Be(TestRunOutcome.Crashed);
    }

    [Theory]
    [InlineData(20, 50, 30)]
    [InlineData(45, 50, 50)]
    public void A_quick_run_watches_the_loading_time_and_ten_seconds_within_the_full_watch(int readySeconds, int watchSeconds, int quickSeconds)
    {
        // Arrange
        var options = TestRunOptions.Default;

        // Act
        var watch = options.QuickWatch(TimeSpan.FromSeconds(readySeconds), TimeSpan.FromSeconds(watchSeconds));

        // Assert
        watch.Should().Be(TimeSpan.FromSeconds(quickSeconds));
    }

    [Fact]
    public void A_game_never_seen_loaded_gets_the_full_watch_on_a_quick_run()
    {
        // Arrange
        var options = TestRunOptions.Default;

        // Act
        var watch = options.QuickWatch(null, TimeSpan.FromSeconds(50));

        // Assert
        watch.Should().Be(TimeSpan.FromSeconds(50));
    }

    [Fact]
    public async Task A_game_that_closes_untranslated_isnt_remembered()
    {
        // Arrange
        var baselines = new TestRunBaselines(_dir.Combine("baselines"));

        // Act
        await RunAsync(new FakeGameProcesses(FakeGame.Closes(1)), baselines: baselines);

        // Assert
        baselines.Find(Manual).Should().BeNull();
    }

    [Fact]
    public async Task A_picture_on_the_window_with_calm_disk_reads_is_loaded()
    {
        // Arrange
        var probe = new FakeProbe(FakeProbe.Checkers, bytes: 1000);

        // Act
        var result = await RunAsync(new FakeGameProcesses(), options: Loading, probe: probe);

        // Assert
        result.Ready.Should().NotBeNull();
    }

    [Fact]
    public async Task A_window_that_stays_black_without_read_counts_never_reads_as_loaded()
    {
        // Arrange
        var probe = new FakeProbe(FakeProbe.Black, bytes: null);

        // Act
        var result = await RunAsync(new FakeGameProcesses(), probe: probe);

        // Assert
        result.Ready.Should().BeNull();
    }

    [Fact]
    public async Task Disk_reads_still_growing_keep_the_game_loading()
    {
        // Arrange
        var probe = new FakeProbe(FakeProbe.Checkers, bytes: 1000) { Growth = 10 * 1024 * 1024 };

        // Act
        var result = await RunAsync(new FakeGameProcesses(), probe: probe);

        // Assert
        result.Ready.Should().BeNull();
    }

    [Fact]
    public async Task Screenshots_of_the_original_and_the_translation_are_saved()
    {
        // Arrange
        var probe = new FakeProbe(FakeProbe.Checkers, bytes: 1000);

        // Act
        var result = await RunAsync(new FakeGameProcesses(), options: Loading, probe: probe);

        // Assert
        result.Shots!.Select(s => (s.Kind, Exists: File.Exists(s.Path)))
            .Should().Equal((TestRunShotKind.Original, true), (TestRunShotKind.Translated, true), (TestRunShotKind.Last, true));
    }

    [Fact]
    public async Task The_agent_can_ask_for_a_longer_watch()
    {
        // Arrange: the original's own watch is its loading time (≥ 100 ms); a slow runner may take a few hundred to load
        // it, and one past MaxReady would read as "never loaded" (the cap), so both stay well apart from what is asked.
        var options = Short with { MaxWatch = TimeSpan.FromSeconds(2), MaxReady = TimeSpan.FromSeconds(5) };
        var probe = new FakeProbe(FakeProbe.Checkers, bytes: 1000);

        // Act
        var result = await RunAsync(new FakeGameProcesses(), options: options, probe: probe, atLeast: TimeSpan.FromMilliseconds(900));

        // Assert
        result.Watch.Should().Be(TimeSpan.FromMilliseconds(900));
    }

    [Fact]
    public async Task A_longer_watch_than_the_cap_is_cut_to_the_cap()
    {
        // Arrange
        var processes = new FakeGameProcesses();

        // Act
        var result = await RunAsync(processes, atLeast: TimeSpan.FromHours(1));

        // Assert
        result.Watch.Should().Be(Short.MaxWatch);
    }

    [Theory]
    [InlineData(20, 50)]
    [InlineData(1, 30)]
    [InlineData(110, 180)]
    public void The_watch_is_the_loading_time_and_a_half_and_20_seconds_within_30_seconds_and_3_minutes(int readySeconds, int watchSeconds)
    {
        // Arrange
        var options = TestRunOptions.Default;

        // Act
        var watch = options.WatchFor(TimeSpan.FromSeconds(readySeconds));

        // Assert
        watch.Should().Be(TimeSpan.FromSeconds(watchSeconds));
    }

    [Fact]
    public void A_game_never_seen_loaded_is_watched_for_the_cap()
    {
        // Arrange
        var options = TestRunOptions.Default;

        // Act
        var watch = options.WatchFor(null);

        // Assert
        watch.Should().Be(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public async Task A_program_the_os_refuses_is_skipped()
    {
        // Arrange
        var processes = new FakeGameProcesses { FailToStart = true };

        // Act
        var result = await RunAsync(processes);

        // Assert
        new { result.Outcome, result.Skip, Data = File.ReadAllText(CopyFile("data/a.json")) }
            .Should().Be(new { Outcome = TestRunOutcome.Skipped, Skip = (TestRunSkip?)TestRunSkip.StartFailed, Data = "original" });
    }

    [Fact]
    public async Task A_windows_program_on_macos_is_skipped_without_starting()
    {
        // Arrange
        File.Delete(CopyFile("Game.sh"));
        _dir.File("job/game/Game.exe", "MZ stub");
        var processes = new FakeGameProcesses();

        // Act
        var result = await RunAsync(processes, os: OsKind.MacOS);

        // Assert
        new { result.Skip, Starts = processes.Started.Count }.Should().Be(new { Skip = (TestRunSkip?)TestRunSkip.OtherPlatform, Starts = 0 });
    }

    [Fact]
    public async Task The_game_runs_rather_than_its_editor_beside_it()
    {
        // Arrange
        File.Delete(CopyFile("Game.sh"));
        _dir.File("job/game/Lantern Editor.app/Contents/MacOS/Lantern Editor", "macho")
            .File("job/game/Lantern.app/Contents/MacOS/Lantern", "macho");
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning);

        // Act
        await RunAsync(processes, os: OsKind.MacOS);

        // Assert
        processes.Started.Select(s => Path.GetFileName(s.FileName)).Should().AllBe("Lantern");
    }

    [Fact]
    public async Task A_copy_without_a_program_is_skipped()
    {
        // Arrange
        File.Delete(CopyFile("Game.sh"));
        var processes = new FakeGameProcesses();

        // Act
        var result = await RunAsync(processes);

        // Assert
        result.Skip.Should().Be(TestRunSkip.NoExecutable);
    }

    [Fact]
    public async Task A_program_wrapped_in_steam_drm_is_skipped_without_starting()
    {
        // Arrange
        File.Delete(CopyFile("Game.sh"));
        _dir.File("job/game/Game.exe", SteamStubPe());
        var processes = new FakeGameProcesses();

        // Act
        var result = await RunAsync(processes, os: OsKind.Windows);

        // Assert
        new { result.Skip, Starts = processes.Started.Count }.Should().Be(new { Skip = (TestRunSkip?)TestRunSkip.SteamDrm, Starts = 0 });
    }

    [Fact]
    public async Task A_game_the_player_has_open_is_not_test_run()
    {
        // Arrange
        Directory.CreateDirectory(Copy);
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning, FakeGame.KeepsRunning);
        var running = new RunningGames(new RunningProcess(42, PathNames.RealPath(CopyFile("Game.sh"))));
        var host = OperatingSystem.IsWindows() ? OsKind.Windows : OperatingSystem.IsMacOS() ? OsKind.MacOS : OsKind.Linux; // the process paths are this machine's

        // Act
        var result = await new GameTestRunner(processes, host, Short, processes: running).RunAsync(
            new TestRunRequest(Manual, Copy, _dir.Combine("job/build"), BuildFiles, Job, _ => { }) { ShotsFolder = _dir.Combine("shots") },
            TestContext.Current.CancellationToken);

        // Assert
        new { result.Skip, Starts = processes.Started.Count }.Should().Be(new { Skip = (TestRunSkip?)TestRunSkip.GameRunning, Starts = 0 });
    }

    [Fact]
    public async Task Test_runs_turned_off_are_skipped()
    {
        // Arrange
        var processes = new FakeGameProcesses();

        // Act
        var result = await RunAsync(processes, options: Short with { Enabled = false });

        // Assert
        new { result.Skip, Starts = processes.Started.Count }.Should().Be(new { Skip = (TestRunSkip?)TestRunSkip.TurnedOff, Starts = 0 });
    }

    [Fact]
    public async Task A_steam_game_runs_with_its_app_id_so_steam_doesnt_start_the_installed_game()
    {
        // Arrange
        string? appIdFile = null;
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning, FakeGame.KeepsRunning) { OnStart = _ => appIdFile = File.ReadAllText(CopyFile("steam_appid.txt")) };

        // Act
        await RunAsync(processes, Steam);

        // Assert
        new { AppIdFile = appIdFile, Variable = processes.Started[0].Environment["SteamAppId"] }.Should().Be(new { AppIdFile = (string?)"620", Variable = (string?)"620" });
    }

    [Fact]
    public async Task The_app_id_file_is_removed_after_the_run()
    {
        // Arrange
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning);

        // Act
        await RunAsync(processes, Steam);

        // Assert
        File.Exists(CopyFile("steam_appid.txt")).Should().BeFalse();
    }

    [Fact]
    public void An_overlay_left_by_a_crash_is_undone()
    {
        // Arrange
        TestRunOverlay.Apply(Job, Copy, _dir.Combine("job/build"), BuildFiles, ["steam_appid.txt"]);
        File.WriteAllText(CopyFile("steam_appid.txt"), "620");

        // Act
        TestRunOverlay.Recover(Job, Copy);

        // Assert
        Directory.EnumerateFiles(Copy, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Copy, f).Replace('\\', '/')).Order(StringComparer.Ordinal)
            .Should().Equal("Game.sh", "data/a.json");
    }

    /// <summary>A probe that always sees the same picture and a read count that grows by <see cref="Growth"/> per look.</summary>
    private sealed class FakeProbe(GameFrame frame, long? bytes) : IGameProbe
    {
        private long? _bytes = bytes;

        public static GameFrame Checkers { get; } = Pattern(i => (i / 4 + i / 64) % 2 == 0 ? (byte)230 : (byte)20);

        public static GameFrame Black { get; } = Pattern(_ => 0);

        public long Growth { get; init; }

        public bool CanCapture => true;

        public long? ReadBytes(int processId) => _bytes += Growth;

        public GameFrame? Capture(int processId) => frame;

        private static GameFrame Pattern(Func<int, byte> value)
        {
            var pixels = new byte[64 * 64 * 4];
            for (var i = 0; i < 64 * 64; i++)
            {
                var v = value(i);
                pixels[i * 4] = v;
                pixels[i * 4 + 1] = v;
                pixels[i * 4 + 2] = v;
                pixels[i * 4 + 3] = 255;
            }

            return new GameFrame(64, 64, pixels);
        }
    }
}

/// <summary>A process list with the given processes running.</summary>
internal sealed class RunningGames(params RunningProcess[] processes) : IProcessList
{
    public IReadOnlyList<RunningProcess> Snapshot() => processes;
}
