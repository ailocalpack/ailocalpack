using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Core.Library;

/// <summary>Why install and generation refuse a game (design §3a).</summary>
/// <param name="Reason"><see cref="LocalizationBlock.AntiCheat"/> or <see cref="LocalizationBlock.OptOut"/>.</param>
/// <param name="AntiCheatId">The blocking anti-cheat (<see cref="LocalizationBlock.AntiCheat"/> only).</param>
/// <param name="AntiCheatName">Its display name.</param>
public sealed record GameBlock(LocalizationBlock Reason, string? AntiCheatId = null, string? AntiCheatName = null);

/// <summary>What a blocked game is refused.</summary>
public enum BlockedOperation
{
    Install,
    Generate,
}

/// <summary>
/// Install or generation was asked for a blocked game. The services throw it whatever the UI showed, so a stale screen,
/// a quick action or a CLI call can't get past the block.
/// </summary>
public sealed class GameBlockedException(GameId game, BlockedOperation operation, GameBlock block)
    : InvalidOperationException(Describe(game, operation, block))
{
    public GameId Game { get; } = game;

    public BlockedOperation Operation { get; } = operation;

    public GameBlock Block { get; } = block;

    private static string Describe(GameId game, BlockedOperation operation, GameBlock block)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(block);
        var what = operation == BlockedOperation.Install ? "installed" : "generated";
        var why = block.Reason == LocalizationBlock.OptOut
            ? "the studio asked not to distribute fan translations"
            : $"{block.AntiCheatName ?? block.AntiCheatId ?? "an anti-cheat"} was detected";
        return $"Packs can't be {what} for {game}: {why}.";
    }
}

/// <summary>Decides whether a game is blocked: one rule for the library, the game page and the services. Pure.</summary>
public static class GameBlocks
{
    /// <summary>
    /// The registry's opt-out first, then an anti-cheat the policy blocks unless the registry allows the game (officially
    /// mod-friendly); <c>null</c> when nothing blocks it. Without an index only the anti-cheat counts.
    /// </summary>
    public static GameBlock? Resolve(GameId game, AntiCheatVerdict antiCheat, RegistryIndex? index)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(antiCheat);
        var registryGame = index?.Find(game);
        if (registryGame is { OptOut: true })
        {
            return new GameBlock(LocalizationBlock.OptOut);
        }

        return antiCheat.IsBlocked && registryGame is not { AntiCheatAllowed: true }
            ? new GameBlock(LocalizationBlock.AntiCheat, antiCheat.AntiCheatId, antiCheat.Name)
            : null;
    }

    /// <inheritdoc cref="Resolve(GameId, AntiCheatVerdict, RegistryIndex?)"/>
    public static GameBlock? Resolve(Game game, AntiCheatPolicy policy, RegistryIndex? index)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(policy);
        return Resolve(game.Id, policy.Resolve(game), index);
    }

    /// <summary>
    /// The guard the install (tasks 27, 29) and generation (task 47) services call before touching anything: throws
    /// <see cref="GameBlockedException"/> for a blocked game.
    /// </summary>
    public static void EnsureAllowed(Game game, AntiCheatPolicy policy, RegistryIndex? index, BlockedOperation operation)
    {
        if (Resolve(game, policy, index) is { } block)
        {
            throw new GameBlockedException(game.Id, operation, block);
        }
    }
}
