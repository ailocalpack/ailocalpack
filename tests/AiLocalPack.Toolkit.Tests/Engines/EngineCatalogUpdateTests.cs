using AiLocalPack.Core.Games;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Engines;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Toolkit.Tests.Engines;

public sealed class EngineCatalogUpdateTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly GameCatalog _catalog;
    private readonly EngineDetector _detector = new();

    public EngineCatalogUpdateTests()
    {
        _catalog = new GameCatalog(AppPaths.UnderHome(_dir.Combine("home")), NullLogger<GameCatalog>.Instance, new FakeClock());
        _catalog.Load();
    }

    public void Dispose() => _dir.Dispose();

    private static readonly GameId Id = GameId.Fingerprint(new string('a', 64));

    private Game AddGame(string folder, string? engine = null, bool available = true)
    {
        var game = new Game(Id, "Novel", GameSource.Manual, _dir.Combine(folder), null, null, engine, null, null, available);
        _catalog.AddManual(game);
        return game;
    }

    [Fact]
    public void Game_without_engine_gets_the_detected_engine_id()
    {
        // Arrange
        _dir.RpgMakerMz("Novel");
        AddGame("Novel");

        // Act
        _detector.UpdateCatalog(_catalog, []);

        // Assert
        _catalog.Find(Id)!.Engine.Should().Be("rpgmaker:mz");
    }

    [Fact]
    public void Game_without_known_engine_is_stored_as_unknown()
    {
        // Arrange
        _dir.Dir("Novel");
        AddGame("Novel");

        // Act
        _detector.UpdateCatalog(_catalog, []);

        // Assert
        _catalog.Find(Id)!.Engine.Should().Be("unknown");
    }

    [Fact]
    public void Game_with_a_stored_engine_is_not_checked_again()
    {
        // Arrange
        _dir.RpgMakerMz("Novel");
        AddGame("Novel", engine: "renpy");

        // Act
        var checkedGames = _detector.UpdateCatalog(_catalog, []);

        // Assert
        (checkedGames, _catalog.Find(Id)!.Engine).Should().Be((0, "renpy"));
    }

    [Fact]
    public void Updated_game_is_checked_again()
    {
        // Arrange
        _dir.RpgMakerMz("Novel");
        var game = AddGame("Novel", engine: "renpy");

        // Act
        _detector.UpdateCatalog(_catalog, [new GameVersionChanged(game, GameVersion.FromBuildId("1"))]);

        // Assert
        _catalog.Find(Id)!.Engine.Should().Be("rpgmaker:mz");
    }

    [Fact]
    public void Unavailable_game_is_not_checked()
    {
        // Arrange
        AddGame("Unplugged", available: false);

        // Act
        _detector.UpdateCatalog(_catalog, []);

        // Assert
        _catalog.Find(Id)!.Engine.Should().BeNull();
    }
}
