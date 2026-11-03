using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Folders;

namespace AiLocalPack.Toolkit.Tests.Folders;

public sealed class FolderRecognizerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FolderRecognizer _recognizer = new();

    public void Dispose() => _dir.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FolderScan Scan(string relative = "", FolderScanOptions? options = null) =>
        _recognizer.Scan(relative.Length == 0 ? _dir.Path : _dir.Combine(relative), options, cancellationToken: Ct);

    private static string[] Paths(FolderScan scan) => [.. scan.Games.Select(g => g.RelativePath)];

    [Fact]
    public void The_picked_folder_itself_is_the_game()
    {
        // Arrange
        _dir.UnityWindows(name: "Moss");

        // Act
        var scan = Scan();

        // Assert
        scan.Games.Should().ContainSingle().Which.Should().Match<FoundGame>(g =>
            g.RelativePath == "" && g.Engine.Kind == new EngineKind(GameEngine.Unity, EngineVariants.Mono) && g.MainExecutable == "Moss.exe");
    }

    [Fact]
    public void A_game_nested_in_a_store_folder_is_found_with_its_relative_path()
    {
        // Arrange
        _dir.RenPy("GOG/Moss and Mortar", name: "MossAndMortar");

        // Act
        var scan = Scan();

        // Assert
        scan.Games.Should().ContainSingle().Which.Should().Match<FoundGame>(g =>
            g.RelativePath == "GOG/Moss and Mortar" && g.Name == "Moss and Mortar" && g.MainExecutable == "MossAndMortar.exe");
    }

    [Fact]
    public void A_folder_with_several_games_lists_them_all_in_path_order()
    {
        // Arrange
        _dir.UnityWindows("Indie/Wren").GodotWindows("Indie/Alpha").RpgMakerMz("Puzzles/Tiles").File("Indie/notes.txt", "x");

        // Act
        var scan = Scan();

        // Assert
        Paths(scan).Should().Equal("Indie/Alpha", "Indie/Wren", "Puzzles/Tiles");
    }

    [Fact]
    public void The_scan_does_not_descend_into_a_recognized_game()
    {
        // Arrange
        _dir.UnrealWindows("Shooter")
            .File("Shooter/EasyAntiCheat/EasyAntiCheat_EOS_Setup.exe", "MZ")
            .File("Shooter/Launcher/Launcher.exe", "MZ")
            .File("Shooter/_CommonRedist/vcredist/vc_redist.x64.exe", "MZ");

        // Act
        var scan = Scan();

        // Assert
        (Paths(scan), scan.Skipped).Should().BeEquivalentTo((new[] { "Shooter" }, Array.Empty<SkippedFolder>()));
    }

    [Theory]
    [InlineData("_CommonRedist/DirectX/Jun2010/DXSETUP.exe", "_CommonRedist", SkipReason.Redistributable)]
    [InlineData("Stuff/vcredist_x64.exe", "Stuff", SkipReason.Redistributable)]
    [InlineData("__Installer/installscript.vdf", "__Installer", SkipReason.Installer)]
    [InlineData("Downloads/setup_moss_and_mortar_1.2.exe", "Downloads", SkipReason.Installer)]
    [InlineData("SteamVR/bin/win64/vrserver.exe", "SteamVR", SkipReason.Utility)]
    [InlineData("Saves/slot1.sav", "Saves", SkipReason.Saves)]
    [InlineData("Docs/manual.pdf", "Docs", SkipReason.NoGame)]
    [InlineData("Archive/Old/readme.txt", "Archive", SkipReason.NoGame)]
    [InlineData("Archive/Redist/x.exe", "Archive", SkipReason.Redistributable)]
    public void A_folder_without_games_is_skipped_with_the_reason(string file, string skipped, SkipReason reason)
    {
        // Arrange
        _dir.GodotWindows("Game").File(file, "MZ");

        // Act
        var scan = Scan();

        // Assert
        (Paths(scan), scan.Skipped).Should().BeEquivalentTo((new[] { "Game" }, new[] { new SkippedFolder(skipped, reason) }));
    }

    [Fact]
    public void A_game_with_its_executable_in_a_binaries_folder_is_the_folder_above()
    {
        // Arrange
        _dir.File("Classic/bin/x64/classic.exe", "MZ").File("Classic/data/strings.dat", "x");

        // Act
        var scan = Scan();

        // Assert
        scan.Games.Should().ContainSingle().Which.Should().Match<FoundGame>(g =>
            g.RelativePath == "Classic" && g.MainExecutable == "bin/x64/classic.exe");
    }

    [Fact]
    public void An_engine_layout_without_an_executable_is_a_game()
    {
        // Arrange
        _dir.File("Novel/renpy/__init__.py", "#").File("Novel/game/script.rpyc", "x");

        // Act
        var scan = Scan();

        // Assert
        scan.Games.Should().ContainSingle().Which.Engine.Kind.Engine.Should().Be(GameEngine.RenPy);
    }

    [Fact]
    public void A_picked_macos_bundle_is_the_game()
    {
        // Arrange
        _dir.UnityMac(name: "Moss");

        // Act
        var scan = Scan("Moss.app");

        // Assert
        scan.Games.Should().ContainSingle().Which.Should().Match<FoundGame>(g =>
            g.Name == "Moss" && g.Engine.Kind.Engine == GameEngine.Unity && g.MainExecutable == "Contents/MacOS/Moss");
    }

    [Fact]
    public void A_folder_holding_a_bare_bundle_contents_is_a_game()
    {
        // Arrange
        _dir.File("Valley/Contents/Info.plist", "<plist/>").File("Valley/Contents/MacOS/Valley", "macho");

        // Act
        var scan = Scan();

        // Assert
        scan.Games.Should().ContainSingle().Which.Should().Match<FoundGame>(g =>
            g.RelativePath == "Valley" && g.MainExecutable == "Contents/MacOS/Valley");
    }

    [Theory]
    [InlineData("Barony Editor.app|Barony.app", "Barony.app")]
    [InlineData("Barony Editor.exe|Launcher.exe", "Launcher.exe")]
    [InlineData("a-game.exe|barony.exe", "barony.exe")]
    public void The_main_executable_is_the_one_named_like_the_folder_and_not_an_editor(string executables, string expected)
    {
        // Arrange
        foreach (var exe in executables.Split('|'))
        {
            _ = exe.EndsWith(".app", StringComparison.Ordinal)
                ? _dir.File($"Barony/{exe}/Contents/MacOS/bin", "macho")
                : _dir.File($"Barony/{exe}", "MZ");
        }

        // Act
        var scan = Scan();

        // Assert
        scan.Games.Should().ContainSingle().Which.MainExecutable.Should().Be(expected);
    }

    [Fact]
    public void A_dropped_steam_library_finds_its_games_and_flags_them()
    {
        // Arrange
        _dir.UnityWindows("SteamLibrary/steamapps/common/Moss")
            .GodotLinux("SteamLibrary/steamapps/common/Wren")
            .File("SteamLibrary/steamapps/appmanifest_10.acf", "\"AppState\" {}")
            .File("SteamLibrary/steamapps/compatdata/10/pfx/drive_c/windows/notepad.exe", "MZ")
            .File("SteamLibrary/steamapps/shadercache/10/fozpipelinesv6/steam.exe", "MZ");

        // Act
        var scan = Scan("SteamLibrary");

        // Assert
        (Paths(scan), scan.IsSteamLibrary).Should().BeEquivalentTo((new[] { "steamapps/common/Moss", "steamapps/common/Wren" }, true));
    }

    [Fact]
    public void A_dropped_steamapps_common_folder_is_flagged_as_steam()
    {
        // Arrange
        _dir.UnityWindows("steamapps/common/Moss").File("steamapps/common/Steamworks Shared/_CommonRedist/x.exe", "MZ");

        // Act
        var scan = Scan("steamapps/common");

        // Assert
        (Paths(scan), scan.IsSteamLibrary).Should().BeEquivalentTo((new[] { "Moss" }, true));
    }

    [Fact]
    public void Games_outside_a_steam_library_are_not_flagged()
    {
        // Arrange
        _dir.UnityWindows("Games/Moss");

        // Act
        var scan = Scan();

        // Assert
        scan.Games.Should().ContainSingle().Which.InSteamLibrary.Should().BeFalse();
    }

    [Fact]
    public void Games_deeper_than_the_depth_limit_are_not_found()
    {
        // Arrange
        _dir.UnityWindows("a/b/c/Deep").UnityWindows("a/b/Shallow");

        // Act
        var scan = Scan();

        // Assert
        Paths(scan).Should().Equal("a/b/Shallow");
    }

    [Fact]
    public void Hidden_and_system_folders_are_ignored_silently()
    {
        // Arrange
        _dir.UnityWindows("Moss").File(".Trash/old/game.exe", "MZ").File("$RECYCLE.BIN/S-1/x.exe", "MZ");

        // Act
        var scan = Scan();

        // Assert
        (Paths(scan), scan.Skipped.Count).Should().BeEquivalentTo((new[] { "Moss" }, 0));
    }

    [Fact]
    public void A_folder_over_the_entry_limit_stops_and_says_so()
    {
        // Arrange
        for (var i = 0; i < 30; i++)
        {
            _dir.File($"Big/file{i}.dat", "x");
        }

        _dir.UnityWindows("Zed");

        // Act
        var scan = Scan(options: new FolderScanOptions { MaxEntries = 20 });

        // Assert
        (scan.LimitReached, Paths(scan).Length).Should().Be((true, 0));
    }

    [Fact]
    public void A_missing_folder_gives_an_empty_scan()
    {
        // Arrange
        var missing = _dir.Combine("missing");

        // Act
        var scan = _recognizer.Scan(missing, cancellationToken: Ct);

        // Assert
        (scan.Games.Count, scan.Skipped.Count, scan.LimitReached).Should().Be((0, 0, false));
    }

    [Fact]
    public void Progress_streams_each_game_as_it_is_found()
    {
        // Arrange
        _dir.UnityWindows("Moss").GodotWindows("Wren");
        var progress = new RecordingProgress<FolderScanProgress>();

        // Act
        _recognizer.Scan(_dir.Path, progress: progress, cancellationToken: Ct);

        // Assert
        progress.Reports.Select(r => r.Game?.Name).OfType<string>().Should().Equal("Moss", "Wren");
    }

    [Fact]
    public void Progress_counts_scanned_and_known_folders()
    {
        // Arrange
        _dir.UnityWindows("Moss").GodotWindows("Wren");
        var progress = new RecordingProgress<FolderScanProgress>();

        // Act
        _recognizer.Scan(_dir.Path, progress: progress, cancellationToken: Ct);

        // Assert
        progress.Reports[^1].Should().Be(new FolderScanProgress(3, 3, 2, progress.Reports[^1].Game));
    }

    [Fact]
    public void Cancelling_stops_the_scan_and_returns_what_was_found()
    {
        // Arrange
        _dir.UnityWindows("A").UnityWindows("B").UnityWindows("C");
        using var cts = new CancellationTokenSource();
        var progress = new RecordingProgress<FolderScanProgress>(p =>
        {
            if (p.Game is not null)
            {
                cts.Cancel();
            }
        });

        // Act
        var scan = _recognizer.Scan(_dir.Path, progress: progress, cancellationToken: cts.Token);

        // Assert
        (scan.Stopped, Paths(scan)).Should().BeEquivalentTo((true, new[] { "A" }));
    }
}
