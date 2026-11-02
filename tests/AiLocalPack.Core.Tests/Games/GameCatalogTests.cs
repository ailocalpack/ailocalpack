using System.Text.Json.Nodes;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Steam;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Core.Tests.Games;

public sealed class GameCatalogTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 10, 15, 30, TimeSpan.Zero);

    private readonly TempDir _dir = new();
    private readonly TestLogger<GameCatalog> _logger = new();

    private AppPaths Paths => AppPaths.UnderHome(_dir.Path);

    public void Dispose() => _dir.Dispose();

    private GameCatalog CreateCatalog() => new(Paths, _logger, new FakeClock(Now));

    private string Library => _dir.Combine("Steam");

    private SteamScan Scan(params (uint AppId, string Name, string Build)[] games) => new(
        [new SteamLibrary(Library, true)],
        [.. games.Select(g => new SteamGame(g.AppId, g.Name, g.Build, Path.Combine(Library, "steamapps", "common", g.Name), Library, Now))]);

    private Game Manual(string fingerprintChar = "d") => new(
        GameId.Fingerprint(string.Concat(Enumerable.Repeat(fingerprintChar, 64))),
        "Moss and Mortar",
        GameSource.Manual,
        _dir.Path,
        null,
        GameVersion.FromFileHashes([new("game/script.rpa", new string('e', 64))]),
        "renpy",
        null,
        null,
        true);

    private void WriteCatalogFile(string content) => _dir.File("data/games.json", content);

    [Fact]
    public void Missing_file_gives_an_empty_catalog()
    {
        // Arrange
        var catalog = CreateCatalog();

        // Act
        var games = catalog.Load();

        // Assert
        games.Should().BeEmpty();
    }

    [Fact]
    public void Games_survive_a_restart()
    {
        // Arrange
        var first = CreateCatalog();
        first.Load();
        first.Refresh(Scan((413150, "Stardew Valley", "100")));
        first.AddManual(Manual());
        first.Update(GameId.Steam(413150), g => g with { Engine = "xna", AntiCheat = null });

        // Act
        var games = CreateCatalog().Load();

        // Assert
        games.Should().Equal(first.Games);
    }

    [Fact]
    public void Refresh_returns_and_raises_the_changes()
    {
        // Arrange
        var catalog = CreateCatalog();
        catalog.Load();
        GameCatalogChangedEventArgs? raised = null;
        catalog.Changed += (_, e) => raised = e;

        // Act
        var changes = catalog.Refresh(Scan((413150, "Stardew Valley", "100")));

        // Assert
        changes.Should().ContainSingle().Which.Should().BeOfType<GameAdded>();
        raised!.Changes.Should().Equal(changes);
    }

    [Fact]
    public void Build_change_after_a_restart_raises_version_changed()
    {
        // Arrange
        var first = CreateCatalog();
        first.Load();
        first.Refresh(Scan((413150, "Stardew Valley", "100")));
        var second = CreateCatalog();
        second.Load();

        // Act
        var changes = second.Refresh(Scan((413150, "Stardew Valley", "200")));

        // Assert
        changes.Should().ContainSingle().Which.Should().BeOfType<GameVersionChanged>()
            .Which.Previous.Should().Be(GameVersion.FromBuildId("100"));
    }

    [Fact]
    public void Unchanged_refresh_raises_no_event()
    {
        // Arrange
        var catalog = CreateCatalog();
        catalog.Load();
        catalog.Refresh(Scan((413150, "Stardew Valley", "100")));
        var raised = false;
        catalog.Changed += (_, _) => raised = true;

        // Act
        catalog.Refresh(Scan((413150, "Stardew Valley", "100")));

        // Assert
        raised.Should().BeFalse();
    }

    [Fact]
    public void Adding_a_manual_game_twice_throws()
    {
        // Arrange
        var catalog = CreateCatalog();
        catalog.Load();
        catalog.AddManual(Manual());
        var act = () => catalog.AddManual(Manual());

        // Act
        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Adding_a_steam_game_by_hand_throws()
    {
        // Arrange
        var catalog = CreateCatalog();
        catalog.Load();
        var steam = Manual() with { Id = GameId.Steam(20), Source = GameSource.Steam };
        var act = () => catalog.AddManual(steam);

        // Act
        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Removing_a_manual_game_raises_removed()
    {
        // Arrange
        var catalog = CreateCatalog();
        catalog.Load();
        var manual = catalog.AddManual(Manual()).Game;
        GameCatalogChangedEventArgs? raised = null;
        catalog.Changed += (_, e) => raised = e;

        // Act
        catalog.RemoveManual(manual.Id);

        // Assert
        raised!.Changes.Should().Equal(new GameRemoved(manual));
        catalog.Games.Should().BeEmpty();
    }

    [Fact]
    public void Removing_a_steam_game_throws()
    {
        // Arrange
        var catalog = CreateCatalog();
        catalog.Load();
        catalog.Refresh(Scan((413150, "Stardew Valley", "100")));
        var act = () => catalog.RemoveManual(GameId.Steam(413150));

        // Act
        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Removing_an_unknown_game_returns_false()
    {
        // Arrange
        var catalog = CreateCatalog();
        catalog.Load();

        // Act
        var removed = catalog.RemoveManual(GameId.Steam(20));

        // Assert
        removed.Should().BeFalse();
    }

    [Fact]
    public void Update_of_a_file_hash_version_raises_version_changed()
    {
        // Arrange
        var catalog = CreateCatalog();
        catalog.Load();
        var manual = catalog.AddManual(Manual()).Game;
        var newVersion = GameVersion.FromFileHashes([new("game/script.rpa", new string('f', 64))]);
        GameCatalogChangedEventArgs? raised = null;
        catalog.Changed += (_, e) => raised = e;

        // Act
        catalog.Update(manual.Id, g => g with { Version = newVersion });

        // Assert
        raised!.Changes.Should().Equal(new GameVersionChanged(manual with { Version = newVersion }, manual.Version!));
    }

    [Fact]
    public void Update_that_changes_the_id_throws()
    {
        // Arrange
        var catalog = CreateCatalog();
        catalog.Load();
        var manual = catalog.AddManual(Manual()).Game;
        var act = () => catalog.Update(manual.Id, g => g with { Id = GameId.Fingerprint(new string('0', 64)) });

        // Act
        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Corrupted_file_is_backed_up_and_the_catalog_starts_empty()
    {
        // Arrange
        WriteCatalogFile("{ not json");
        var catalog = CreateCatalog();

        // Act
        var games = catalog.Load();

        // Assert
        games.Should().BeEmpty();
        File.Exists($"{Paths.GamesFile}.20261102-101530.bak").Should().BeTrue();
    }

    [Fact]
    public void Broken_entries_are_skipped_and_the_rest_is_read()
    {
        // Arrange
        WriteCatalogFile("""
            {
              "schemaVersion": 1,
              "games": [
                { "id": "steam:abc", "name": "Bad id", "source": "steam", "installPath": "/x" },
                { "id": "steam:20", "name": "Wrong source", "source": "manual", "installPath": "/x" },
                { "id": "steam:30", "name": "Two versions", "source": "steam", "installPath": "/x",
                  "version": { "buildId": "1", "files": { "a": "b" } } },
                null,
                { "id": "steam:413150", "name": "Stardew Valley", "source": "steam", "installPath": "/x",
                  "version": { "buildId": "100" } }
              ]
            }
            """);
        var catalog = CreateCatalog();

        // Act
        var games = catalog.Load();

        // Assert
        games.Select(g => g.Id.ToString()).Should().Equal("steam:413150");
        _logger.Entries.Count(e => e.Level == LogLevel.Warning).Should().Be(4);
    }

    [Fact]
    public void Entry_without_available_flag_reads_as_available()
    {
        // Arrange
        WriteCatalogFile("""{ "schemaVersion": 1, "games": [ { "id": "steam:20", "name": "A", "source": "steam", "installPath": "/x" } ] }""");
        var catalog = CreateCatalog();

        // Act
        var games = catalog.Load();

        // Assert
        games.Single().IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void Saved_file_has_the_schema_version_and_string_ids()
    {
        // Arrange
        var catalog = CreateCatalog();
        catalog.Load();

        // Act
        catalog.Refresh(Scan((413150, "Stardew Valley", "100")));

        // Assert
        var json = JsonNode.Parse(File.ReadAllText(Paths.GamesFile))!;
        json["schemaVersion"]!.GetValue<int>().Should().Be(GameCatalog.CurrentSchemaVersion);
        json["games"]![0]!["id"]!.GetValue<string>().Should().Be("steam:413150");
        json["games"]![0]!["version"]!["buildId"]!.GetValue<string>().Should().Be("100");
    }

    [Fact]
    public void Failed_save_keeps_the_change_in_memory()
    {
        // Arrange
        _dir.File("data", "a file where the data folder should be");
        var catalog = CreateCatalog();
        catalog.Load();

        // Act
        catalog.Refresh(Scan((413150, "Stardew Valley", "100")));

        // Assert
        catalog.Games.Should().ContainSingle();
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void Unreadable_file_is_never_overwritten()
    {
        // Arrange
        _dir.Dir("data/games.json");
        var catalog = CreateCatalog();
        catalog.Load();

        // Act
        catalog.Refresh(Scan((413150, "Stardew Valley", "100")));

        // Assert
        catalog.Games.Should().ContainSingle();
        _logger.Entries.Should().NotContain(e => e.Message.Contains("Can't write", StringComparison.Ordinal));
    }
}
