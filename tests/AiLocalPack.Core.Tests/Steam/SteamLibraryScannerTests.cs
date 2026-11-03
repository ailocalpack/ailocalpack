using AiLocalPack.Core.Steam;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Core.Tests.Steam;

public sealed class SteamLibraryScannerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly SteamFixture _steam;
    private readonly TestLogger<SteamLibraryScanner> _logger = new();

    public SteamLibraryScannerTests() => _steam = new SteamFixture(_dir);

    public void Dispose() => _dir.Dispose();

    private SteamLibraryScanner Scanner()
    {
        var env = new OsEnvironment(OsKind.Linux, _dir.Path, name => name == SteamLocator.RootVariable ? _steam.Root : null);
        return new SteamLibraryScanner(new SteamLocator(env), _logger);
    }

    [Fact]
    public void Lists_a_fully_installed_game_with_its_manifest_data()
    {
        // Arrange
        _steam.AddGame(413150, "Stardew Valley", buildId: "16826371");

        // Act
        var scan = Scanner().Scan();

        // Assert
        scan.Games.Should().ContainSingle().Which.Should().BeEquivalentTo(new SteamGame(
            413150,
            "Stardew Valley",
            "16826371",
            Path.Combine(_steam.Root, "steamapps", "common", "Stardew Valley"),
            _steam.Root,
            LastPlayed: null));
    }

    [Fact]
    public void Lists_games_from_the_available_library_and_reports_the_missing_one_as_not_available()
    {
        // Arrange
        var second = _steam.AddLibrary("Drive/SteamLibrary");
        var missing = _steam.AddLibrary("SdCard/SteamLibrary", create: false);
        _steam.AddGame(10, "Root Game").AddGame(20, "Second Drive Game", library: second);

        // Act
        var scan = Scanner().Scan();

        // Assert
        scan.Libraries.Should().Equal(
            new SteamLibrary(_steam.Root, true),
            new SteamLibrary(second, true),
            new SteamLibrary(missing, false));
        scan.Games.Select(g => (g.AppId, g.LibraryPath)).Should().Equal((10u, _steam.Root), (20u, second));
    }

    [Fact]
    public void Missing_library_is_not_logged_as_a_warning()
    {
        // Arrange
        _steam.AddLibrary("SdCard/SteamLibrary", create: false);

        // Act
        Scanner().Scan();

        // Assert
        _logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public void Skips_a_partially_installed_game()
    {
        // Arrange
        _steam.AddGame(10, "Installed").AddGame(20, "Downloading", stateFlags: SteamFixture.Downloading);

        // Act
        var scan = Scanner().Scan();

        // Assert
        scan.Games.Select(g => g.AppId).Should().Equal(10u);
    }

    [Fact]
    public void Keeps_an_installed_game_with_an_update_pending()
    {
        // Arrange: fully installed (4) + update required (2)
        _steam.AddGame(10, "Needs Update", stateFlags: 6);

        // Act
        var scan = Scanner().Scan();

        // Assert
        scan.Games.Should().ContainSingle();
    }

    [Theory]
    [InlineData(228980u, "Steamworks Common Redistributables")]
    [InlineData(1493710u, "Proton Experimental")]
    [InlineData(1628350u, "Steam Linux Runtime 3.0 (sniper)")]
    [InlineData(9999991u, "Proton 11.0")]
    [InlineData(9999992u, "Steam Linux Runtime 4.0")]
    public void Skips_redistributables_and_runtimes(uint appId, string name)
    {
        // Arrange
        _steam.AddGame(appId, name);

        // Act
        var scan = Scanner().Scan();

        // Assert
        scan.Games.Should().BeEmpty();
    }

    [Fact]
    public void Keeps_a_game_whose_name_merely_starts_with_proton()
    {
        // Arrange
        _steam.AddGame(10, "Proton Pulse");

        // Act
        var scan = Scanner().Scan();

        // Assert
        scan.Games.Should().ContainSingle();
    }

    [Fact]
    public void Takes_last_played_as_the_latest_across_users()
    {
        // Arrange
        _steam.AddGame(10, "Game")
            .SetLastPlayed("111", (10, 1_700_000_000))
            .SetLastPlayed("222", (10, 1_800_000_000));

        // Act
        var scan = Scanner().Scan();

        // Assert
        scan.Games.Single().LastPlayed.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
    }

    [Fact]
    public void Falls_back_to_the_manifest_last_played_without_user_data()
    {
        // Arrange
        _steam.AddGame(10, "Game", lastPlayed: 1_600_000_000);

        // Act
        var scan = Scanner().Scan();

        // Assert
        scan.Games.Single().LastPlayed.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1_600_000_000));
    }

    [Fact]
    public void Broken_localconfig_leaves_last_played_unknown()
    {
        // Arrange
        _steam.AddGame(10, "Game");
        _dir.File("Steam/userdata/111/config/localconfig.vdf", "\"UserLocalConfigStore\" { \"Software\"");

        // Act
        var scan = Scanner().Scan();

        // Assert
        scan.Games.Single().LastPlayed.Should().BeNull();
    }

    [Fact]
    public void Skips_a_broken_manifest_and_keeps_the_rest()
    {
        // Arrange
        _steam.AddGame(10, "Good").AddRawSteamappsFile("appmanifest_20.acf", "\"AppState\" { \"appid\" ");

        // Act
        var scan = Scanner().Scan();

        // Assert
        scan.Games.Select(g => g.AppId).Should().Equal(10u);
    }

    [Fact]
    public void Logs_a_warning_for_a_broken_manifest()
    {
        // Arrange
        _steam.AddRawSteamappsFile("appmanifest_20.acf", "\"AppState\" { \"appid\" ");

        // Act
        Scanner().Scan();

        // Assert
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Message.Contains("appmanifest_20.acf"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../../etc")]
    public void Skips_a_manifest_with_an_invalid_install_dir(string installDir)
    {
        // Arrange
        _steam.AddRawSteamappsFile(
            "appmanifest_10.acf",
            $"\"AppState\" {{ \"appid\" \"10\" \"name\" \"Game\" \"StateFlags\" \"4\" \"installdir\" \"{installDir}\" }}");

        // Act
        var scan = Scanner().Scan();

        // Assert
        scan.Games.Should().BeEmpty();
    }

    [Fact]
    public void Scans_only_the_root_when_libraryfolders_is_broken()
    {
        // Arrange
        _steam.AddGame(10, "Game").AddRawSteamappsFile("libraryfolders.vdf", "\"libraryfolders\" {");

        // Act
        var scan = Scanner().Scan();

        // Assert
        scan.Libraries.Should().Equal(new SteamLibrary(_steam.Root, true));
        scan.Games.Should().ContainSingle();
    }

    [Fact]
    public void Reads_the_old_libraryfolders_format()
    {
        // Arrange
        var second = _dir.Dir("Old/steamapps").Combine("Old");
        _steam.AddGame(20, "Old Game", library: second).AddRawSteamappsFile(
            "libraryfolders.vdf",
            $"\"LibraryFolders\" {{ \"TimeNextStatsReport\" \"1\" \"ContentStatsID\" \"2\" \"1\" \"{second.Replace("\\", "\\\\")}\" }}");

        // Act
        var scan = Scanner().Scan();

        // Assert
        scan.Games.Select(g => (g.AppId, g.LibraryPath)).Should().Equal((20u, second));
    }

    [Fact]
    public void Lists_a_library_once_when_libraryfolders_repeats_the_root()
    {
        // Arrange
        _steam.AddLibrary("Steam");

        // Act
        var scan = Scanner().Scan();

        // Assert
        scan.Libraries.Should().ContainSingle();
    }

    [Fact]
    public void Sorts_games_by_name()
    {
        // Arrange
        _steam.AddGame(1, "beta").AddGame(2, "Alpha").AddGame(3, "Gamma");

        // Act
        var scan = Scanner().Scan();

        // Assert
        scan.Games.Select(g => g.Name).Should().Equal("Alpha", "beta", "Gamma");
    }

    [Fact]
    public void No_steam_gives_an_empty_scan()
    {
        // Arrange
        var env = new OsEnvironment(OsKind.Linux, _dir.Combine("empty-home"), _ => null);

        // Act
        var scan = new SteamLibraryScanner(new SteamLocator(env)).Scan();

        // Assert
        scan.Should().BeEquivalentTo(SteamScan.Empty);
    }

    [Fact]
    public void Lists_games_of_a_library_folder_added_by_hand()
    {
        // Arrange
        var extra = _dir.Dir("Extra/steamapps").Combine("Extra");
        _steam.AddGame(30, "Gamma", library: extra);

        // Act
        var scan = Scanner().Scan(new SteamLibraryChoices([extra], []));

        // Assert
        scan.Libraries.Should().Equal(new SteamLibrary(_steam.Root, true), new SteamLibrary(extra, true, IsAddedByHand: true));
        scan.Games.Should().ContainSingle().Which.LibraryPath.Should().Be(extra);
    }

    [Fact]
    public void A_folder_added_by_hand_that_steam_lists_too_is_reported_once_as_steams()
    {
        // Arrange
        var sdCard = _steam.AddLibrary("SdCard");

        // Act
        var scan = Scanner().Scan(new SteamLibraryChoices([sdCard], []));

        // Assert
        scan.Libraries.Should().Equal(new SteamLibrary(_steam.Root, true), new SteamLibrary(sdCard, true));
    }

    [Fact]
    public void A_missing_folder_added_by_hand_is_reported_as_not_available()
    {
        // Arrange
        var gone = _dir.Combine("Gone");

        // Act
        var scan = Scanner().Scan(new SteamLibraryChoices([gone], []));

        // Assert
        scan.Libraries.Should().Contain(new SteamLibrary(gone, false, IsAddedByHand: true));
    }

    [Fact]
    public void Leaves_out_a_library_the_player_removed()
    {
        // Arrange
        var sdCard = _steam.AddLibrary("SdCard");
        _steam.AddGame(20, "Beta", library: sdCard);

        // Act
        var scan = Scanner().Scan(new SteamLibraryChoices([], [sdCard]));

        // Assert
        scan.Libraries.Select(l => l.Path).Should().Equal(_steam.Root);
        scan.Games.Should().BeEmpty();
    }

    [Fact]
    public void The_steam_root_can_not_be_left_out()
    {
        // Arrange
        _steam.AddGame(10, "Alpha");

        // Act
        var scan = Scanner().Scan(new SteamLibraryChoices([], [_steam.Root]));

        // Assert
        scan.Games.Should().ContainSingle();
    }

    [Fact]
    public void Folders_added_by_hand_alone_do_not_count_as_steam_found()
    {
        // Arrange
        var extra = _dir.Dir("Extra/steamapps").Combine("Extra");
        var env = new OsEnvironment(OsKind.Linux, _dir.Path, name => name == SteamLocator.RootVariable ? _dir.Combine("no-steam") : null);

        // Act
        var scan = new SteamLibraryScanner(new SteamLocator(env)).Scan(new SteamLibraryChoices([extra], []));

        // Assert
        scan.SteamFound.Should().BeFalse();
        scan.Libraries.Should().ContainSingle();
    }

    [Theory]
    [InlineData("Lib", "Lib")]
    [InlineData("Lib/steamapps", "Lib")]
    [InlineData("Lib/steamapps/common", "Lib")]
    public void Find_library_folder_accepts_the_library_or_its_steamapps(string picked, string expected)
    {
        // Arrange
        _dir.Dir("Lib/steamapps/common");

        // Act
        var folder = SteamLibraryScanner.FindLibraryFolder(_dir.Combine(picked));

        // Assert
        folder.Should().Be(_dir.Combine(expected));
    }

    [Fact]
    public void Find_library_folder_rejects_a_folder_without_steamapps()
    {
        // Arrange
        _dir.Dir("Games/common");

        // Act
        var folder = SteamLibraryScanner.FindLibraryFolder(_dir.Combine("Games/common"));

        // Assert
        folder.Should().BeNull();
    }
}
