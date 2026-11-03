using AiLocalPack.Core.Games;
using AiLocalPack.Core.Steam;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Toolkit.AntiCheat;
using AiLocalPack.Toolkit.Engines;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Desktop.Tests.Library;

public sealed class GameLibraryTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly SteamFixture _steam;

    public GameLibraryTests() => _steam = new SteamFixture(_dir);

    public void Dispose() => _dir.Dispose();

    private GameLibrary Create(string? steamRoot = null)
    {
        var root = steamRoot ?? _steam.Root;
        var env = new OsEnvironment(OsEnvironment.Current.Os, _dir.Dir("user").Combine("user"), n => n == SteamLocator.RootVariable ? root : null);
        var catalog = new GameCatalog(AppPaths.UnderHome(_dir.Combine("app")), NullLogger<GameCatalog>.Instance, new FakeClock());
        return new GameLibrary(
            catalog,
            new SteamLibraryScanner(new SteamLocator(env)),
            new EngineDetector(),
            new AntiCheatDetector(),
            NullLogger<GameLibrary>.Instance);
    }

    [Fact]
    public void Starts_not_scanned()
    {
        // Arrange
        var library = Create();

        // Act
        var state = (library.IsScanning, library.HasScanned);

        // Assert
        state.Should().Be((false, false));
    }

    [Fact]
    public void Refresh_puts_steam_games_into_the_catalog()
    {
        // Arrange
        _steam.AddGame(10, "Alpha").AddGame(20, "Beta");
        var library = Create();

        // Act
        library.Refresh();

        // Assert
        library.Catalog.Games.Select(g => g.Name).Should().Equal("Alpha", "Beta");
        library.HasScanned.Should().BeTrue();
        library.IsScanning.Should().BeFalse();
    }

    [Fact]
    public void Refresh_detects_engine_and_anti_cheat()
    {
        // Arrange
        _steam.AddGame(10, "Alpha");
        _dir.RenPy("Steam/steamapps/common/Alpha").File("Steam/steamapps/common/Alpha/EasyAntiCheat/settings.json", "{}");
        var library = Create();

        // Act
        library.Refresh();

        // Assert
        var game = library.Catalog.Games.Single();
        game.Engine.Should().Be("renpy");
        game.AntiCheat.Should().Be("eac");
    }

    [Fact]
    public void Refresh_without_steam_finishes_with_an_empty_catalog()
    {
        // Arrange
        var library = Create(steamRoot: _dir.Combine("missing"));

        // Act
        library.Refresh();

        // Assert
        library.Catalog.Games.Should().BeEmpty();
        library.HasScanned.Should().BeTrue();
    }

    [Fact]
    public void Refresh_reports_scanning_while_it_runs()
    {
        // Arrange
        var library = Create();
        var seen = new List<(bool Scanning, bool Scanned)>();
        library.StateChanged += (_, _) => seen.Add((library.IsScanning, library.HasScanned));

        // Act
        library.Refresh();

        // Assert
        seen.Should().Equal((true, false), (false, true));
    }

    [Fact]
    public async Task Refresh_async_runs_the_scan_in_the_background()
    {
        // Arrange
        _steam.AddGame(10, "Alpha");
        var library = Create();

        // Act
        await library.RefreshAsync();

        // Assert
        library.Catalog.Games.Should().ContainSingle(g => g.Name == "Alpha");
    }
}
