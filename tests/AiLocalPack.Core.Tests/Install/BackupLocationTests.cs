using AiLocalPack.Core.Install;
using AiLocalPack.Core.Tests.Generation;

namespace AiLocalPack.Core.Tests.Install;

public sealed class BackupLocationTests : IDisposable
{
    private readonly InstallFixture _f = new();
    private readonly TempDir _elsewhere = new();

    public void Dispose()
    {
        _f.Dispose();
        _elsewhere.Dispose();
    }

    private BackupLocation Location(long? free = null, bool? sameDrive = false) =>
        new(_f.Paths, new FixedDiskSpace(free)) { SameDrive = sameDrive };

    /// <summary>Installs the sample pack, so the backups hold the originals of two files.</summary>
    private void Install() =>
        _f.Installer().Install(_f.Request(("data/Map001.json", "map 1 ru"), ("data/System.json", "system ru")), null, cancellationToken: Ct).Succeeded.Should().BeTrue();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Target => _elsewhere.Combine(BackupLocation.FolderName);

    /// <summary>The location file as a move from the default folder to <see cref="Target"/> left it.</summary>
    private void WriteJournal(string path, string phase) =>
        File.WriteAllText(_f.Paths.BackupsLocationFile, System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            path,
            move = new { from = _f.Paths.Backups, to = Target, phase, createdTarget = true },
        }));

    /// <summary>The files under <paramref name="folder"/> as one comparable line (relative paths, sorted).</summary>
    private static string FilesUnder(string folder) =>
        Directory.Exists(folder) ? string.Join(", ", Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).Order(StringComparer.Ordinal)) : string.Empty;

    private static int CountFiles(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Count() : 0;

    [Fact]
    public void Root_is_the_default_folder_until_the_backups_are_moved()
    {
        // Arrange
        var location = Location();

        // Act
        var root = location.Root;

        // Assert
        root.Should().Be(_f.Paths.Backups);
    }

    [Fact]
    public void Target_is_a_backups_folder_inside_the_picked_one()
    {
        // Arrange
        var picked = _elsewhere.Path;

        // Act
        var target = BackupLocation.TargetFor(picked);

        // Assert
        target.Should().Be(Path.Combine(Path.GetFullPath(picked), BackupLocation.FolderName));
    }

    [Fact]
    public void Target_is_the_picked_folder_when_it_is_a_backups_folder_already()
    {
        // Arrange
        var picked = Target;

        // Act
        var target = BackupLocation.TargetFor(picked);

        // Assert
        target.Should().Be(Path.GetFullPath(picked));
    }

    [Fact]
    public void Move_to_another_drive_copies_every_backup_and_switches_the_root()
    {
        // Arrange
        Install();
        var before = FilesUnder(_f.Paths.Backups);
        var location = Location();

        // Act
        var result = location.Move(_elsewhere.Path, cancellationToken: Ct);

        // Assert
        result.Should().Be(new BackupMoveResult(Target));
        (FilesUnder(Target), location.Root, Directory.Exists(_f.Paths.Backups)).Should().Be((before, Target, false));
    }

    [Fact]
    public void Move_on_the_same_drive_renames_the_folder()
    {
        // Arrange
        Install();
        var before = FilesUnder(_f.Paths.Backups);

        // Act
        var result = Location(sameDrive: true).Move(_elsewhere.Path, cancellationToken: Ct);

        // Assert
        (result.Succeeded, FilesUnder(Target), Directory.Exists(_f.Paths.Backups)).Should().Be((true, before, false));
    }

    [Fact]
    public void Move_reports_progress_in_bytes()
    {
        // Arrange
        Install();
        var progress = new RecordingProgress<BackupMoveProgress>();

        // Act
        Location().Move(_elsewhere.Path, progress, Ct);

        // Assert
        progress.Reports.Should().HaveCount(3).And.EndWith(new BackupMoveProgress(progress.Reports[^1].BytesTotal, progress.Reports[^1].BytesTotal));
    }

    [Fact]
    public void Uninstall_after_a_move_restores_the_originals_from_the_new_folder()
    {
        // Arrange
        Install();
        Location().Move(_elsewhere.Path, cancellationToken: Ct);

        // Act
        var result = _f.Installer().Uninstall(_f.Game, cancellationToken: Ct);

        // Assert
        (result.Succeeded, _f.Read("data/Map001.json"), _f.Read("data/System.json"), FilesUnder(Target)).Should().Be((true, "map 1 original", "system original", string.Empty));
    }

    [Fact]
    public void An_install_after_a_move_backs_up_into_the_new_folder()
    {
        // Arrange
        Location().Move(_elsewhere.Path, cancellationToken: Ct);

        // Act
        Install();

        // Assert
        (CountFiles(Target), Directory.Exists(_f.Paths.Backups)).Should().Be((2, false));
    }

    [Fact]
    public void Move_without_backups_only_switches_the_root()
    {
        // Arrange
        var location = Location();

        // Act
        var result = location.Move(_elsewhere.Path, cancellationToken: Ct);

        // Assert
        (result.Succeeded, location.Root, Directory.Exists(Target)).Should().Be((true, Target, true));
    }

    [Fact]
    public void Move_into_the_folder_in_use_is_refused()
    {
        // Arrange
        var location = Location();
        location.Move(_elsewhere.Path, cancellationToken: Ct);

        // Act
        var result = location.Move(Target, cancellationToken: Ct);

        // Assert
        result.Problem.Should().Be(BackupMoveProblem.SameFolder);
    }

    [Fact]
    public void Move_into_a_folder_inside_the_backups_is_refused()
    {
        // Arrange
        Install();

        // Act
        var result = Location().Move(Path.Combine(_f.Paths.Backups, "inner"), cancellationToken: Ct);

        // Assert
        (result.Problem, CountFiles(_f.Paths.Backups)).Should().Be((BackupMoveProblem.Nested, 2));
    }

    [Fact]
    public void Move_into_a_folder_with_files_is_refused()
    {
        // Arrange
        _elsewhere.File($"{BackupLocation.FolderName}/notes.txt", "mine");

        // Act
        var result = Location().Move(_elsewhere.Path, cancellationToken: Ct);

        // Assert
        (result.Problem, FilesUnder(Target)).Should().Be((BackupMoveProblem.NotEmpty, "notes.txt"));
    }

    [Fact]
    public void Move_to_a_drive_without_the_space_is_refused_before_anything_is_written()
    {
        // Arrange
        Install();

        // Act
        var result = Location(free: 10).Move(_elsewhere.Path, cancellationToken: Ct);

        // Assert
        (result.Problem, Directory.Exists(Target), File.Exists(_f.Paths.BackupsLocationFile)).Should().Be((BackupMoveProblem.DiskFull, false, false));
    }

    [Fact]
    public void Move_while_an_install_runs_is_refused()
    {
        // Arrange
        Directory.CreateDirectory(_f.Paths.Data);
        using var held = new FileStream(_f.Paths.InstallLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        // Act
        var result = Location().Move(_elsewhere.Path, cancellationToken: Ct);

        // Assert
        result.Problem.Should().Be(BackupMoveProblem.Busy);
    }

    [Fact]
    public void A_failed_copy_deletes_what_was_copied_and_keeps_the_old_folder()
    {
        // Arrange
        Install();
        var before = FilesUnder(_f.Paths.Backups);
        var location = Location();
        var copied = 0;
        location.BeforeFile = _ =>
        {
            if (++copied == 2)
            {
                throw new IOException("The drive went away.");
            }
        };

        // Act
        var result = location.Move(_elsewhere.Path, cancellationToken: Ct);

        // Assert
        (result.Problem, location.Root, FilesUnder(_f.Paths.Backups), Directory.Exists(Target)).Should().Be((BackupMoveProblem.IoError, _f.Paths.Backups, before, false));
    }

    [Fact]
    public void A_failed_copy_into_an_existing_empty_folder_leaves_it_empty()
    {
        // Arrange
        Install();
        _elsewhere.Dir(BackupLocation.FolderName);
        var location = Location();
        location.BeforeFile = _ => throw new IOException("The drive went away.");

        // Act
        location.Move(_elsewhere.Path, cancellationToken: Ct);

        // Assert
        (Directory.Exists(Target), FilesUnder(Target)).Should().Be((true, string.Empty));
    }

    [Fact]
    public void A_cancelled_move_is_rolled_back_and_rethrown()
    {
        // Arrange
        Install();
        var location = Location();
        using var cancel = new CancellationTokenSource();
        location.BeforeFile = _ => cancel.Cancel();

        // Act
        var act = () => location.Move(_elsewhere.Path, cancellationToken: cancel.Token);

        // Assert
        act.Should().Throw<OperationCanceledException>();
        (location.Root, Directory.Exists(Target)).Should().Be((_f.Paths.Backups, false));
    }

    [Fact]
    public void A_crash_while_copying_keeps_the_old_folder_in_use()
    {
        // Arrange
        Install();
        var location = Location();
        location.RollBackOnFailure = false;
        location.BeforeFile = path => throw new IOException("Crash.");

        // Act
        var act = () => location.Move(_elsewhere.Path, cancellationToken: Ct);

        // Assert
        act.Should().Throw<IOException>();
        (location.Root, location.HasInterruptedMove).Should().Be((_f.Paths.Backups, true));
    }

    [Fact]
    public void Recovery_after_a_crash_while_copying_deletes_the_partial_copy()
    {
        // Arrange
        Install();
        var crashing = Location();
        crashing.RollBackOnFailure = false;
        var copied = 0;
        crashing.BeforeFile = _ =>
        {
            if (++copied == 2)
            {
                throw new IOException("Crash.");
            }
        };
        try
        {
            crashing.Move(_elsewhere.Path, cancellationToken: Ct);
        }
        catch (IOException)
        {
        }

        var location = Location();

        // Act
        var recovered = location.Recover();

        // Assert
        (recovered, location.HasInterruptedMove, location.Root, Directory.Exists(Target), CountFiles(_f.Paths.Backups)).Should().Be((true, false, _f.Paths.Backups, false, 2));
    }

    [Fact]
    public void Recovery_after_a_crash_once_the_folder_was_renamed_completes_the_move()
    {
        // Arrange
        Install();
        var before = FilesUnder(_f.Paths.Backups);
        Location().Move(_elsewhere.Path, cancellationToken: Ct);

        // The journal as it was just before the commit, with the rename done.
        WriteJournal(path: _f.Paths.Backups, phase: "copy");
        var location = Location();

        // Act
        location.Recover();

        // Assert
        (location.Root, location.HasInterruptedMove, FilesUnder(Target)).Should().Be((Target, false, before));
    }

    [Fact]
    public void Recovery_after_a_crash_after_the_commit_deletes_the_old_folder()
    {
        // Arrange
        Install();
        var old = FilesUnder(_f.Paths.Backups);
        _elsewhere.Dir(BackupLocation.FolderName);
        foreach (var file in old.Split(", "))
        {
            _elsewhere.File($"{BackupLocation.FolderName}/{file}", File.ReadAllText(Path.Combine(_f.Paths.Backups, file)));
        }

        WriteJournal(path: Target, phase: "cleanup");
        var location = Location();

        // Act
        location.Recover();

        // Assert
        (location.Root, location.HasInterruptedMove, Directory.Exists(_f.Paths.Backups), FilesUnder(Target)).Should().Be((Target, false, false, old));
    }

    [Fact]
    public void An_install_finishes_an_interrupted_move_first()
    {
        // Arrange
        Install();
        var crashing = Location();
        crashing.RollBackOnFailure = false;
        crashing.BeforeFile = _ => throw new IOException("Crash.");
        try
        {
            crashing.Move(_elsewhere.Path, cancellationToken: Ct);
        }
        catch (IOException)
        {
        }

        // Act
        var result = _f.Installer().Uninstall(_f.Game, cancellationToken: Ct);

        // Assert
        (result.Succeeded, Location().HasInterruptedMove, Directory.Exists(Target), _f.Read("data/Map001.json")).Should().Be((true, false, false, "map 1 original"));
    }

    [Fact]
    public void An_interrupted_move_counts_as_an_interrupted_transaction()
    {
        // Arrange
        var crashing = Location();
        crashing.RollBackOnFailure = false;
        Install();
        crashing.BeforeFile = _ => throw new IOException("Crash.");
        try
        {
            crashing.Move(_elsewhere.Path, cancellationToken: Ct);
        }
        catch (IOException)
        {
        }

        // Act
        var interrupted = _f.Installer().HasInterruptedTransaction;

        // Assert
        interrupted.Should().BeTrue();
    }

    [Fact]
    public void A_damaged_location_file_is_moved_aside_by_recovery()
    {
        // Arrange
        Directory.CreateDirectory(_f.Paths.Data);
        File.WriteAllText(_f.Paths.BackupsLocationFile, "{ not json");
        var location = Location();

        // Act
        var recovered = location.Recover();

        // Assert
        (recovered, location.Root, File.Exists(_f.Paths.BackupsLocationFile), Directory.GetFiles(_f.Paths.Data, "backups-location.json.*.bak").Length).Should().Be((true, _f.Paths.Backups, false, 1));
    }

    [Fact]
    public void A_location_file_of_a_newer_app_is_not_overwritten_by_a_move()
    {
        // Arrange
        Directory.CreateDirectory(_f.Paths.Data);
        var text = $$"""{"schemaVersion": 2, "path": {{System.Text.Json.JsonSerializer.Serialize(_f.Paths.Backups)}}}""";
        File.WriteAllText(_f.Paths.BackupsLocationFile, text);

        // Act
        var result = Location().Move(_elsewhere.Path, cancellationToken: Ct);

        // Assert
        (result.Problem, File.ReadAllText(_f.Paths.BackupsLocationFile)).Should().Be((BackupMoveProblem.NewerFormat, text));
    }
}
