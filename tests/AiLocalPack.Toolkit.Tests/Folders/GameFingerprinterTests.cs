using AiLocalPack.Core.Games;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Folders;

namespace AiLocalPack.Toolkit.Tests.Folders;

public sealed class GameFingerprinterTests : IDisposable
{
    private static readonly EngineKind UnityMono = new(GameEngine.Unity, EngineVariants.Mono);
    private static readonly EngineKind RpgMakerMv = new(GameEngine.RpgMaker, EngineVariants.RpgMakerMv);

    private readonly TempDir _dir = new();
    private readonly GameFingerprinter _fingerprinter = new();

    public void Dispose() => _dir.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GameId Fingerprint(string folder, EngineKind engine, string? exe = "Game.exe") =>
        _fingerprinter.Fingerprint(_dir.Combine(folder), engine, exe, cancellationToken: Ct);

    private GameVersion? Version(string folder, EngineKind engine, string? exe = "Game.exe") =>
        _fingerprinter.Version(_dir.Combine(folder), engine, exe, cancellationToken: Ct);

    private TempDir RpgMaker(string root, string title = "Moss and Mortar")
    {
        var prefix = root.Length == 0 ? "" : root + "/";
        return _dir.RpgMakerMv(root)
            .File($"{prefix}package.json", "{\"name\":\"rmmv-game\"}")
            .File($"{prefix}www/data/System.json", $"{{\"gameTitle\":\"{title}\"}}")
            .File($"{prefix}www/data/Map001.json", "{\"events\":[]}")
            .File($"{prefix}www/js/plugins.js", "var $plugins = [];");
    }

    /// <summary>Builds a layout at the top of the temp folder and returns its engine.</summary>
    private EngineKind Build(string layout)
    {
        switch (layout)
        {
            case "unity-mono":
                _dir.UnityWindows().File("Game_Data/resources.assets", "a").File("Game_Data/app.info", "x").File("Game_Data/StreamingAssets/v.mp4", "x");
                return UnityMono;
            case "unity-il2cpp":
                _dir.UnityWindows(il2Cpp: true);
                return new EngineKind(GameEngine.Unity, EngineVariants.Il2Cpp);
            case "unity-mac":
                _dir.UnityMac();
                return UnityMono;
            case "unreal":
                _dir.UnrealWindows().File("Engine/Content/Paks/engine.pak", "x");
                return new EngineKind(GameEngine.Unreal, EngineVariants.Unreal5);
            case "rpgmaker":
                RpgMaker("");
                return RpgMakerMv;
            case "renpy":
                _dir.RenPy().File("game/scripts/day1.rpyc", "x").File("renpy/common/00start.rpy", "x");
                return new EngineKind(GameEngine.RenPy);
            case "godot":
                _dir.GodotWindows();
                return new EngineKind(GameEngine.Godot, "4");
            default:
                _dir.File("Game.exe", "MZ").File("data/strings.json", "{}");
                return EngineKind.Unknown;
        }
    }

    [Fact]
    public void The_same_game_at_two_paths_has_the_same_fingerprint()
    {
        // Arrange
        RpgMaker("C/Games/Moss");
        RpgMaker("D/Other place/Moss and Mortar");

        // Act
        var (first, second) = (Fingerprint("C/Games/Moss", RpgMakerMv), Fingerprint("D/Other place/Moss and Mortar", RpgMakerMv));

        // Assert
        first.Should().Be(second);
    }

    [Fact]
    public void A_fingerprint_is_an_fp_game_id()
    {
        // Arrange
        RpgMaker("Moss");

        // Act
        var id = Fingerprint("Moss", RpgMakerMv);

        // Assert
        id.Kind.Should().Be(GameIdKind.Fingerprint);
    }

    [Fact]
    public void A_changed_key_file_changes_the_version_but_not_the_fingerprint()
    {
        // Arrange
        RpgMaker("Moss");
        var (idBefore, versionBefore) = (Fingerprint("Moss", RpgMakerMv), Version("Moss", RpgMakerMv));
        _dir.File("Moss/www/data/Map001.json", "{\"events\":[{\"text\":\"translated\"}]}");

        // Act
        var (idAfter, versionAfter) = (Fingerprint("Moss", RpgMakerMv), Version("Moss", RpgMakerMv));

        // Assert
        (idAfter == idBefore, versionAfter == versionBefore).Should().Be((true, false));
    }

    [Fact]
    public void The_same_game_at_two_paths_has_the_same_version()
    {
        // Arrange
        _dir.UnityWindows("A/Moss").UnityWindows("B/Moss");

        // Act
        var (first, second) = (Version("A/Moss", UnityMono), Version("B/Moss", UnityMono));

        // Assert
        first.Should().Be(second);
    }

    [Fact]
    public void Rpg_maker_games_with_the_stock_executable_differ_by_title()
    {
        // Arrange
        RpgMaker("Moss", title: "Moss and Mortar");
        RpgMaker("Wren", title: "Wren and Whistle");

        // Act
        var (moss, wren) = (Fingerprint("Moss", RpgMakerMv), Fingerprint("Wren", RpgMakerMv));

        // Assert
        moss.Should().NotBe(wren);
    }

    [Fact]
    public void Unity_games_with_the_stock_executable_differ_by_app_info()
    {
        // Arrange
        _dir.UnityWindows("Moss").File("Moss/Game_Data/app.info", "Studio\nMoss and Mortar")
            .UnityWindows("Wren").File("Wren/Game_Data/app.info", "Studio\nWren and Whistle");

        // Act
        var (moss, wren) = (Fingerprint("Moss", UnityMono), Fingerprint("Wren", UnityMono));

        // Assert
        moss.Should().NotBe(wren);
    }

    [Fact]
    public void A_different_main_executable_changes_the_fingerprint()
    {
        // Arrange
        _dir.GodotWindows("Moss").GodotWindows("Wren").File("Wren/Game.exe", "MZ other build");
        var godot = new EngineKind(GameEngine.Godot, "4");

        // Act
        var (moss, wren) = (Fingerprint("Moss", godot), Fingerprint("Wren", godot));

        // Assert
        moss.Should().NotBe(wren);
    }

    [Theory]
    [InlineData("unity-mono", "Game.exe", "Game.exe|Game_Data/globalgamemanagers|Game_Data/Managed/Assembly-CSharp.dll|Game_Data/resources.assets")]
    [InlineData("unity-il2cpp", "Game.exe", "Game.exe|Game_Data/globalgamemanagers|Game_Data/il2cpp_data/Metadata/global-metadata.dat")]
    [InlineData("unity-mac", "Game.app", "Game.app/Contents/MacOS/Game|Game.app/Contents/Resources/Data/Managed/Assembly-CSharp.dll|Game.app/Contents/Resources/Data/globalgamemanagers")]
    [InlineData("unreal", "Game/Binaries/Win64/Game-Win64-Shipping.exe", "Game/Binaries/Win64/Game-Win64-Shipping.exe|Game/Content/Paks/Game-Windows.pak")]
    [InlineData("rpgmaker", "Game.exe", "Game.exe|www/data/Map001.json|www/data/System.json|www/js/plugins.js")]
    [InlineData("renpy", "Game.exe", "Game.exe|game/archive.rpa|game/scripts/day1.rpyc")]
    [InlineData("godot", "Game.exe", "Game.exe|Game.pck")]
    [InlineData("unknown", "Game.exe", "Game.exe")]
    public void Version_files_are_the_executable_and_the_files_packs_touch(string layout, string exe, string expected)
    {
        // Arrange
        var engine = Build(layout);

        // Act
        var files = GameFingerprinter.VersionFiles(_dir.Path, engine, exe);

        // Assert
        files.Should().BeEquivalentTo(expected.Split('|'));
    }

    [Fact]
    public void A_missing_main_executable_throws_an_io_error()
    {
        // Arrange
        _dir.File("Moss/data.bin", "x");

        // Act
        var act = () => Fingerprint("Moss", EngineKind.Unknown, exe: "Moss.exe");

        // Assert
        act.Should().Throw<IOException>();
    }

    [Fact]
    public void A_game_without_executable_or_key_files_cannot_be_fingerprinted()
    {
        // Arrange
        _dir.File("Moss/readme.txt", "x");

        // Act
        var act = () => Fingerprint("Moss", EngineKind.Unknown, exe: null);

        // Assert
        act.Should().Throw<IOException>();
    }

    [Fact]
    public void A_game_without_an_executable_is_fingerprinted_by_its_content()
    {
        // Arrange
        _dir.File("Moss/renpy/__init__.py", "#").File("Moss/game/script.rpyc", "moss")
            .File("Wren/renpy/__init__.py", "#").File("Wren/game/script.rpyc", "wren");
        var renPy = new EngineKind(GameEngine.RenPy);

        // Act
        var (moss, wren) = (Fingerprint("Moss", renPy, exe: null), Fingerprint("Wren", renPy, exe: null));

        // Assert
        moss.Should().NotBe(wren);
    }

    [Fact]
    public void Hashing_reports_files_done_of_total()
    {
        // Arrange
        RpgMaker("Moss");
        var progress = new RecordingProgress<HashProgress>();

        // Act
        _fingerprinter.Version(_dir.Combine("Moss"), RpgMakerMv, "Game.exe", progress, Ct);

        // Assert
        progress.Reports.Should().Equal(
            new HashProgress(0, 4), new HashProgress(1, 4), new HashProgress(2, 4), new HashProgress(3, 4), new HashProgress(4, 4));
    }

    [Fact]
    public void Cancelling_throws()
    {
        // Arrange
        RpgMaker("Moss");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var act = () => _fingerprinter.Version(_dir.Combine("Moss"), RpgMakerMv, "Game.exe", cancellationToken: cts.Token);

        // Assert
        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Hashes_go_through_the_cache()
    {
        // Arrange
        RpgMaker("Moss");
        var cache = new FileHashCache();
        var fingerprinter = new GameFingerprinter(cache);

        // Act
        fingerprinter.Version(_dir.Combine("Moss"), RpgMakerMv, "Game.exe", cancellationToken: Ct);

        // Assert
        cache.Count.Should().Be(4);
    }

    [Fact]
    public void A_found_game_is_fingerprinted_with_its_detected_engine_and_executable()
    {
        // Arrange
        RpgMaker("Moss");
        var game = new FolderRecognizer().Scan(_dir.Path, cancellationToken: Ct).Games.Single();

        // Act
        var id = _fingerprinter.Fingerprint(game, cancellationToken: Ct);

        // Assert
        id.Should().Be(Fingerprint("Moss", RpgMakerMv));
    }
}
