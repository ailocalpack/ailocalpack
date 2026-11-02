using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Toolkit.AntiCheat;

namespace AiLocalPack.Toolkit.Tests.AntiCheat;

public sealed class AntiCheatDetectorTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AntiCheatDetector _detector = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("EasyAntiCheat/EasyAntiCheat_x64.dll", "eac", "EasyAntiCheat/")]
    [InlineData("EasyAntiCheat_EOS_Setup.exe", "eac", "EasyAntiCheat_EOS_Setup.exe")]
    [InlineData("start_protected_game.exe", "eac", "start_protected_game.exe")]
    [InlineData("BattlEye/BEClient_x64.dll", "battleye", "BattlEye/")]
    [InlineData("BEService_x64.exe", "battleye", "BEService_x64.exe")]
    [InlineData("DayZ_BE.exe", "battleye", "DayZ_BE.exe")]
    [InlineData("Riot Vanguard/vgc.exe", "vanguard", "Riot Vanguard/")]
    [InlineData("vgk.sys", "vanguard", "vgk.sys")]
    [InlineData("GameGuard/GameMon.des", "gameguard", "GameGuard/")]
    [InlineData("GameGuard.des", "gameguard", "GameGuard.des")]
    [InlineData("XIGNCODE/x3.xem", "xigncode", "XIGNCODE/")]
    [InlineData("xxd-0.xem", "xigncode", "xxd-0.xem")]
    [InlineData("pb/pbcl.dll", "punkbuster", "pb/pbcl.dll")]
    [InlineData("PnkBstrA.exe", "punkbuster", "PnkBstrA.exe")]
    [InlineData("mhypbase.dll", "mhyprot", "mhypbase.dll")]
    [InlineData("mhyprot3.sys", "mhyprot", "mhyprot3.sys")]
    public void Marker_is_detected(string file, string id, string marker)
    {
        // Arrange
        _dir.File(file, "x").File("Game.exe", "MZ");

        // Act
        var detection = _detector.Detect(_dir.Path);

        // Assert
        detection.Match.Should().BeEquivalentTo(new { Id = id, Marker = marker });
    }

    [Fact]
    public void Every_bundled_anti_cheat_has_a_test_marker()
    {
        // Arrange
        var tested = new[] { "eac", "battleye", "vanguard", "gameguard", "xigncode", "punkbuster", "mhyprot" };

        // Act
        var bundled = AntiCheatPolicy.Bundled.AntiCheats.Select(r => r.Id);

        // Assert
        bundled.Should().BeEquivalentTo(tested);
    }

    [Fact]
    public void Match_has_the_display_name()
    {
        // Arrange
        _dir.Dir("EasyAntiCheat");

        // Act
        var detection = _detector.Detect(_dir.Path);

        // Assert
        detection.Match!.Name.Should().Be("Easy Anti-Cheat");
    }

    [Fact]
    public void Nested_marker_is_found()
    {
        // Arrange
        _dir.UnrealWindows().File("Game/Binaries/Win64/EasyAntiCheat/Settings.json", "{}");

        // Act
        var detection = _detector.Detect(_dir.Path);

        // Assert
        detection.Match.Should().Be(new AntiCheatMatch("eac", "Easy Anti-Cheat", "Game/Binaries/Win64/EasyAntiCheat/"));
    }

    [Fact]
    public void Marker_inside_a_mac_bundle_is_found()
    {
        // Arrange
        _dir.UnityMac().Dir("Game.app/Contents/Resources/BattlEye");

        // Act
        var detection = _detector.Detect(_dir.Path);

        // Assert
        detection.Match!.Marker.Should().Be("Game.app/Contents/Resources/BattlEye/");
    }

    [Fact]
    public void Marker_names_ignore_case()
    {
        // Arrange
        _dir.File("easyanticheat/settings.json", "{}");

        // Act
        var detection = _detector.Detect(_dir.Path);

        // Assert
        detection.Match!.Marker.Should().Be("easyanticheat/");
    }

    [Fact]
    public void Folder_marker_does_not_match_a_file_of_that_name()
    {
        // Arrange
        _dir.File("BattlEye", "not a folder");

        // Act
        var detection = _detector.Detect(_dir.Path);

        // Assert
        detection.Match.Should().BeNull();
    }

    [Fact]
    public void Path_marker_needs_its_parent_folder()
    {
        // Arrange
        _dir.File("pbcl.dll", "x").Dir("pb");

        // Act
        var detection = _detector.Detect(_dir.Path);

        // Assert
        detection.Match.Should().BeNull();
    }

    [Fact]
    public void Game_without_markers_has_no_anti_cheat()
    {
        // Arrange
        _dir.UnityWindows();

        // Act
        var detection = _detector.Detect(_dir.Path);

        // Assert
        (detection.Match, detection.StoredId).Should().Be(((AntiCheatMatch?)null, "none"));
    }

    [Fact]
    public void Several_anti_cheats_are_listed_shallowest_first()
    {
        // Arrange
        _dir.Dir("Bin/Win64/EasyAntiCheat").Dir("BattlEye");

        // Act
        var detection = _detector.Detect(_dir.Path);

        // Assert
        detection.Matches.Select(m => m.Id).Should().Equal("battleye", "eac");
    }

    [Fact]
    public void Marker_deeper_than_the_limit_is_ignored()
    {
        // Arrange
        var deep = string.Join('/', Enumerable.Repeat("d", AntiCheatDetector.MaxDepth + 1));
        _dir.Dir($"{deep}/EasyAntiCheat");

        // Act
        var detection = _detector.Detect(_dir.Path);

        // Assert
        detection.Match.Should().BeNull();
    }

    [Fact]
    public void Missing_folder_has_no_anti_cheat()
    {
        // Arrange
        var missing = _dir.Combine("not-there");

        // Act
        var detection = _detector.Detect(missing);

        // Assert
        detection.Should().Be(AntiCheatDetection.None);
    }

    [Fact]
    public void Custom_policy_rules_are_used()
    {
        // Arrange
        var policy = AntiCheatPolicy.Parse("""
            { "schemaVersion": 1, "antiCheats": [ { "id": "acme", "name": "Acme Guard", "markers": ["acme*.dll"] } ] }
            """);
        _dir.File("AcmeGuard64.dll", "x").Dir("EasyAntiCheat");

        // Act
        var detection = new AntiCheatDetector(policy).Detect(_dir.Path);

        // Assert
        detection.Matches.Should().Equal(new AntiCheatMatch("acme", "Acme Guard", "AcmeGuard64.dll"));
    }
}
