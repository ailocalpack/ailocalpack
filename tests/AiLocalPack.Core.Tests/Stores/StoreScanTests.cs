using AiLocalPack.Core.Games;
using AiLocalPack.Core.Storage;
using AiLocalPack.Core.Stores;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Core.Tests.Stores;

/// <summary>The stores beside Steam read from a fixture root (tasks 84–86).</summary>
public sealed class StoreScanTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly StoreFixture _fixture;

    public StoreScanTests() => _fixture = new StoreFixture(_dir.Combine("stores"));

    public void Dispose() => _dir.Dispose();

    private StoreLocations Locations(OsKind os = OsKind.Windows) =>
        new(new OsEnvironment(os, _dir.Combine("home"), n => n == StoreLocations.RootVariable ? _fixture.Root : null), new FileStoreRegistry("unused"));

    private StoreScan Epic() => new EpicStore(Locations(), NullLogger<EpicStore>.Instance).Scan();

    private StoreScan Gog() => new GogStore(Locations(), NullLogger<GogStore>.Instance).Scan();

    private StoreScan Ea() => new EaAppStore(Locations(), NullLogger<EaAppStore>.Instance).Scan();

    private static IEnumerable<string> Ids(StoreScan scan) => scan.Games.Select(g => g.Id.ToString());

    [Fact]
    public void Epic_reads_the_launchers_manifests()
    {
        // Arrange
        var folder = _fixture.EpicGame("Fennel", "Lanternkeepers", version: "++Fennel+Release-1.2 CL/7");

        // Act
        var scan = Epic();

        // Assert
        scan.Games.Should().Equal(new StoreGame(
            GameId.Parse("epic:Fennel"), "Lanternkeepers", folder, Path.GetDirectoryName(folder)!, "++Fennel+Release-1.2_CL_7", "Game.exe", StoreApps.EpicLauncher));
    }

    [Fact]
    public void Epic_skips_dlc_unfinished_installs_and_non_games()
    {
        // Arrange
        _fixture.EpicGame("Fennel", "Lanternkeepers");
        _fixture.EpicGame("Fennel_DLC", "Night Market", mainGameAppName: "Fennel");
        _fixture.EpicGame("Half", "Half Installed", incomplete: true);
        _fixture.EpicGame("UE_5.4", "Unreal Engine", categories: ["engines"]);

        // Act
        var scan = Epic();

        // Assert
        Ids(scan).Should().Equal("epic:Fennel");
    }

    [Fact]
    public void Epic_skips_a_broken_manifest_and_reads_the_rest()
    {
        // Arrange
        _fixture.EpicGame("Fennel", "Lanternkeepers");
        File.WriteAllText(Path.Combine(_fixture.Root, "epic-launcher", "Manifests", "broken.item"), "{ not json");

        // Act
        var scan = Epic();

        // Assert
        (Ids(scan).Single(), scan.Problem).Should().Be(("epic:Fennel", null));
    }

    [Fact]
    public void Epic_reads_heroic_and_legendary_and_keeps_the_launchers_entry_first()
    {
        // Arrange
        _fixture.EpicGame("Fennel", "Lanternkeepers");
        _fixture.HeroicEpicGame("Fennel", "Lanternkeepers (Heroic)");
        _fixture.HeroicEpicGame("Juniper", "Clockwork Orchard");
        _fixture.HeroicEpicGame("Juniper_DLC", "Orchard DLC", dlc: true);
        _fixture.HeroicEpicGame("Sorrel", "Sorrel Fields", standalone: true);

        // Act
        var scan = Epic();

        // Assert
        scan.Games.Select(g => (g.Id.Value, g.App)).Should().BeEquivalentTo(
            [("Fennel", StoreApps.EpicLauncher), ("Juniper", StoreApps.Heroic), ("Sorrel", StoreApps.Legendary)]);
    }

    [Fact]
    public void A_game_on_a_drive_that_isnt_there_is_listed_as_unavailable()
    {
        // Arrange
        _fixture.EpicGame("Fennel", "Lanternkeepers", createFolder: false);

        // Act
        var scan = Epic();

        // Assert
        (scan.Games.Single().IsAvailable, scan.Folders.Single().IsAvailable).Should().Be((false, false));
    }

    [Fact]
    public void A_store_without_records_is_not_found()
    {
        // Arrange
        // Act
        var scans = new[] { Epic(), Gog(), Ea() };

        // Assert
        scans.Select(s => (s.Found, s.Games.Count)).Should().AllBeEquivalentTo((false, 0));
    }

    [Fact]
    public void Gog_reads_the_registry_and_the_games_info_file()
    {
        // Arrange
        var folder = _fixture.GogGame("1207658924", "Saltmarsh Tales", buildId: "56789");
        _fixture.GogGame("1207658925", "Saltmarsh Soundtrack", dependsOn: "1207658924");

        // Act
        var scan = Gog();

        // Assert
        scan.Games.Should().Equal(new StoreGame(
            GameId.Parse("gog:1207658924"), "Saltmarsh Tales", folder, Path.GetDirectoryName(folder)!, "56789", "Game.exe", StoreApps.GogGalaxy));
    }

    [Fact]
    public void Gog_reads_heroic_and_app_bundles()
    {
        // Arrange
        _fixture.HeroicGogGame("1453375253", "Paper Lighthouse");
        _fixture.HeroicGogGame("1453375254", "Paper Lighthouse DLC", dlc: true);
        var bundle = _fixture.GogBundle("1207666666", "Moss and Copper");

        // Act
        var scan = Gog();

        // Assert
        scan.Games.Select(g => (g.Name, g.App, g.InstallPath == bundle)).Should().BeEquivalentTo(
            [("Paper Lighthouse", StoreApps.Heroic, false), ("Moss and Copper", StoreApps.GogGalaxy, true)]);
    }

    [Fact]
    public void Gog_info_names_a_dlc_by_its_root_game()
    {
        // Arrange
        var folder = _dir.Dir("dlc").Combine("dlc");
        File.WriteAllText(Path.Combine(folder, "goggame-2.info"), """{ "gameId": "2", "rootGameId": "1", "name": "DLC" }""");

        // Act
        var info = GogInfo.Read(folder, "2");

        // Assert
        info!.IsDlc.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ea_reads_its_uninstall_entries_and_installer_data(bool utf16)
    {
        // Arrange
        var folder = _fixture.EaGame("Origin.OFR.50.0004321", "Ironbark Legends", version: "1.0.2.3", utf16: utf16);
        _fixture.OtherProgram("Some Other Program");

        // Act
        var scan = Ea();

        // Assert
        scan.Games.Should().Equal(new StoreGame(
            GameId.Parse("ea:Origin.OFR.50.0004321"), "Ironbark Legends", folder, Path.GetDirectoryName(folder)!, "1.0.2.3", "Game.exe", StoreApps.EaApp));
    }

    [Fact]
    public void Ea_lists_a_missing_game_folder_so_the_catalog_keeps_the_game()
    {
        // Arrange
        var folder = _fixture.EaGame("1035052", "Starfall Rally", createFolder: false);

        // Act
        var scan = Ea();

        // Assert
        (scan.Found, scan.Games.Count, scan.MissingFolders!.Single()).Should().Be((true, 0, folder));
    }

    [Fact]
    public void Ea_reads_the_older_installer_data_form()
    {
        // Arrange
        var folder = _dir.Dir("old/__Installer").Combine("old/__Installer");
        File.WriteAllText(Path.Combine(folder, "installerdata.xml"),
            "<game><contentIDs><contentID>71171</contentID></contentIDs><metadata><localeInfo><title>Old Game</title></localeInfo></metadata></game>");

        // Act
        var data = EaInstallerData.Read(_dir.Combine("old"));

        // Assert
        data.Should().Be(new EaInstallerData("71171", "Old Game", null, null));
    }

    [Theory]
    [InlineData(OsKind.Windows, true)]
    [InlineData(OsKind.MacOS, false)]
    [InlineData(OsKind.Linux, false)]
    public void The_ea_app_is_a_windows_store(OsKind os, bool supported)
    {
        // Arrange
        var locations = new StoreLocations(new OsEnvironment(os, _dir.Combine("home"), _ => null), new FileStoreRegistry("unused"));

        // Act
        var store = new EaAppStore(locations, NullLogger<EaAppStore>.Instance);

        // Assert
        store.IsSupportedOnThisOs.Should().Be(supported);
    }

    [Fact]
    public void Linux_reads_heroic_from_the_config_home_and_flatpak()
    {
        // Arrange
        var home = _dir.Combine("home");
        var env = new OsEnvironment(OsKind.Linux, home, n => n == "XDG_CONFIG_HOME" ? Path.Combine(home, "cfg") : null);

        // Act
        var folders = new StoreLocations(env, new FileStoreRegistry("unused")).HeroicFolders();

        // Assert
        folders.Should().Equal(
            Path.Combine(home, "cfg", "heroic"),
            Path.Combine(home, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic"));
    }

    [Fact]
    public void Windows_reads_epic_manifests_from_program_data()
    {
        // Arrange
        var env = new OsEnvironment(OsKind.Windows, _dir.Combine("home"), n => n == "ProgramData" ? "D:\\ProgramData" : null);

        // Act
        var folders = new StoreLocations(env, new FileStoreRegistry("unused")).EpicManifestFolders();

        // Assert
        folders.Should().Equal(Path.Combine("D:\\ProgramData", "Epic", "EpicGamesLauncher", "Data", "Manifests"));
    }
}
