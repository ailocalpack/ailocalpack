using AiLocalPack.Core.Install;
using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Install;

/// <summary>The install transaction (task 27): backup, apply, verify, rollback, uninstall, re-apply, crash recovery.</summary>
public sealed class PackInstallerTests : IDisposable
{
    private readonly InstallFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static bool HostIsWindows => OperatingSystem.IsWindows();

    public static bool HostIsNotWindows => !OperatingSystem.IsWindows();

    private static Action<InstallStage, string> FailOnWrite(int number)
    {
        var count = 0;
        return (stage, _) =>
        {
            if (stage == InstallStage.Writing && ++count == number)
            {
                throw new IOException("Simulated failure.");
            }
        };
    }

    [Fact]
    public void Install_writes_the_new_files()
    {
        // Arrange
        var installer = _fx.Installer();

        // Act
        var result = installer.Install(_fx.FiveFiles(), null, cancellationToken: Ct);

        // Assert
        result.Succeeded.Should().BeTrue();
        new[] { _fx.Read("data/Map001.json"), _fx.Read("data/System.json"), _fx.Read("fonts/new/font.json") }
            .Should().Equal("map 1 ru", "system ru", "font ru");
    }

    [Fact]
    public void Install_records_the_pack_with_original_and_new_hashes()
    {
        // Arrange
        var store = new InstalledPackStore(_fx.Paths);

        // Act
        _fx.Installer(store).Install(_fx.Request(("data/Map001.json", "map 1 ru"), ("data/Fonts.json", "fonts ru")), null, cancellationToken: Ct);

        // Assert
        var record = new InstalledPackStore(_fx.Paths).Find(InstallFixture.Id)!;
        record.Files.Should().BeEquivalentTo(
        [
            new InstalledFile("data/Map001.json", InstallFixture.Sha("map 1 original"), InstallFixture.Sha("map 1 ru")),
            new InstalledFile("data/Fonts.json", null, InstallFixture.Sha("fonts ru")),
        ]);
    }

    [Fact]
    public void Install_copies_the_originals_into_the_backup()
    {
        // Arrange
        var installer = _fx.Installer();

        // Act
        var record = installer.Install(_fx.Request(("data/Map001.json", "map 1 ru")), null, cancellationToken: Ct).Installed!;

        // Assert
        var copy = Path.Combine(_fx.Paths.Backups, "steam-620", record.Backup, "files", "data", "Map001.json");
        File.ReadAllText(copy).Should().Be("map 1 original");
    }

    [Fact]
    public void Install_leaves_no_journal_behind()
    {
        // Arrange
        var installer = _fx.Installer();

        // Act
        installer.Install(_fx.FiveFiles(), null, cancellationToken: Ct);

        // Assert
        installer.HasInterruptedTransaction.Should().BeFalse();
    }

    [Fact]
    public void Install_reports_each_stage()
    {
        // Arrange
        var progress = new RecordingProgress<InstallProgress>();

        // Act
        _fx.Installer().Install(_fx.FiveFiles(), null, progress, Ct);

        // Assert
        progress.Reports.Where(p => p.Done == p.Total).Select(p => p.Stage).Should()
            .Equal(InstallStage.Checking, InstallStage.BackingUp, InstallStage.Writing, InstallStage.Verifying);
    }

    [Fact]
    public void Failure_on_the_third_of_five_files_restores_the_game_folder()
    {
        // Arrange
        var before = _fx.Snapshot();
        var installer = _fx.Installer();
        installer.BeforeFile = FailOnWrite(3);

        // Act
        installer.Install(_fx.FiveFiles(), null, cancellationToken: Ct);

        // Assert
        _fx.Snapshot().Should().BeEquivalentTo(before);
    }

    [Fact]
    public void Failure_records_nothing_and_drops_the_backup()
    {
        // Arrange
        var installer = _fx.Installer();
        installer.BeforeFile = FailOnWrite(3);

        // Act
        var result = installer.Install(_fx.FiveFiles(), null, cancellationToken: Ct);

        // Assert
        new
        {
            result.Failure!.Problem,
            result.Failure.File,
            result.Failure.FilesRestored,
            Record = new InstalledPackStore(_fx.Paths).Find(InstallFixture.Id),
            Backups = Directory.Exists(_fx.Paths.Backups) ? Directory.EnumerateFileSystemEntries(_fx.Paths.Backups).Count() : 0,
            Journal = installer.HasInterruptedTransaction,
        }.Should().BeEquivalentTo(new
        {
            Problem = InstallProblem.IoError,
            File = "data/System.json",
            FilesRestored = true,
            Record = (InstalledPack?)null,
            Backups = 0,
            Journal = false,
        });
    }

    [Fact(SkipWhen = nameof(HostIsWindows), Skip = "Unix permissions")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void Read_only_file_fails_before_anything_changes_on_unix()
    {
        // Arrange
        var before = _fx.Snapshot();
        var file = _fx.GameFolder.Combine("data/System.json");
        File.SetUnixFileMode(file, UnixFileMode.UserRead);
        try
        {
            // Act
            var result = _fx.Installer().Install(_fx.FiveFiles(), null, cancellationToken: Ct);

            // Assert
            new { result.Failure!.Problem, result.Failure.File, Unchanged = _fx.Snapshot().SequenceEqual(before) }
                .Should().BeEquivalentTo(new { Problem = InstallProblem.ReadOnly, File = "data/System.json", Unchanged = true });
        }
        finally
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact(SkipWhen = nameof(HostIsNotWindows), Skip = "The read-only attribute is Windows'")]
    public void Read_only_file_fails_before_anything_changes_on_windows()
    {
        // Arrange
        var file = _fx.GameFolder.Combine("data/System.json");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            // Act
            var result = _fx.Installer().Install(_fx.FiveFiles(), null, cancellationToken: Ct);

            // Assert
            new { result.Failure!.Problem, Map = _fx.Read("data/Map001.json") }
                .Should().BeEquivalentTo(new { Problem = InstallProblem.ReadOnly, Map = "map 1 original" });
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    [Fact(SkipWhen = nameof(HostIsNotWindows), Skip = "Only Windows locks open files")]
    public void Locked_file_fails_as_in_use_on_windows()
    {
        // Arrange
        using var locked = new FileStream(_fx.GameFolder.Combine("data/System.json"), FileMode.Open, FileAccess.Read, FileShare.None);

        // Act
        var result = _fx.Installer().Install(_fx.FiveFiles(), null, cancellationToken: Ct);

        // Assert
        new { result.Failure!.Problem, result.Failure.File, Map = _fx.Read("data/Map001.json") }
            .Should().BeEquivalentTo(new { Problem = InstallProblem.FileInUse, File = "data/System.json", Map = "map 1 original" });
    }

    [Fact]
    public void Running_game_is_refused()
    {
        // Arrange
        _fx.Processes.Running.Add(new RunningProcess(42, Path.Combine(PathNames.RealPath(_fx.GameFolder.Path), "Game.exe")));

        // Act
        var result = _fx.Installer().Install(_fx.FiveFiles(), null, cancellationToken: Ct);

        // Assert
        new { result.Failure!.Problem, Map = _fx.Read("data/Map001.json") }
            .Should().BeEquivalentTo(new { Problem = InstallProblem.GameRunning, Map = "map 1 original" });
    }

    [Fact]
    public void Unavailable_game_is_refused()
    {
        // Arrange
        _fx.MakeGame(available: false);

        // Act
        var result = _fx.Installer().Install(_fx.FiveFiles(), null, cancellationToken: Ct);

        // Assert
        result.Failure!.Problem.Should().Be(InstallProblem.GameUnavailable);
    }

    [Fact]
    public void Blocked_game_throws_whatever_the_ui_showed()
    {
        // Arrange
        _fx.MakeGame(antiCheat: "eac");
        var request = _fx.FiveFiles();
        var installer = _fx.Installer();

        // Act
        var act = () => installer.Install(request, null, cancellationToken: Ct);

        // Assert
        act.Should().Throw<GameBlockedException>().Which.Operation.Should().Be(BlockedOperation.Install);
    }

    [Fact]
    public void Second_install_for_the_same_game_is_refused()
    {
        // Arrange
        var installer = _fx.Installer();
        installer.Install(_fx.Request(("data/Map001.json", "map 1 ru")), null, cancellationToken: Ct);

        // Act
        var result = installer.Install(_fx.Request(("data/Map002.json", "map 2 ru")), null, cancellationToken: Ct);

        // Assert
        new { result.Failure!.Problem, Map = _fx.Read("data/Map002.json") }
            .Should().BeEquivalentTo(new { Problem = InstallProblem.AlreadyInstalled, Map = "map 2 original" });
    }

    [Fact]
    public void Install_while_another_runs_is_refused_as_busy()
    {
        // Arrange
        Directory.CreateDirectory(_fx.Paths.Data);
        using var held = new FileStream(_fx.Paths.InstallLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        // Act
        var result = _fx.Installer().Install(_fx.FiveFiles(), null, cancellationToken: Ct);

        // Assert
        result.Failure!.Problem.Should().Be(InstallProblem.Busy);
    }

    [Fact]
    public void Path_leaving_the_game_folder_is_rejected()
    {
        // Arrange
        var request = _fx.Request(("data/Map001.json", "x")) with { Files = [new InstallFile("../outside.txt", _fx.Staging.Path)] };
        var installer = _fx.Installer();

        // Act
        var act = () => installer.Install(request, null, cancellationToken: Ct);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Crash_while_writing_is_rolled_back_on_the_next_start()
    {
        // Arrange
        var before = _fx.Snapshot();
        var crashing = _fx.Installer();
        crashing.BeforeFile = FailOnWrite(3);
        crashing.RollBackOnFailure = false;
        var crash = () => crashing.Install(_fx.FiveFiles(), null, cancellationToken: Ct);
        crash.Should().Throw<IOException>();

        // Act
        var recovery = _fx.Installer().Recover(Ct);

        // Assert
        new { recovery.Outcome, Unchanged = _fx.Snapshot().SequenceEqual(before), Record = new InstalledPackStore(_fx.Paths).Find(InstallFixture.Id) }
            .Should().BeEquivalentTo(new { Outcome = RecoveryOutcome.RolledBack, Unchanged = true, Record = (InstalledPack?)null });
    }

    [Fact]
    public void Crash_with_a_temp_file_left_in_the_game_folder_is_cleaned_up()
    {
        // Arrange
        var before = _fx.Snapshot();
        var crashing = _fx.Installer();
        crashing.BeforeFile = (stage, path) =>
        {
            if (stage == InstallStage.Writing && path == "data/Map002.json")
            {
                File.WriteAllText(_fx.GameFolder.Combine("data/.Map002.json.abcdefgh.ailocalpack"), "half");
                throw new IOException("Simulated crash.");
            }
        };
        crashing.RollBackOnFailure = false;
        var crash = () => crashing.Install(_fx.FiveFiles(), null, cancellationToken: Ct);
        crash.Should().Throw<IOException>();

        // Act
        _fx.Installer().Recover(Ct);

        // Assert
        _fx.Snapshot().Should().BeEquivalentTo(before);
    }

    [Fact]
    public void Crash_while_backing_up_leaves_the_game_untouched_and_drops_the_backup()
    {
        // Arrange
        var before = _fx.Snapshot();
        var crashing = _fx.Installer();
        crashing.BeforeFile = (stage, path) =>
        {
            if (stage == InstallStage.BackingUp && path == "data/Map002.json")
            {
                throw new IOException("Simulated crash.");
            }
        };
        crashing.RollBackOnFailure = false;
        var crash = () => crashing.Install(_fx.FiveFiles(), null, cancellationToken: Ct);
        crash.Should().Throw<IOException>();

        // Act
        var recovery = _fx.Installer().Recover(Ct);

        // Assert
        new { recovery.Outcome, Unchanged = _fx.Snapshot().SequenceEqual(before), BackupLeft = Directory.Exists(Path.Combine(_fx.Paths.Backups, "steam-620")) }
            .Should().BeEquivalentTo(new { Outcome = RecoveryOutcome.RolledBack, Unchanged = true, BackupLeft = false });
    }

    [Fact]
    public void Crash_after_the_record_was_saved_completes_the_install()
    {
        // Arrange
        var crashing = _fx.Installer();
        crashing.AfterCommit = () => throw new IOException("Simulated crash.");
        crashing.RollBackOnFailure = false;
        var crash = () => crashing.Install(_fx.FiveFiles(), null, cancellationToken: Ct);
        crash.Should().Throw<IOException>();
        var installer = _fx.Installer();

        // Act
        var recovery = installer.Recover(Ct);

        // Assert
        new { recovery.Outcome, Map = _fx.Read("data/Map001.json"), Installed = new InstalledPackStore(_fx.Paths).Find(InstallFixture.Id) is not null, installer.HasInterruptedTransaction }
            .Should().BeEquivalentTo(new { Outcome = RecoveryOutcome.Completed, Map = "map 1 ru", Installed = true, HasInterruptedTransaction = false });
    }

    [Fact]
    public void Install_finishes_an_interrupted_transaction_first()
    {
        // Arrange
        var crashing = _fx.Installer();
        crashing.BeforeFile = FailOnWrite(3);
        crashing.RollBackOnFailure = false;
        var crash = () => crashing.Install(_fx.FiveFiles(), null, cancellationToken: Ct);
        crash.Should().Throw<IOException>();

        // Act
        var result = _fx.Installer().Install(_fx.FiveFiles(), null, cancellationToken: Ct);

        // Assert
        new { result.Succeeded, Map = _fx.Read("data/Map001.json") }.Should().BeEquivalentTo(new { Succeeded = true, Map = "map 1 ru" });
    }

    [Fact]
    public void Recover_without_a_journal_does_nothing()
    {
        // Arrange
        var installer = _fx.Installer();

        // Act
        var recovery = installer.Recover(Ct);

        // Assert
        recovery.Outcome.Should().Be(RecoveryOutcome.Nothing);
    }

    [Fact]
    public void Uninstall_restores_byte_identical_originals_and_removes_added_files()
    {
        // Arrange
        var before = _fx.Snapshot();
        var installer = _fx.Installer();
        installer.Install(_fx.FiveFiles(), null, cancellationToken: Ct);

        // Act
        installer.Uninstall(_fx.Game, cancellationToken: Ct);

        // Assert
        _fx.Snapshot().Should().BeEquivalentTo(before);
    }

    [Fact]
    public void Uninstall_deletes_the_record_and_the_backup()
    {
        // Arrange
        var installer = _fx.Installer();
        installer.Install(_fx.FiveFiles(), null, cancellationToken: Ct);

        // Act
        var result = installer.Uninstall(_fx.Game, cancellationToken: Ct);

        // Assert
        new
        {
            result.Succeeded,
            Restored = result.Restored.Count,
            Removed = result.Removed.Count,
            Record = new InstalledPackStore(_fx.Paths).Find(InstallFixture.Id),
            BackupLeft = Directory.Exists(Path.Combine(_fx.Paths.Backups, "steam-620")),
        }.Should().BeEquivalentTo(new { Succeeded = true, Restored = 3, Removed = 2, Record = (InstalledPack?)null, BackupLeft = false });
    }

    [Fact]
    public void Uninstall_leaves_a_file_the_game_rewrote_since()
    {
        // Arrange
        var installer = _fx.Installer();
        installer.Install(_fx.FiveFiles(), null, cancellationToken: Ct);
        _fx.GameFolder.File("data/Map001.json", "map 1 from a game update");

        // Act
        var result = installer.Uninstall(_fx.Game, cancellationToken: Ct);

        // Assert
        new { result.Kept, Map = _fx.Read("data/Map001.json"), Other = _fx.Read("data/Map002.json") }
            .Should().BeEquivalentTo(new { Kept = new[] { "data/Map001.json" }, Map = "map 1 from a game update", Other = "map 2 original" });
    }

    [Fact]
    public void Uninstall_without_a_pack_is_refused()
    {
        // Arrange
        var installer = _fx.Installer();

        // Act
        var result = installer.Uninstall(_fx.Game, cancellationToken: Ct);

        // Assert
        result.Failure!.Problem.Should().Be(InstallProblem.NotInstalled);
    }

    [Fact]
    public void Uninstall_that_stopped_midway_can_be_retried()
    {
        // Arrange
        var before = _fx.Snapshot();
        var installer = _fx.Installer();
        installer.Install(_fx.FiveFiles(), null, cancellationToken: Ct);
        installer.BeforeFile = (stage, path) =>
        {
            if (stage == InstallStage.Restoring && path == "data/System.json")
            {
                throw new IOException("Simulated failure.");
            }
        };
        installer.Uninstall(_fx.Game, cancellationToken: Ct).Succeeded.Should().BeFalse();
        installer.BeforeFile = null;

        // Act
        var retry = installer.Uninstall(_fx.Game, cancellationToken: Ct);

        // Assert
        new { retry.Succeeded, Unchanged = _fx.Snapshot().SequenceEqual(before) }.Should().BeEquivalentTo(new { Succeeded = true, Unchanged = true });
    }

    [Fact]
    public void Crash_during_uninstall_is_finished_on_the_next_start()
    {
        // Arrange
        var before = _fx.Snapshot();
        var crashing = _fx.Installer();
        crashing.Install(_fx.FiveFiles(), null, cancellationToken: Ct);
        crashing.BeforeFile = (stage, path) =>
        {
            if (stage == InstallStage.Restoring && path == "data/System.json")
            {
                throw new IOException("Simulated crash.");
            }
        };
        crashing.RollBackOnFailure = false;
        var crash = () => crashing.Uninstall(_fx.Game, cancellationToken: Ct);
        crash.Should().Throw<IOException>();

        // Act
        var recovery = _fx.Installer().Recover(Ct);

        // Assert
        new { recovery.Outcome, Unchanged = _fx.Snapshot().SequenceEqual(before), Record = new InstalledPackStore(_fx.Paths).Find(InstallFixture.Id) }
            .Should().BeEquivalentTo(new { Outcome = RecoveryOutcome.UninstallFinished, Unchanged = true, Record = (InstalledPack?)null });
    }

    [Fact]
    public void Reapply_after_an_update_wiped_the_pack_backs_up_the_new_originals()
    {
        // Arrange
        var installer = _fx.Installer();
        installer.Install(_fx.Request(("data/Map001.json", "map 1 ru"), ("data/Map002.json", "map 2 ru")), null, cancellationToken: Ct);
        _fx.GameFolder.File("data/Map001.json", "map 1 v2 original");
        installer.Reapply(_fx.Request(("data/Map001.json", "map 1 ru"), ("data/Map002.json", "map 2 ru")), null, cancellationToken: Ct);

        // Act
        installer.Uninstall(_fx.Game, cancellationToken: Ct);

        // Assert
        new { Updated = _fx.Read("data/Map001.json"), Untouched = _fx.Read("data/Map002.json") }
            .Should().BeEquivalentTo(new { Updated = "map 1 v2 original", Untouched = "map 2 original" });
    }

    [Fact]
    public void Reapply_drops_the_old_backup()
    {
        // Arrange
        var installer = _fx.Installer();
        var first = installer.Install(_fx.Request(("data/Map001.json", "map 1 ru")), null, cancellationToken: Ct).Installed!;
        _fx.GameFolder.File("data/Map001.json", "map 1 v2 original");

        // Act
        var second = installer.Reapply(_fx.Request(("data/Map001.json", "map 1 ru")), null, cancellationToken: Ct).Installed!;

        // Assert
        Directory.EnumerateDirectories(Path.Combine(_fx.Paths.Backups, "steam-620")).Select(Path.GetFileName)
            .Should().Equal(second.Backup).And.NotContain(first.Backup);
    }

    [Fact]
    public void Failed_reapply_keeps_the_installed_pack()
    {
        // Arrange
        var installer = _fx.Installer();
        installer.Install(_fx.Request(("data/Map001.json", "map 1 ru"), ("data/Map002.json", "map 2 ru")), null, cancellationToken: Ct);
        installer.BeforeFile = FailOnWrite(2);

        // Act
        installer.Reapply(_fx.Request(2, ("data/Map001.json", "map 1 ru rev 2"), ("data/Map002.json", "map 2 ru rev 2")), null, cancellationToken: Ct);

        // Assert
        new { Map1 = _fx.Read("data/Map001.json"), Map2 = _fx.Read("data/Map002.json"), new InstalledPackStore(_fx.Paths).Find(InstallFixture.Id)!.Revision }
            .Should().BeEquivalentTo(new { Map1 = "map 1 ru", Map2 = "map 2 ru", Revision = 1 });
    }

    [Fact]
    public void Reapply_without_an_installed_pack_is_refused()
    {
        // Arrange
        var installer = _fx.Installer();

        // Act
        var result = installer.Reapply(_fx.FiveFiles(), null, cancellationToken: Ct);

        // Assert
        result.Failure!.Problem.Should().Be(InstallProblem.NotInstalled);
    }
}
