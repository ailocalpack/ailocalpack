using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;

namespace AiLocalPack.Core.Tests.AntiCheat;

public sealed class AntiCheatPolicyTests
{
    private static readonly GameId Shooter = GameId.Steam(1001);
    private static readonly GameId Moddable = GameId.Steam(1002);
    private static readonly GameId Kernel = GameId.Steam(1003);

    private static AntiCheatPolicy Policy() => AntiCheatPolicy.Parse("""
        {
          "schemaVersion": 1,
          "antiCheats": [
            { "id": "eac", "name": "Easy Anti-Cheat", "markers": ["EasyAntiCheat/"] },
            { "id": "vanguard", "name": "Riot Vanguard", "markers": ["vgc.exe"] },
          ],
          // Comments and trailing commas are allowed.
          "games": [ { "game": "steam:1003", "antiCheat": "vanguard" } ],
          "allowed": [ { "game": "steam:1002", "note": "Studio supports mods" } ],
        }
        """);

    private static string Valid(string antiCheats) =>
        $$"""{ "schemaVersion": 1, "antiCheats": [ {{antiCheats}} ] }""";

    [Fact]
    public void Bundled_policy_loads()
    {
        // Arrange
        // (bundled resource)

        // Act
        var policy = AntiCheatPolicy.Bundled;

        // Assert
        policy.Find("eac")!.Name.Should().Be("Easy Anti-Cheat");
    }

    [Fact]
    public void Detected_anti_cheat_blocks_the_game()
    {
        // Arrange
        var policy = Policy();

        // Act
        var verdict = policy.Resolve(Shooter, "eac");

        // Assert
        verdict.Should().BeEquivalentTo(new { AntiCheatId = "eac", Name = "Easy Anti-Cheat", IsAllowed = false, IsBlocked = true });
    }

    [Fact]
    public void Whitelisted_game_is_not_blocked()
    {
        // Arrange
        var policy = Policy();

        // Act
        var verdict = policy.Resolve(Moddable, "eac");

        // Assert
        (verdict.AntiCheatId, verdict.IsAllowed, verdict.IsBlocked).Should().Be(("eac", true, false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("none")]
    public void Game_without_anti_cheat_is_not_blocked(string? detected)
    {
        // Arrange
        var policy = Policy();

        // Act
        var verdict = policy.Resolve(Shooter, detected);

        // Assert
        verdict.Should().Be(AntiCheatVerdict.None);
    }

    [Fact]
    public void Known_game_is_blocked_without_files()
    {
        // Arrange
        var policy = Policy();

        // Act
        var verdict = policy.Resolve(Kernel, "none");

        // Assert
        (verdict.Name, verdict.IsBlocked).Should().Be(("Riot Vanguard", true));
    }

    [Fact]
    public void Game_record_is_resolved_by_its_stored_anti_cheat()
    {
        // Arrange
        var game = new Game(Shooter, "Shooter", GameSource.Steam, "/games/shooter", "/steam", null, null, "eac", null, true);

        // Act
        var verdict = Policy().Resolve(game);

        // Assert
        verdict.IsBlocked.Should().BeTrue();
    }

    [Fact]
    public void Unknown_stored_id_shows_the_id_as_name()
    {
        // Arrange
        var policy = Policy();

        // Act
        var verdict = policy.Resolve(Shooter, "future-ac");

        // Assert
        (verdict.Name, verdict.IsBlocked).Should().Be(("future-ac", true));
    }

    [Theory]
    [InlineData("""{ "id": "EAC", "name": "x", "markers": ["a"] }""")]
    [InlineData("""{ "id": "none", "name": "x", "markers": ["a"] }""")]
    [InlineData("""{ "id": "eac", "name": " ", "markers": ["a"] }""")]
    [InlineData("""{ "id": "eac", "name": "x", "markers": [] }""")]
    [InlineData("""{ "id": "eac", "name": "x", "markers": ["../a"] }""")]
    [InlineData("""{ "id": "eac", "name": "x", "markers": ["a\\b"] }""")]
    [InlineData("""{ "id": "eac", "name": "x", "markers": ["a//b"] }""")]
    [InlineData("""{ "id": "eac", "name": "x", "markers": ["*/b.dll"] }""")]
    [InlineData("""{ "id": "eac", "name": "x", "markers": ["a"] }, { "id": "eac", "name": "y", "markers": ["b"] }""")]
    public void Invalid_rule_is_rejected(string antiCheats)
    {
        // Arrange
        var json = Valid(antiCheats);

        // Act
        var act = () => AntiCheatPolicy.Parse(json);

        // Assert
        act.Should().Throw<AntiCheatPolicyException>();
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 2, "antiCheats": [] }""")]
    [InlineData("""{ "schemaVersion": 1, "games": [ { "game": "steam:1", "antiCheat": "missing" } ] }""")]
    [InlineData("""{ "schemaVersion": 1, "allowed": [ { "game": "steam:01" } ] }""")]
    [InlineData("""{ "schemaVersion": 1, "antiCheats": [ { "id": "eac", "name": "x", "markers": ["a"] } ], "games": [ { "game": "steam:1", "antiCheat": "eac" }, { "game": "steam:1", "antiCheat": "eac" } ] }""")]
    [InlineData("null")]
    [InlineData("{ not json")]
    public void Invalid_file_is_rejected(string json)
    {
        // Arrange
        // (json)

        // Act
        var act = () => AntiCheatPolicy.Parse(json);

        // Assert
        act.Should().Throw<AntiCheatPolicyException>();
    }

    [Fact]
    public void Folder_path_and_wildcard_markers_are_accepted()
    {
        // Arrange
        var json = Valid("""{ "id": "eac", "name": "x", "markers": ["EasyAntiCheat/", "pb/pbcl.dll", "*_BE.exe"] }""");

        // Act
        var policy = AntiCheatPolicy.Parse(json);

        // Assert
        policy.Find("eac")!.Markers.Should().Equal("EasyAntiCheat/", "pb/pbcl.dll", "*_BE.exe");
    }
}
