using AiLocalPack.Core.Install;

namespace AiLocalPack.Core.Tests.Install;

/// <summary>What a game writes when it runs is undone (_Live game folder_).</summary>
public sealed class GameWriteGuardTests : IDisposable
{
    private static readonly DateTime Written = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private readonly TempDir _dir = new();

    public GameWriteGuardTests()
    {
        _dir.File("game/settings.ini", "as shipped").File("game/data/world.dat", "world");
        File.SetLastWriteTimeUtc(_dir.Combine("game/settings.ini"), Written);
    }

    public void Dispose() => _dir.Dispose();

    private string Game => _dir.Combine("game");

    private string Trial => _dir.Combine("trial");

    private GameWriteGuard Take(IEnumerable<string>? known = null, params GameWriteRoot[] data) =>
        GameWriteGuard.Take(Trial, [GameWriteRoot.Game(Game), .. data], known ?? []);

    [Fact]
    public void A_file_the_game_added_is_deleted()
    {
        // Arrange
        var guard = Take();
        _dir.File("game/cache/books.json", "built from the translation");

        // Act
        guard.Undo();

        // Assert
        new { File = File.Exists(_dir.Combine("game/cache/books.json")), Folder = Directory.Exists(_dir.Combine("game/cache")) }
            .Should().Be(new { File = false, Folder = false });
    }

    [Fact]
    public void A_small_file_the_game_changed_is_put_back_with_its_write_time()
    {
        // Arrange
        var guard = Take();
        File.WriteAllText(_dir.Combine("game/settings.ini"), "language=ru");

        // Act
        guard.Undo();

        // Assert
        new { Text = File.ReadAllText(_dir.Combine("game/settings.ini")), Time = File.GetLastWriteTimeUtc(_dir.Combine("game/settings.ini")) }
            .Should().Be(new { Text = "as shipped", Time = Written });
    }

    [Fact]
    public void A_file_the_game_deleted_is_put_back()
    {
        // Arrange
        var guard = Take();
        File.Delete(_dir.Combine("game/settings.ini"));

        // Act
        guard.Undo();

        // Assert
        File.ReadAllText(_dir.Combine("game/settings.ini")).Should().Be("as shipped");
    }

    [Fact]
    public void A_large_file_the_game_is_known_to_write_is_put_back()
    {
        // Arrange
        _dir.File("game/data/big.cache", new byte[GameWriteGuard.MaxSmallFile + 1]);
        var guard = Take(["data/big.cache"]);
        File.WriteAllText(_dir.Combine("game/data/big.cache"), "rewritten");

        // Act
        guard.Undo();

        // Assert
        new FileInfo(_dir.Combine("game/data/big.cache")).Length.Should().Be(GameWriteGuard.MaxSmallFile + 1);
    }

    [Fact]
    public void A_large_file_without_a_backup_is_listed_as_left()
    {
        // Arrange
        _dir.File("game/data/big.cache", new byte[GameWriteGuard.MaxSmallFile + 1]);
        var guard = Take();
        File.WriteAllText(_dir.Combine("game/data/big.cache"), "rewritten");

        // Act
        var undo = guard.Undo();

        // Assert
        undo.Left.Should().Equal("data/big.cache");
    }

    [Fact]
    public void A_data_folder_the_run_made_is_deleted_whole()
    {
        // Arrange
        var guard = Take(data: new GameWriteRoot("unity", _dir.Combine("LocalLow/Studio/Game")));
        _dir.File("LocalLow/Studio/Game/Player.log", "log");

        // Act
        guard.Undo();

        // Assert
        Directory.Exists(_dir.Combine("LocalLow/Studio/Game")).Should().BeFalse();
    }

    [Fact]
    public void A_file_of_a_data_folder_is_keyed_by_the_folder()
    {
        // Arrange
        _dir.File("LocalLow/Studio/Game/prefs", "a");
        var guard = Take(data: new GameWriteRoot("unity", _dir.Combine("LocalLow/Studio/Game")));
        File.WriteAllText(_dir.Combine("LocalLow/Studio/Game/prefs"), "b");

        // Act
        var undo = guard.Undo();

        // Assert
        undo.Written.Should().Equal("~unity/prefs");
    }

    [Fact]
    public void A_guard_left_by_a_crash_is_undone_from_its_folder()
    {
        // Arrange
        Take();
        File.WriteAllText(_dir.Combine("game/settings.ini"), "language=ru");

        // Act
        GameWriteGuard.Load(Trial)!.Undo();

        // Assert
        File.ReadAllText(_dir.Combine("game/settings.ini")).Should().Be("as shipped");
    }
}
