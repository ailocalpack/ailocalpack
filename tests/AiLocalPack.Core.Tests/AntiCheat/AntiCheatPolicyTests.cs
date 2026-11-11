using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Registry;

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

    private static readonly GameId Listed = GameId.Steam(1004);

    /// <summary>An index carrying registry policy: rules, a game listed with an anti-cheat, an allowed game.</summary>
    private static RegistryIndex Registry(IReadOnlyList<AntiCheatRule>? rules = null, string? listed = null, GameId? allowed = null)
    {
        var games = new Dictionary<GameId, RegistryGame>();
        if (listed is not null)
        {
            games[Listed] = new RegistryGame(Listed, false, false, new Dictionary<string, IReadOnlyList<RegistryPack>>()) { AntiCheat = listed };
        }

        if (allowed is { } id)
        {
            games[id] = new RegistryGame(id, false, true, new Dictionary<string, IReadOnlyList<RegistryPack>>()) { AntiCheat = games.GetValueOrDefault(id)?.AntiCheat };
        }

        return new RegistryIndex(DateTimeOffset.UnixEpoch, games) { AntiCheats = rules ?? [] };
    }

    [Fact]
    public void Registry_rule_replaces_the_bundled_rule_with_its_id()
    {
        // Arrange
        var index = Registry([new AntiCheatRule("eac", "Easy Anti-Cheat (EOS)", ["EOSBootstrapper.exe"])]);

        // Act
        var merged = Policy().WithRegistry(index);

        // Assert
        merged.AntiCheats.Select(r => (r.Id, r.Markers[0])).Should().Equal(("eac", "EOSBootstrapper.exe"), ("vanguard", "vgc.exe"));
    }

    [Fact]
    public void Registry_rule_with_a_new_id_is_added_after_the_bundled_ones()
    {
        // Arrange
        var index = Registry([new AntiCheatRule("acme-guard", "Acme Guard", ["AcmeGuard/"])]);

        // Act
        var merged = Policy().WithRegistry(index);

        // Assert
        merged.AntiCheats.Select(r => r.Id).Should().Equal("eac", "vanguard", "acme-guard");
    }

    [Fact]
    public void Game_the_registry_lists_is_blocked_without_files()
    {
        // Arrange
        var index = Registry(listed: "vanguard");

        // Act
        var verdict = Policy().WithRegistry(index).Resolve(Listed, AntiCheatPolicy.NoneId);

        // Assert
        verdict.Should().BeEquivalentTo(new { AntiCheatId = "vanguard", Name = "Riot Vanguard", IsBlocked = true });
    }

    [Fact]
    public void Registry_whitelist_beats_detection()
    {
        // Arrange
        var index = Registry(allowed: Shooter);

        // Act
        var verdict = Policy().WithRegistry(index).Resolve(Shooter, "eac");

        // Assert
        verdict.IsBlocked.Should().BeFalse();
    }

    [Fact]
    public void Registry_whitelist_beats_a_game_the_registry_lists()
    {
        // Arrange
        var index = Registry(listed: "vanguard", allowed: Listed);

        // Act
        var verdict = Policy().WithRegistry(index).Resolve(Listed, AntiCheatPolicy.NoneId);

        // Assert
        verdict.IsBlocked.Should().BeFalse();
    }

    [Fact]
    public void Bundled_whitelist_still_counts_with_a_registry()
    {
        // Arrange
        var index = Registry([new AntiCheatRule("acme-guard", "Acme Guard", ["AcmeGuard/"])]);

        // Act
        var verdict = Policy().WithRegistry(index).Resolve(Moddable, "eac");

        // Assert
        verdict.IsBlocked.Should().BeFalse();
    }

    [Fact]
    public void Without_an_index_the_policy_is_unchanged()
    {
        // Arrange
        var policy = Policy();

        // Act
        var merged = policy.WithRegistry(null);

        // Assert
        merged.Should().BeSameAs(policy);
    }

    [Fact]
    public void Same_index_gives_the_same_merged_policy()
    {
        // Arrange
        var policy = Policy();
        var index = Registry(listed: "eac");

        // Act
        var merged = policy.WithRegistry(index);

        // Assert
        policy.WithRegistry(index).Should().BeSameAs(merged);
    }

    [Fact]
    public void Rules_stamp_changes_with_a_new_marker()
    {
        // Arrange
        var index = Registry([new AntiCheatRule("eac", "Easy Anti-Cheat", ["EasyAntiCheat/", "EOSBootstrapper.exe"])]);

        // Act
        var stamp = Policy().WithRegistry(index).RulesStamp;

        // Assert
        stamp.Should().NotBe(Policy().RulesStamp).And.HaveLength(16);
    }

    [Fact]
    public void Rules_stamp_ignores_the_game_lists()
    {
        // Arrange
        var index = Registry(listed: "eac", allowed: Moddable);

        // Act
        var stamp = Policy().WithRegistry(index).RulesStamp;

        // Assert
        stamp.Should().Be(Policy().RulesStamp);
    }

    [Fact]
    public void Games_list_may_name_an_anti_cheat_of_the_baseline()
    {
        // Arrange
        var json = """{ "schemaVersion": 1, "games": [ { "game": "steam:1004", "antiCheat": "eac" } ] }""";

        // Act
        var policy = AntiCheatPolicy.Parse(json, AntiCheatPolicy.Bundled);

        // Assert
        policy.KnownGames[Listed].Should().Be("eac");
    }
}
