using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.ViewModels.Settings;
using AiLocalPack.Desktop.ViewModels.Settings.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Settings;

public sealed class StorageSettingsSectionTests : IDisposable
{
    private readonly ServiceProvider _services = TestServices.Build();
    private readonly TempDir _elsewhere = new();

    public void Dispose()
    {
        _services.Dispose();
        _elsewhere.Dispose();
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private AppPaths Paths => Get<AppPaths>();

    /// <summary>The section with its first measure in.</summary>
    private async Task<StorageSettingsSection> SectionAsync()
    {
        var section = Get<StorageSettingsSection>();
        await section.RefreshAsync();
        return section;
    }

    private void File(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
    }

    /// <summary>A backup of one file for <paramref name="game"/>.</summary>
    private void Backup(GameId game, int bytes) =>
        File(Path.Combine(Paths.Backups, RegistryLayout.GameFolder(game), "20261101-120000-abcdefgh", "files", "data.json"), bytes);

    /// <summary>A job folder of the Sample game with a working copy of <paramref name="bytes"/> and its job in <paramref name="status"/>.</summary>
    private string Job(GenerationJobStatus status, int bytes = 2048, string id = "20261101-100000-aaaaaa")
    {
        var folder = Path.Combine(Paths.WorkingCopies, "steam-620", id);
        File(Path.Combine(folder, "game", "data.bin"), bytes);
        GenerationJobStore.Write(folder, new GenerationJob
        {
            Id = id,
            Game = GameId.Steam(620),
            GameName = "Sample",
            Language = "ru",
            Agent = "fake",
            Stage = status == GenerationJobStatus.Completed ? GenerationStage.Done : GenerationStage.Translate,
            Status = status,
            PauseReason = status == GenerationJobStatus.Paused ? GenerationPauseReason.Player : null,
            Created = DateTimeOffset.UnixEpoch,
            Updated = DateTimeOffset.UnixEpoch,
        });
        return folder;
    }

    private ConfirmDialogViewModel Confirmation() => Get<IDialogService>().Current.Should().BeOfType<ConfirmDialogViewModel>().Subject;

    [Fact]
    public async Task Summary_shows_the_total_and_each_part()
    {
        // Arrange
        Backup(GameId.Steam(620), 1024 * 1024);
        Job(GenerationJobStatus.Completed, 2 * 1024 * 1024);
        File(Path.Combine(Paths.Packs, "x", "manifest.json"), 1024 * 1024);

        // Act
        var section = await SectionAsync();

        // Assert
        (section.Summary.Label, section.Summary.BackupsLegend, section.Summary.WorkingCopiesLegend, section.Summary.CacheLegend, section.Hint)
            .Should().Be(("4 MB used", "Backups 1 MB", "Working copies 2 MB", "Cache 1 MB", "4 MB"));
    }

    [Fact]
    public async Task Summary_bar_shares_add_up_to_the_whole()
    {
        // Arrange
        Backup(GameId.Steam(620), 3000);
        File(Path.Combine(Paths.Packs, "x", "manifest.json"), 1000);

        // Act
        var section = await SectionAsync();

        // Assert
        (section.Summary.BackupsShare, section.Summary.WorkingCopiesShare, section.Summary.CacheShare).Should().Be((0.75, 0d, 0.25));
    }

    [Fact]
    public async Task Backups_row_shows_size_games_and_folder()
    {
        // Arrange
        Backup(GameId.Steam(620), 1024 * 1024);
        Backup(GameId.Steam(700), 1024 * 1024);

        // Act
        var section = await SectionAsync();

        // Assert
        (section.Backups.Help, section.Backups.Folder).Should().Be(("2 MB · 2 games", Paths.Backups));
    }

    [Fact]
    public async Task Backups_row_says_when_nothing_is_backed_up()
    {
        // Arrange

        // Act
        var section = await SectionAsync();

        // Assert
        (section.Backups.Help, section.Backups.OpenCommand.CanExecute(null)).Should().Be(("Nothing backed up yet", false));
    }

    [Fact]
    public async Task Open_opens_the_backups_folder()
    {
        // Arrange
        Backup(GameId.Steam(620), 10);
        var section = await SectionAsync();

        // Act
        section.Backups.OpenCommand.Execute(null);

        // Assert
        Get<FakeShell>().OpenedFolders.Should().Equal(Paths.Backups);
    }

    [Fact]
    public async Task Change_location_asks_before_moving_and_names_the_new_folder()
    {
        // Arrange
        var section = await SectionAsync();
        Get<FakeFilePicker>().Folder = _elsewhere.Path;

        // Act
        await section.ChangeBackupsLocationAsync();

        // Assert
        Confirmation().Message.Should().Contain(BackupLocation.TargetFor(_elsewhere.Path));
    }

    [Fact]
    public async Task Change_location_cancelled_in_the_picker_does_nothing()
    {
        // Arrange
        var section = await SectionAsync();
        Get<FakeFilePicker>().Folder = null;

        // Act
        await section.ChangeBackupsLocationAsync();

        // Assert
        Get<IDialogService>().Current.Should().BeNull();
    }

    [Fact]
    public async Task Moving_the_backups_shows_the_new_folder()
    {
        // Arrange
        Backup(GameId.Steam(620), 1024);
        var section = await SectionAsync();

        // Act
        await section.MoveBackupsAsync(_elsewhere.Path);

        // Assert
        (section.Backups.Folder, section.Backups.Help, section.Backups.IsMoving, section.Backups.HasError, Get<BackupLocation>().Root)
            .Should().Be((BackupLocation.TargetFor(_elsewhere.Path), "1 KB · 1 game", false, false, BackupLocation.TargetFor(_elsewhere.Path)));
    }

    [Fact]
    public async Task A_refused_move_says_why_under_the_row()
    {
        // Arrange
        Backup(GameId.Steam(620), 1024);
        _elsewhere.File($"{BackupLocation.FolderName}/mine.txt", "mine");
        var section = await SectionAsync();

        // Act
        await section.MoveBackupsAsync(_elsewhere.Path);

        // Assert
        (section.Backups.Error, section.Backups.Folder).Should().Be(
            ($"{BackupLocation.TargetFor(_elsewhere.Path)} already holds files. Pick another folder; nothing was moved.", Paths.Backups));
    }

    [Fact]
    public async Task Working_copies_are_listed_with_size_and_state()
    {
        // Arrange
        Job(GenerationJobStatus.Paused, 2048);

        // Act
        var section = await SectionAsync();

        // Assert
        section.WorkingCopies.Select(r => (r.Label, r.Help, r.LosesProgress)).Should().Equal(("Sample", "2 KB · Generation paused", true));
    }

    [Fact]
    public async Task Without_working_copies_the_group_says_so()
    {
        // Arrange

        // Act
        var section = await SectionAsync();

        // Assert
        section.Groups[2].Rows.Should().Equal(section.NoWorkingCopies);
    }

    [Fact]
    public async Task A_copy_generating_now_cannot_be_deleted()
    {
        // Arrange
        Job(GenerationJobStatus.Running);
        var section = await SectionAsync();
        var row = section.WorkingCopies.Single();

        // Act
        section.DeleteWorkingCopy(row);

        // Assert
        (row.CanDelete, row.DeleteCommand.CanExecute(null), Get<IDialogService>().Current).Should().Be((false, false, (DialogViewModel?)null));
    }

    [Fact]
    public async Task Deleting_a_paused_copy_warns_that_progress_is_lost()
    {
        // Arrange
        Job(GenerationJobStatus.Paused);
        var section = await SectionAsync();

        // Act
        section.DeleteWorkingCopy(section.WorkingCopies.Single());

        // Assert
        Confirmation().Message.Should().Contain("progress is lost");
    }

    [Fact]
    public async Task Deleting_a_finished_copy_does_not_warn_about_progress()
    {
        // Arrange
        Job(GenerationJobStatus.Completed);
        var section = await SectionAsync();

        // Act
        section.DeleteWorkingCopy(section.WorkingCopies.Single());

        // Assert
        Confirmation().Message.Should().NotContain("progress");
    }

    [Fact]
    public async Task Deleting_a_paused_copy_cancels_the_job_and_frees_the_folder()
    {
        // Arrange
        var folder = Job(GenerationJobStatus.Paused);
        var section = await SectionAsync();

        // Act
        await section.DeleteConfirmedAsync(section.WorkingCopies.Single());

        // Assert
        (Directory.Exists(folder), Get<GenerationService>().FindOpen(GameId.Steam(620)), section.WorkingCopies.Count).Should().Be((false, (GenerationJob?)null, 0));
    }

    [Fact]
    public async Task Deleting_a_finished_copy_frees_the_folder()
    {
        // Arrange
        var folder = Job(GenerationJobStatus.Completed);
        var section = await SectionAsync();

        // Act
        await section.DeleteConfirmedAsync(section.WorkingCopies.Single());

        // Assert
        (Directory.Exists(folder), section.WorkingCopies.Count).Should().Be((false, 0));
    }

    [Fact]
    public async Task Cache_row_shows_its_size()
    {
        // Arrange
        File(Path.Combine(Paths.Packs, "x", "manifest.json"), 1024);

        // Act
        var section = await SectionAsync();

        // Assert
        (section.Cache.Help, section.Cache.ClearCommand.CanExecute(null)).Should().Be(("1 KB. Downloaded again when needed.", true));
    }

    [Fact]
    public async Task Clearing_the_cache_says_what_it_freed()
    {
        // Arrange
        File(Path.Combine(Paths.Packs, "x", "manifest.json"), 2048);
        var section = await SectionAsync();

        // Act
        await section.ClearCacheAsync();

        // Assert
        (section.Cache.Result, section.Cache.ResultIsError, section.Cache.Bytes, Directory.Exists(Paths.Packs)).Should().Be(("Cleared: 2 KB freed.", false, 0L, false));
    }

    [Fact]
    public async Task Clearing_the_cache_while_an_install_runs_says_to_wait()
    {
        // Arrange
        File(Path.Combine(Paths.Packs, "x", "manifest.json"), 2048);
        var section = await SectionAsync();
        Directory.CreateDirectory(Paths.Data);
        using var held = new FileStream(Paths.InstallLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        // Act
        await section.ClearCacheAsync();

        // Assert
        (section.Cache.ResultIsError, Directory.Exists(Paths.Packs)).Should().Be((true, true));
    }

    [Fact]
    public async Task Section_is_registered_in_its_place_in_the_list()
    {
        // Arrange
        var section = await SectionAsync();

        // Act
        var sections = _services.GetServices<SettingsSection>().ToList();

        // Assert
        sections.IndexOf(section).Should().Be((int)SettingsSectionId.Storage);
    }
}
