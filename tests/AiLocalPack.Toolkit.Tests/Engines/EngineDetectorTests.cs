using AiLocalPack.Toolkit.Engines;

namespace AiLocalPack.Toolkit.Tests.Engines;

public sealed class EngineDetectorTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly EngineDetector _detector = new();

    public void Dispose() => _dir.Dispose();

    private EngineDetection Detect(string relative = "") => _detector.Detect(relative.Length == 0 ? _dir.Path : _dir.Combine(relative));

    private void Build(string layout)
    {
        _ = layout switch
        {
            "unity-windows-mono" => _dir.UnityWindows(),
            "unity-windows-il2cpp" => _dir.UnityWindows(il2Cpp: true),
            "unity-linux-mono" => _dir.UnityLinux(),
            "unity-linux-il2cpp" => _dir.UnityLinux(il2Cpp: true),
            "unity-mac-mono" => _dir.UnityMac(),
            "unity-mac-il2cpp" => _dir.UnityMac(il2Cpp: true),
            "unreal-windows-5" => _dir.UnrealWindows(),
            "unreal-windows-4" => _dir.UnrealWindows(branch: "++UE4+Release-4.27"),
            "unreal-linux-4" => _dir.UnrealLinux(),
            "unreal-mac-4" => _dir.UnrealMac(ue4: true),
            "unreal-mac-5" => _dir.UnrealMac(),
            "rpgmaker-mv" => _dir.RpgMakerMv(),
            "rpgmaker-mz" => _dir.RpgMakerMz(),
            "rpgmaker-mac-mz" => _dir.RpgMakerMac(),
            "renpy-windows-linux" => _dir.RenPy(),
            "renpy-mac" => _dir.RenPyMac(),
            "godot-windows-4" => _dir.GodotWindows(),
            "godot-linux-3" => _dir.GodotLinux(),
            "godot-mac-4" => _dir.GodotMac(),
            "godot-embedded-4" => _dir.GodotEmbedded(),
            "godot-embedded-linux-3" => _dir.GodotEmbedded(name: "Game.x86_64", major: 3),
            _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, null),
        };
    }

    [Theory]
    [InlineData("unity-windows-mono", "unity:mono", "Game.exe")]
    [InlineData("unity-windows-il2cpp", "unity:il2cpp", "Game.exe")]
    [InlineData("unity-linux-mono", "unity:mono", "Game.x86_64")]
    [InlineData("unity-linux-il2cpp", "unity:il2cpp", "Game.x86_64")]
    [InlineData("unity-mac-mono", "unity:mono", "Game.app")]
    [InlineData("unity-mac-il2cpp", "unity:il2cpp", "Game.app")]
    [InlineData("unreal-windows-5", "unreal:5", "Game/Binaries/Win64/Game-Win64-Shipping.exe")]
    [InlineData("unreal-windows-4", "unreal:4", "Game/Binaries/Win64/Game-Win64-Shipping.exe")]
    [InlineData("unreal-linux-4", "unreal:4", "Game/Binaries/Linux/Game-Linux-Shipping")]
    [InlineData("unreal-mac-4", "unreal:4", "Game.app")]
    [InlineData("unreal-mac-5", "unreal:5", "Game.app")]
    [InlineData("rpgmaker-mv", "rpgmaker:mv", "Game.exe")]
    [InlineData("rpgmaker-mz", "rpgmaker:mz", "Game.exe")]
    [InlineData("rpgmaker-mac-mz", "rpgmaker:mz", "Game.app")]
    [InlineData("renpy-windows-linux", "renpy", "Game.exe")]
    [InlineData("renpy-mac", "renpy", "Game.app")]
    [InlineData("godot-windows-4", "godot:4", "Game.exe")]
    [InlineData("godot-linux-3", "godot:3", "Game.x86_64")]
    [InlineData("godot-mac-4", "godot:4", "Game.app")]
    [InlineData("godot-embedded-4", "godot:4", "Game.exe")]
    [InlineData("godot-embedded-linux-3", "godot:3", "Game.x86_64")]
    public void Layout_gives_engine_variant_and_main_executable(string layout, string engine, string executable)
    {
        // Arrange
        Build(layout);

        // Act
        var detection = Detect();

        // Assert
        detection.Match.Should().NotBeNull();
        (detection.Match!.Kind.ToString(), detection.Match.Confidence, detection.Match.MainExecutable)
            .Should().Be((engine, EngineConfidence.High, executable));
    }

    [Fact]
    public void Empty_folder_is_unknown()
    {
        // Arrange
        _dir.Dir("Empty");

        // Act
        var detection = Detect("Empty");

        // Assert
        detection.Should().BeEquivalentTo(new { Match = (EngineMatch?)null, Candidates = Array.Empty<EngineMatch>(), Kind = EngineKind.Unknown, IsAmbiguous = false });
    }

    [Fact]
    public void Missing_folder_is_unknown()
    {
        // Arrange
        var missing = _dir.Combine("not-there");

        // Act
        var detection = _detector.Detect(missing);

        // Assert
        detection.Kind.Should().Be(EngineKind.Unknown);
    }

    [Fact]
    public void Two_engines_equally_likely_are_ambiguous()
    {
        // Arrange
        _dir.UnityWindows().GodotWindows(name: "Other");

        // Act
        var detection = Detect();

        // Assert
        (detection.Match, detection.IsAmbiguous, detection.Kind).Should().Be(((EngineMatch?)null, true, EngineKind.Unknown));
        detection.Candidates.Select(c => c.Kind.Engine).Should().BeEquivalentTo([GameEngine.Unity, GameEngine.Godot]);
    }

    [Fact]
    public void Game_in_a_subfolder_is_found()
    {
        // Arrange
        _dir.File("launcher.txt", "x").RpgMakerMz("Game");

        // Act
        var detection = Detect();

        // Assert
        (detection.Kind.ToString(), detection.Match!.Marker, detection.Match.MainExecutable)
            .Should().Be(("rpgmaker:mz", "Game/js/rmmz_core.js", "Game/Game.exe"));
    }

    [Fact]
    public void Top_level_engine_wins_over_another_engine_in_a_subfolder()
    {
        // Arrange
        _dir.UnityWindows().RenPy("Extras/Bonus");
        _dir.GodotWindows("Tools");

        // Act
        var detection = Detect();

        // Assert
        (detection.Kind.ToString(), detection.IsAmbiguous).Should().Be(("unity:mono", false));
    }

    [Fact]
    public void Folder_path_of_a_mac_bundle_itself_is_detected()
    {
        // Arrange
        _dir.UnityMac(il2Cpp: true);

        // Act
        var detection = Detect("Game.app");

        // Assert
        (detection.Kind.ToString(), detection.Match!.MainExecutable).Should().Be(("unity:il2cpp", "Contents/MacOS/Game"));
    }

    [Fact]
    public void Bare_bundle_contents_in_the_game_folder_are_detected()
    {
        // Arrange
        _dir.UnityMac("Steam");
        Directory.Move(_dir.Combine("Steam/Game.app/Contents"), _dir.Combine("Steam/Contents"));
        Directory.Delete(_dir.Combine("Steam/Game.app"));
        _dir.File("Steam/Contents/Info.plist", "<plist/>");

        // Act
        var detection = Detect("Steam");

        // Assert
        (detection.Kind.ToString(), detection.Match!.MainExecutable).Should().Be(("unity:mono", "Contents/MacOS/Game"));
    }

    [Fact]
    public void Wwise_sound_bank_pck_is_not_godot()
    {
        // Arrange
        _dir.File("Game.exe", "MZ").File("SoundBanks.pck", "AKPK....");

        // Act
        var detection = Detect();

        // Assert
        detection.Kind.Should().Be(EngineKind.Unknown);
    }

    [Fact]
    public void Folder_named_like_unity_data_without_unity_files_is_not_unity()
    {
        // Arrange
        _dir.File("Game.exe", "MZ").File("Save_Data/slot1.sav", "x");

        // Act
        var detection = Detect();

        // Assert
        detection.Kind.Should().Be(EngineKind.Unknown);
    }

    [Fact]
    public void Unity_player_without_data_folder_is_a_low_confidence_unity()
    {
        // Arrange
        _dir.File("Game.exe", "MZ").File("UnityPlayer.dll", "MZ");

        // Act
        var detection = Detect();

        // Assert
        (detection.Kind.ToString(), detection.Match!.Confidence).Should().Be(("unity", EngineConfidence.Low));
    }

    [Fact]
    public void Unity_markers_match_whatever_their_case()
    {
        // Arrange
        _dir.File("game.exe", "MZ").File("unityplayer.dll", "MZ").File("game_data/GLOBALGAMEMANAGERS", "x").Dir("game_data/managed");

        // Act
        var detection = Detect();

        // Assert
        (detection.Kind.ToString(), detection.Match!.Confidence).Should().Be(("unity:mono", EngineConfidence.High));
    }

    [Fact]
    public void Unreal_without_engine_folder_has_medium_confidence()
    {
        // Arrange
        _dir.UnrealWindows(engineFolder: false);

        // Act
        var detection = Detect();

        // Assert
        (detection.Kind.Engine, detection.Match!.Confidence).Should().Be((GameEngine.Unreal, EngineConfidence.Medium));
    }

    [Fact]
    public void Unreal_built_from_a_custom_branch_has_no_version()
    {
        // Arrange
        _dir.UnrealWindows(branch: "++Studio+Main");

        // Act
        var detection = Detect();

        // Assert
        detection.Kind.Should().Be(new EngineKind(GameEngine.Unreal));
    }

    [Fact]
    public void Unreal_marker_is_the_pak_file()
    {
        // Arrange
        _dir.UnrealWindows();

        // Act
        var detection = Detect();

        // Assert
        detection.Match!.Marker.Should().Be("Game/Content/Paks/Game-Windows.pak");
    }

    [Fact]
    public void Helper_executables_are_not_the_main_executable()
    {
        // Arrange
        _dir.RpgMakerMz(exe: "zGame.exe").File("crashpad_handler.exe", "MZ").File("unins000.exe", "MZ");

        // Act
        var detection = Detect();

        // Assert
        detection.Match!.MainExecutable.Should().Be("zGame.exe");
    }

    [Fact]
    public void Renpy_main_executable_is_the_launcher_named_after_the_script()
    {
        // Arrange
        _dir.RenPy(name: "MyNovel").File("AAA.exe", "MZ");

        // Act
        var detection = Detect();

        // Assert
        detection.Match!.MainExecutable.Should().Be("MyNovel.exe");
    }

    [Fact]
    public void Godot_executable_with_a_truncated_trailer_is_not_godot()
    {
        // Arrange
        _dir.File("Game.exe", [.. "MZ"u8, .. "GDPC"u8]);

        // Act
        var detection = Detect();

        // Assert
        detection.Kind.Should().Be(EngineKind.Unknown);
    }
}
