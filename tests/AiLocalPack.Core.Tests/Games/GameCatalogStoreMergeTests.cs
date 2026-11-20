using AiLocalPack.Core.Games;
using AiLocalPack.Core.Steam;
using AiLocalPack.Core.Stores;

namespace AiLocalPack.Core.Tests.Games;

/// <summary>Merging the scans of stores beside Steam (task 83).</summary>
public sealed class GameCatalogStoreMergeTests
{
    private static readonly string Library = OperatingSystem.IsWindows() ? @"C:\Program Files\Epic Games" : "/games/epic";
    private static readonly string ManualPath = Path.Combine(Library, "Lanternkeepers");

    private static StoreGame Epic(string appName, string name, string? version = "1.0", bool available = true) =>
        new(GameId.Store(GameIdKind.Epic, appName)!, name, Path.Combine(Library, name), Library, version, "Game.exe", StoreApps.EpicLauncher, available);

    private static StoreScan Scan(params StoreGame[] games) => new(GameSource.Epic, true, Library, games);

    private static Game Stored(StoreGame game) => new(
        game.Id, game.Name, GameSource.Epic, game.InstallPath, game.LibraryPath,
        game.Version is null ? null : GameVersion.FromBuildId(game.Version), null, null, null, game.IsAvailable, game.MainExecutable, null, game.App);

    private static Game SteamGame() => new(
        GameId.Steam(413150), "Stardew Valley", GameSource.Steam, "/steam/steamapps/common/Stardew Valley", "/steam",
        GameVersion.FromBuildId("100"), null, null, null, true);

    private static (IReadOnlyList<Game> Games, IReadOnlyList<GameChange> Changes) Merge(
        IEnumerable<Game> stored,
        StoreScan? epic,
        SteamScan? steam = null,
        params GameSource[] disabled) =>
        GameCatalogMerge.Merge(stored, new StoreScans(steam, epic is null ? [] : [epic], disabled.ToHashSet()), _ => true);

    [Fact]
    public void A_new_store_game_is_added_with_its_store_details()
    {
        // Arrange
        var game = Epic("Fennel", "Lanternkeepers");

        // Act
        var (games, changes) = Merge([], Scan(game));

        // Assert
        changes.Should().Equal(new GameAdded(Stored(game)));
    }

    [Fact]
    public void A_store_game_keeps_its_engine_and_anti_cheat_on_refresh()
    {
        // Arrange
        var game = Epic("Fennel", "Lanternkeepers");
        var stored = Stored(game) with { Engine = "unity:mono", AntiCheat = "none" };

        // Act
        var (games, _) = Merge([stored], Scan(game with { Name = "Lanternkeepers Remastered" }));

        // Assert
        games.Single().Should().Be(stored with { Name = "Lanternkeepers Remastered" });
    }

    [Fact]
    public void A_new_store_version_is_a_version_change()
    {
        // Arrange
        var stored = Stored(Epic("Fennel", "Lanternkeepers", "1.0"));

        // Act
        var (_, changes) = Merge([stored], Scan(Epic("Fennel", "Lanternkeepers", "1.1")));

        // Assert
        changes.Should().ContainSingle().Which.Should().BeOfType<GameVersionChanged>().Which.Previous.Should().Be(GameVersion.FromBuildId("1.0"));
    }

    [Fact]
    public void A_store_game_missing_from_a_read_scan_was_uninstalled()
    {
        // Arrange
        var stored = Stored(Epic("Fennel", "Lanternkeepers"));

        // Act
        var (games, changes) = Merge([stored], Scan());

        // Assert
        (games.Count, changes.Single()).Should().Be((0, new GameRemoved(stored)));
    }

    [Fact]
    public void A_store_game_stays_unavailable_while_the_store_cant_be_read()
    {
        // Arrange
        var stored = Stored(Epic("Fennel", "Lanternkeepers"));

        // Act
        var (games, _) = Merge([stored], Scan() with { Problem = "locked" });

        // Assert
        games.Should().Equal(stored with { IsAvailable = false });
    }

    [Fact]
    public void A_store_game_in_a_missing_folder_stays_unavailable()
    {
        // Arrange
        var stored = Stored(Epic("Fennel", "Lanternkeepers"));

        // Act
        var (games, _) = Merge([stored], Scan() with { MissingFolders = [stored.InstallPath] });

        // Assert
        games.Should().Equal(stored with { IsAvailable = false });
    }

    [Fact]
    public void A_store_without_a_scan_keeps_its_games()
    {
        // Arrange
        var stored = Stored(Epic("Fennel", "Lanternkeepers"));

        // Act
        var (games, changes) = Merge([stored], epic: null);

        // Assert
        (games.Single(), changes.Count).Should().Be((stored, 0));
    }

    [Fact]
    public void A_store_switched_off_loses_its_games()
    {
        // Arrange
        var stored = Stored(Epic("Fennel", "Lanternkeepers"));

        // Act
        var (games, changes) = Merge([stored], Scan(Epic("Fennel", "Lanternkeepers")), null, GameSource.Epic);

        // Assert
        (games.Count, changes.Single()).Should().Be((0, new GameRemoved(stored)));
    }

    [Fact]
    public void Steam_switched_off_loses_its_games_and_adds_none()
    {
        // Arrange
        var stored = SteamGame();
        var scan = new SteamScan([new SteamLibrary("/steam", true)], [new SteamGame(620, "Portal 2", "1", "/steam/steamapps/common/Portal 2", "/steam", null)]);

        // Act
        var (games, changes) = Merge([stored], null, scan, GameSource.Steam);

        // Assert
        (games.Count, changes.Single()).Should().Be((0, new GameRemoved(stored)));
    }

    [Fact]
    public void A_store_game_in_a_manual_games_folder_is_left_out()
    {
        // Arrange
        var manual = new Game(GameId.Fingerprint(new string('c', 64)), "Lanternkeepers", GameSource.Manual, ManualPath, null, null, null, null, null, true);

        // Act
        var (games, changes) = Merge([manual], Scan(Epic("Fennel", "Lanternkeepers")));

        // Assert
        (games.Single(), changes.Count).Should().Be((manual, 0));
    }

    [Fact]
    public void A_scan_entry_of_another_store_is_ignored()
    {
        // Arrange
        var gog = new StoreGame(GameId.Store(GameIdKind.Gog, "42")!, "Saltmarsh", "/games/gog/Saltmarsh", "/games/gog", null, null, StoreApps.GogGalaxy);

        // Act
        var (games, _) = Merge([], Scan(gog));

        // Assert
        games.Should().BeEmpty();
    }
}
