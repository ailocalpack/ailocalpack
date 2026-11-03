using AiLocalPack.Core.Games;
using AiLocalPack.Core.Steam;

namespace AiLocalPack.Core.Tests.Games;

public sealed class GameCatalogMergeTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\Steam" : "/steam";
    private static readonly string SdCard = OperatingSystem.IsWindows() ? @"E:\SteamLibrary" : "/media/sd/SteamLibrary";
    private static readonly string ManualPath = OperatingSystem.IsWindows() ? @"D:\Games\Moss" : "/games/moss";
    private static readonly DateTimeOffset Played = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static SteamGame Steam(uint appId, string name, string? build = "100", string? library = null) =>
        new(appId, name, build, Path.Combine(library ?? Root, "steamapps", "common", name), library ?? Root, null);

    private static SteamScan Scan(params SteamGame[] games) => new([new SteamLibrary(Root, true)], games);

    private static Game Stored(SteamGame steam) => new(
        GameId.Steam(steam.AppId),
        steam.Name,
        GameSource.Steam,
        steam.InstallPath,
        steam.LibraryPath,
        steam.BuildId is null ? null : GameVersion.FromBuildId(steam.BuildId),
        null,
        null,
        null,
        true);

    private static Game Manual(string name = "Moss and Mortar") => new(
        GameId.Fingerprint(new string('c', 64)),
        name,
        GameSource.Manual,
        ManualPath,
        null,
        null,
        "renpy",
        null,
        null,
        true);

    private static (IReadOnlyList<Game> Games, IReadOnlyList<GameChange> Changes) Merge(
        IEnumerable<Game> stored,
        SteamScan scan,
        bool manualFolderExists = true) =>
        GameCatalogMerge.Merge(stored, scan, _ => manualFolderExists);

    [Fact]
    public void New_steam_game_is_added()
    {
        // Arrange
        var stardew = Steam(413150, "Stardew Valley");

        // Act
        var (games, changes) = Merge([], Scan(stardew));

        // Assert
        games.Should().ContainSingle().Which.Should().Be(Stored(stardew));
        changes.Should().Equal(new GameAdded(Stored(stardew)));
    }

    [Fact]
    public void Uninstalled_steam_game_is_removed()
    {
        // Arrange
        var stardew = Stored(Steam(413150, "Stardew Valley"));

        // Act
        var (games, changes) = Merge([stardew], Scan());

        // Assert
        games.Should().BeEmpty();
        changes.Should().Equal(new GameRemoved(stardew));
    }

    [Fact]
    public void Game_on_a_missing_drive_is_kept_as_unavailable()
    {
        // Arrange
        var stardew = Stored(Steam(413150, "Stardew Valley", library: SdCard)) with { Engine = "unity" };
        var scan = new SteamScan([new SteamLibrary(Root, true), new SteamLibrary(SdCard, false)], []);

        // Act
        var (games, changes) = Merge([stardew], scan);

        // Assert
        games.Should().Equal(stardew with { IsAvailable = false });
        changes.Should().BeEmpty();
    }

    [Fact]
    public void Steam_not_found_keeps_steam_games_as_unavailable()
    {
        // Arrange
        var stardew = Stored(Steam(413150, "Stardew Valley"));

        // Act
        var (games, changes) = Merge([stardew], SteamScan.Empty);

        // Assert
        games.Should().Equal(stardew with { IsAvailable = false });
        changes.Should().BeEmpty();
    }

    [Fact]
    public void Game_back_on_its_drive_is_available_again()
    {
        // Arrange
        var steam = Steam(413150, "Stardew Valley", library: SdCard);
        var stored = Stored(steam) with { IsAvailable = false };

        // Act
        var (games, _) = Merge([stored], Scan(steam));

        // Assert
        games.Should().ContainSingle().Which.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void Build_id_change_gives_version_changed()
    {
        // Arrange
        var stored = Stored(Steam(413150, "Stardew Valley", build: "100"));
        var updated = Steam(413150, "Stardew Valley", build: "200");

        // Act
        var (games, changes) = Merge([stored], Scan(updated));

        // Assert
        changes.Should().Equal(new GameVersionChanged(games.Single(), GameVersion.FromBuildId("100")));
        games.Single().Version.Should().Be(GameVersion.FromBuildId("200"));
    }

    [Fact]
    public void Same_build_id_gives_no_change()
    {
        // Arrange
        var steam = Steam(413150, "Stardew Valley");

        // Act
        var (_, changes) = Merge([Stored(steam)], Scan(steam));

        // Assert
        changes.Should().BeEmpty();
    }

    [Fact]
    public void First_known_build_id_is_not_a_version_change()
    {
        // Arrange
        var stored = Stored(Steam(413150, "Stardew Valley", build: null));

        // Act
        var (_, changes) = Merge([stored], Scan(Steam(413150, "Stardew Valley", build: "200")));

        // Assert
        changes.Should().BeEmpty();
    }

    [Fact]
    public void Lost_build_id_keeps_the_known_version()
    {
        // Arrange
        var stored = Stored(Steam(413150, "Stardew Valley", build: "100"));

        // Act
        var (games, _) = Merge([stored], Scan(Steam(413150, "Stardew Valley", build: null)));

        // Assert
        games.Single().Version.Should().Be(GameVersion.FromBuildId("100"));
    }

    [Fact]
    public void Rescanned_game_keeps_detected_engine_and_anti_cheat()
    {
        // Arrange
        var steam = Steam(1091500, "Cyberpunk 2077");
        var stored = Stored(steam) with { Engine = "redengine", AntiCheat = "eac" };

        // Act
        var (games, _) = Merge([stored], Scan(steam));

        // Assert
        games.Single().Should().Be(stored);
    }

    [Fact]
    public void Rescanned_game_takes_the_new_name_and_folder()
    {
        // Arrange
        var stored = Stored(Steam(413150, "Stardew Valley"));
        var moved = Steam(413150, "Stardew Valley 1.6", library: SdCard);

        // Act
        var (games, _) = Merge([stored], Scan(moved));

        // Assert
        games.Single().Should().Be(Stored(moved));
    }

    [Fact]
    public void Manual_game_is_kept_when_steam_has_nothing()
    {
        // Arrange
        var manual = Manual();

        // Act
        var (games, changes) = Merge([manual], Scan());

        // Assert
        games.Should().Equal(manual);
        changes.Should().BeEmpty();
    }

    [Fact]
    public void Manual_game_with_a_missing_folder_is_unavailable()
    {
        // Arrange
        var manual = Manual();

        // Act
        var (games, _) = Merge([manual], Scan(), manualFolderExists: false);

        // Assert
        games.Should().Equal(manual with { IsAvailable = false });
    }

    [Fact]
    public void Games_are_sorted_by_name()
    {
        // Arrange
        var scan = Scan(Steam(2, "beta"), Steam(1, "Alpha"));

        // Act
        var (games, _) = Merge([Manual("Gamma")], scan);

        // Assert
        games.Select(g => g.Name).Should().Equal("Alpha", "beta", "Gamma");
    }

    [Fact]
    public void Steam_not_found_but_a_folder_added_by_hand_keeps_the_other_steam_games_as_unavailable()
    {
        // Arrange
        var stardew = Stored(Steam(413150, "Stardew Valley"));
        var scan = new SteamScan([new SteamLibrary(SdCard, true, IsAddedByHand: true)], []);

        // Act
        var (games, changes) = Merge([stardew], scan);

        // Assert
        games.Should().Equal(stardew with { IsAvailable = false });
        changes.Should().BeEmpty();
    }
}
