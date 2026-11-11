using AiLocalPack.Core.Games;
using AiLocalPack.Core.Settings;
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

    private readonly FakeClock _clock = new();
    private SettingsStore? _settings;

    private GameLibrary Create(string? steamRoot = null)
    {
        var root = steamRoot ?? _steam.Root;
        var env = new OsEnvironment(OsEnvironment.Current.Os, _dir.Dir("user").Combine("user"), n => n == SteamLocator.RootVariable ? root : null);
        var paths = AppPaths.UnderHome(_dir.Combine("app"));
        var catalog = new GameCatalog(paths, NullLogger<GameCatalog>.Instance, _clock);
        _settings = new SettingsStore(paths, NullLogger<SettingsStore>.Instance, _clock);
        return new GameLibrary(
            catalog,
            new SteamLibraryScanner(new SteamLocator(env)),
            new EngineDetector(),
            new AntiCheatDetector(),
            null,
            _settings,
            _clock,
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
    public void Refresh_reports_the_scan_then_the_detection()
    {
        // Arrange
        _steam.AddGame(10, "Alpha").AddGame(20, "Beta");
        var library = Create();
        var progress = new List<GameLibraryProgress>();
        library.Progressed += (_, p) => progress.Add(p);

        // Act
        library.Refresh();

        // Assert
        progress.Should().HaveCount(2);
        progress[0].Should().BeOfType<GameLibraryScanned>().Which.Changes.Should().HaveCount(2);
        progress[1].Should().Be(new GameLibraryDetected(2));
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

    [Fact]
    public void Refresh_remembers_the_scan_and_when_it_finished()
    {
        // Arrange
        var library = Create();

        // Act
        library.Refresh();

        // Assert
        library.LastScan!.Libraries.Select(l => l.Path).Should().Equal(_steam.Root);
        library.LastScanAt.Should().Be(_clock.GetUtcNow());
    }

    [Fact]
    public void Refresh_scans_library_folders_added_in_settings()
    {
        // Arrange
        var extra = _dir.Dir("Extra/steamapps").Combine("Extra");
        _steam.AddGame(30, "Gamma", library: extra);
        var library = Create();
        _settings!.Update(s => s with { SteamLibraryFolders = [extra] });

        // Act
        library.Refresh();

        // Assert
        library.Catalog.Games.Should().ContainSingle(g => g.Name == "Gamma" && g.LibraryPath == extra);
        library.LastScan!.Libraries.Should().Contain(new SteamLibrary(extra, true, IsAddedByHand: true));
    }

    [Fact]
    public void Refresh_leaves_out_library_folders_removed_in_settings()
    {
        // Arrange
        var card = _steam.AddLibrary("Card", create: false);
        var library = Create();
        _settings!.Update(s => s with { IgnoredSteamLibraries = [card] });

        // Act
        library.Refresh();

        // Assert
        library.LastScan!.Libraries.Select(l => l.Path).Should().Equal(_steam.Root);
    }

    [Fact]
    public async Task Refresh_requested_while_one_runs_runs_again_afterwards()
    {
        // Arrange
        var library = Create();
        var scans = 0;
        library.StateChanged += (_, _) =>
        {
            if (!library.IsScanning)
            {
                Interlocked.Increment(ref scans);
            }
        };
        var first = library.RefreshAsync();

        // Act
        var second = library.RefreshAsync();
        await Task.WhenAll(first, second);

        // Assert
        second.Should().NotBeSameAs(first);
        scans.Should().Be(2);
    }
}
