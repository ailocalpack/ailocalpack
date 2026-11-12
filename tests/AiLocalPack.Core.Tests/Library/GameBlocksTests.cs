using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Core.Tests.Library;

/// <summary>Block resolution and the guard install and generation call (task 23).</summary>
public sealed class GameBlocksTests
{
    private static readonly GameId Id = GameId.Steam(620);

    private static readonly AntiCheatRule Eac = new("eac", "Easy Anti-Cheat", ["EasyAntiCheat/"]);

    private static Game Game(string? antiCheat = "none") =>
        new(Id, "Portal 2", GameSource.Steam, "/steam/steamapps/common/Portal 2", "/steam", GameVersion.FromBuildId("1000"), "unknown", antiCheat, null, true);

    private static AntiCheatPolicy Policy(bool allowed = false) => new(
        [Eac],
        new Dictionary<GameId, string>(),
        allowed ? new HashSet<GameId> { Id } : new HashSet<GameId>());

    private static RegistryIndex Index(bool optOut = false, bool antiCheatAllowed = false) => new(
        DateTimeOffset.UnixEpoch,
        new Dictionary<GameId, RegistryGame> { [Id] = new(Id, optOut, antiCheatAllowed, new Dictionary<string, IReadOnlyList<RegistryPack>>()) });

    [Fact]
    public void Detected_anti_cheat_blocks_the_game()
    {
        // Arrange
        var game = Game("eac");

        // Act
        var block = GameBlocks.Resolve(game, Policy(), Index());

        // Assert
        block.Should().Be(new GameBlock(LocalizationBlock.AntiCheat, "eac", "Easy Anti-Cheat"));
    }

    [Fact]
    public void Opt_out_blocks_a_game_without_anti_cheat()
    {
        // Arrange
        var game = Game("none");

        // Act
        var block = GameBlocks.Resolve(game, Policy(), Index(optOut: true));

        // Assert
        block.Should().Be(new GameBlock(LocalizationBlock.OptOut));
    }

    [Fact]
    public void Opt_out_is_named_before_the_anti_cheat()
    {
        // Arrange
        var game = Game("eac");

        // Act
        var block = GameBlocks.Resolve(game, Policy(), Index(optOut: true));

        // Assert
        block!.Reason.Should().Be(LocalizationBlock.OptOut);
    }

    [Fact]
    public void Registry_whitelist_lifts_the_anti_cheat_block()
    {
        // Arrange
        var game = Game("eac");

        // Act
        var block = GameBlocks.Resolve(game, Policy(), Index(antiCheatAllowed: true));

        // Assert
        block.Should().BeNull();
    }

    [Fact]
    public void Whitelist_does_not_lift_an_opt_out()
    {
        // Arrange
        var game = Game("eac");

        // Act
        var block = GameBlocks.Resolve(game, Policy(allowed: true), Index(optOut: true, antiCheatAllowed: true));

        // Assert
        block!.Reason.Should().Be(LocalizationBlock.OptOut);
    }

    [Fact]
    public void Install_of_a_game_with_anti_cheat_throws_a_blocked_error()
    {
        // Arrange
        var game = Game("eac");

        // Act
        var act = () => GameBlocks.EnsureAllowed(game, Policy(), Index(), BlockedOperation.Install);

        // Assert
        act.Should().Throw<GameBlockedException>()
            .Which.Should().BeEquivalentTo(new
            {
                Game = Id,
                Operation = BlockedOperation.Install,
                Block = new GameBlock(LocalizationBlock.AntiCheat, "eac", "Easy Anti-Cheat"),
                Message = "Packs can't be installed for steam:620: Easy Anti-Cheat was detected.",
            });
    }

    [Fact]
    public void Generation_for_an_opted_out_game_throws_a_blocked_error()
    {
        // Arrange
        var game = Game("none");

        // Act
        var act = () => GameBlocks.EnsureAllowed(game, Policy(), Index(optOut: true), BlockedOperation.Generate);

        // Assert
        act.Should().Throw<GameBlockedException>()
            .Which.Should().BeEquivalentTo(new
            {
                Operation = BlockedOperation.Generate,
                Block = new GameBlock(LocalizationBlock.OptOut),
                Message = "Packs can't be generated for steam:620: the studio asked not to distribute fan translations.",
            });
    }

    [Fact]
    public void Whitelisted_game_passes_the_guard()
    {
        // Arrange
        var game = Game("eac");

        // Act
        var act = () => GameBlocks.EnsureAllowed(game, Policy(), Index(antiCheatAllowed: true), BlockedOperation.Install);

        // Assert
        act.Should().NotThrow();
    }
}
