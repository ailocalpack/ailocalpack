using AiLocalPack.Core.Games;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Install;

/// <summary>The installed packs file (task 27).</summary>
public sealed class InstalledPackStoreTests : IDisposable
{
    private readonly TempDir _home = new();
    private readonly AppPaths _paths;

    public InstalledPackStoreTests() => _paths = AppPaths.UnderHome(_home.Path);

    public void Dispose() => _home.Dispose();

    private static InstalledPack Pack(GameId? game = null) => new(
        game ?? GameId.Steam(620),
        "ru",
        "full",
        3,
        new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero),
        "/games/Portal 2",
        "20261106-120000-abcdefgh",
        [
            new InstalledFile("data/a.json", InstallFixture.Sha("a"), InstallFixture.Sha("a ru")),
            new InstalledFile("data/new.json", null, InstallFixture.Sha("new")),
        ],
        ["fonts"]);

    [Fact]
    public void Saved_record_reads_back_in_a_new_store()
    {
        // Arrange
        new InstalledPackStore(_paths).Put(Pack());

        // Act
        var read = new InstalledPackStore(_paths).Find(GameId.Steam(620));

        // Assert
        read.Should().BeEquivalentTo(Pack());
    }

    [Fact]
    public void Removed_record_is_gone_in_a_new_store()
    {
        // Arrange
        var store = new InstalledPackStore(_paths);
        store.Put(Pack());
        store.Put(Pack(GameId.Steam(10)));
        store.Remove(GameId.Steam(620));

        // Act
        var all = new InstalledPackStore(_paths).All;

        // Assert
        all.Select(p => p.Game).Should().Equal(GameId.Steam(10));
    }

    [Fact]
    public void Corrupted_file_is_moved_aside_and_reads_empty()
    {
        // Arrange
        Directory.CreateDirectory(_paths.Data);
        File.WriteAllText(_paths.InstalledPacksFile, "{ not json");

        // Act
        var all = new InstalledPackStore(_paths).All;

        // Assert
        new { Count = all.Count, Moved = Directory.EnumerateFiles(_paths.Data, "installed-packs.json.*.bak").Count() }
            .Should().BeEquivalentTo(new { Count = 0, Moved = 1 });
    }

    [Fact]
    public void Entry_with_a_path_leaving_the_game_folder_is_skipped()
    {
        // Arrange
        new InstalledPackStore(_paths).Put(Pack());
        File.WriteAllText(_paths.InstalledPacksFile, File.ReadAllText(_paths.InstalledPacksFile).Replace("data/a.json", "../a.json", StringComparison.Ordinal));

        // Act
        var all = new InstalledPackStore(_paths).All;

        // Assert
        all.Should().BeEmpty();
    }

    [Fact]
    public void Newer_file_is_read_but_never_written()
    {
        // Arrange
        new InstalledPackStore(_paths).Put(Pack());
        File.WriteAllText(_paths.InstalledPacksFile, File.ReadAllText(_paths.InstalledPacksFile).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal));
        var store = new InstalledPackStore(_paths);

        // Act
        var act = () => store.Put(Pack(GameId.Steam(10)));

        // Assert
        act.Should().Throw<IOException>();
    }
}
