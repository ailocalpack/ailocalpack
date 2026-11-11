using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Storage;

public sealed class StorageUsageTests : IDisposable
{
    private readonly TempDir _home = new();

    public void Dispose() => _home.Dispose();

    private AppPaths Paths => AppPaths.UnderHome(_home.Path);

    private static GenerationJob Job(string id) => new()
    {
        Id = id,
        Game = GameId.Steam(620),
        GameName = "Sample",
        Language = "ru",
        Agent = "claude",
        Status = GenerationJobStatus.Paused,
        PauseReason = GenerationPauseReason.Player,
        Created = DateTimeOffset.UnixEpoch,
        Updated = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void Folder_size_sums_the_files_every_level_down()
    {
        // Arrange
        _home.File("a/one.bin", new byte[10]).File("a/b/two.bin", new byte[25]).File("a/b/c/three.bin", new byte[7]);

        // Act
        var size = FolderBytes.Of(_home.Combine("a"), TestContext.Current.CancellationToken);

        // Assert
        size.Should().Be(42);
    }

    [Fact]
    public void Folder_size_of_a_missing_folder_is_zero()
    {
        // Arrange
        var missing = _home.Combine("missing");

        // Act
        var size = FolderBytes.Of(missing, TestContext.Current.CancellationToken);

        // Assert
        size.Should().Be(0);
    }

    [Fact]
    public void Folder_size_does_not_follow_folder_links()
    {
        // Arrange
        _home.File("big/game.bin", new byte[1000]).File("copy/own.bin", new byte[5]);
        Directory.CreateSymbolicLink(_home.Combine("copy/link"), _home.Combine("big"));

        // Act
        var size = FolderBytes.Of(_home.Combine("copy"), TestContext.Current.CancellationToken);

        // Assert
        size.Should().Be(5);
    }

    [Fact]
    public void Measure_counts_backups_and_the_games_that_have_one()
    {
        // Arrange
        _home.File("data/backups/steam-620/20261101-120000-abc/files/a.json", new byte[100])
            .File("data/backups/steam-700/20261101-120000-def/files/b.json", new byte[50])
            .Dir("data/backups/steam-800");

        // Act
        var usage = StorageUsage.Measure(Paths, TestContext.Current.CancellationToken);

        // Assert
        (usage.BackupsFolder, usage.BackupBytes, usage.BackupGames).Should().Be((Paths.Backups, 150L, 2));
    }

    [Fact]
    public void Measure_lists_each_job_folder_with_its_job_and_size_newest_first()
    {
        // Arrange
        var older = _home.Combine("data/work/steam-620/20261101-100000-aaaaaa");
        var newer = _home.Combine("data/work/steam-620/20261102-100000-bbbbbb");
        _home.File("data/work/steam-620/20261101-100000-aaaaaa/game/a.bin", new byte[30]);
        GenerationJobStore.Write(older, Job("20261101-100000-aaaaaa"));
        _home.File("data/work/steam-620/20261102-100000-bbbbbb/game/b.bin", new byte[70]);

        // Act
        var usage = StorageUsage.Measure(Paths, TestContext.Current.CancellationToken);

        // Assert
        usage.WorkingCopies.Select(w => (w.Folder, w.GameFolder, w.Job?.Id, w.Bytes > 0)).Should().Equal(
            (newer, "steam-620", (string?)null, true),
            (older, "steam-620", "20261101-100000-aaaaaa", true));
    }

    [Fact]
    public void Measure_counts_the_whole_cache()
    {
        // Arrange
        _home.File("cache/packs/x/manifest.json", new byte[11]).File("cache/file-hashes.json", new byte[4]);

        // Act
        var usage = StorageUsage.Measure(Paths, TestContext.Current.CancellationToken);

        // Assert
        (usage.CacheBytes, usage.TotalBytes).Should().Be((15L, 15L));
    }

    [Fact]
    public void Measure_of_a_fresh_home_is_empty()
    {
        // Arrange
        var paths = Paths;

        // Act
        var usage = StorageUsage.Measure(paths, TestContext.Current.CancellationToken);

        // Assert
        (usage.TotalBytes, usage.BackupGames, usage.WorkingCopies.Count).Should().Be((0L, 0, 0));
    }
}
