using AiLocalPack.Core.Games;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.AntiCheat;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Toolkit.Tests.AntiCheat;

public sealed class AntiCheatCatalogUpdateTests : IDisposable
{
    private static readonly GameId Id = GameId.Fingerprint(new string('b', 64));

    private readonly TempDir _dir = new();
    private readonly GameCatalog _catalog;
    private readonly AntiCheatDetector _detector = new();

    public AntiCheatCatalogUpdateTests()
    {
        _catalog = new GameCatalog(AppPaths.UnderHome(_dir.Combine("home")), NullLogger<GameCatalog>.Instance, new FakeClock());
        _catalog.Load();
    }

    public void Dispose() => _dir.Dispose();

    private Game AddGame(string folder, string? antiCheat = null, bool available = true)
    {
        var game = new Game(Id, "Shooter", GameSource.Manual, _dir.Combine(folder), null, null, null, antiCheat, null, available);
        _catalog.AddManual(game);
        return game;
    }

    [Fact]
    public void Unchecked_game_gets_the_detected_anti_cheat_id()
    {
        // Arrange
        _dir.Dir("Shooter/EasyAntiCheat");
        AddGame("Shooter");

        // Act
        _detector.UpdateCatalog(_catalog, []);

        // Assert
        _catalog.Find(Id)!.AntiCheat.Should().Be("eac");
    }

    [Fact]
    public void Game_without_anti_cheat_is_stored_as_none()
    {
        // Arrange
        _dir.Dir("Shooter");
        AddGame("Shooter");

        // Act
        _detector.UpdateCatalog(_catalog, []);

        // Assert
        _catalog.Find(Id)!.AntiCheat.Should().Be("none");
    }

    [Fact]
    public void Checked_game_is_not_checked_again()
    {
        // Arrange
        _dir.Dir("Shooter/EasyAntiCheat");
        AddGame("Shooter", antiCheat: "none");

        // Act
        var checkedGames = _detector.UpdateCatalog(_catalog, []);

        // Assert
        (checkedGames, _catalog.Find(Id)!.AntiCheat).Should().Be((0, "none"));
    }

    [Fact]
    public void Updated_game_is_checked_again()
    {
        // Arrange
        _dir.Dir("Shooter/BattlEye");
        var game = AddGame("Shooter", antiCheat: "none");

        // Act
        _detector.UpdateCatalog(_catalog, [new GameVersionChanged(game, GameVersion.FromBuildId("1"))]);

        // Assert
        _catalog.Find(Id)!.AntiCheat.Should().Be("battleye");
    }

    [Fact]
    public void Unavailable_game_is_not_checked()
    {
        // Arrange
        AddGame("Unplugged", available: false);

        // Act
        _detector.UpdateCatalog(_catalog, []);

        // Assert
        _catalog.Find(Id)!.AntiCheat.Should().BeNull();
    }
}
