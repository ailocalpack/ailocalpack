using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Core.Library;

/// <summary>A pack the quick action installs, with the check that chose it.</summary>
public sealed record PackChoice(RegistryPack Pack, PackCompatibility Compatibility);

/// <summary>
/// The pack <c>Install</c> uses on a library card and in the game page's header (design §1, §3): the best
/// compatible one.
/// </summary>
public static class BestPack
{
    /// <summary>
    /// Checks <paramref name="ranked"/> in order (<see cref="LocalizationStatuses.Rank"/>) and returns the first
    /// compatible pack; when none is, the first probably compatible one; <c>null</c> when every pack is incompatible or
    /// couldn't be checked (<paramref name="check"/> returned <c>null</c>: manifest unreadable). Stops at the first
    /// compatible pack, so a list headed by the pack made for the player's build costs one check.
    /// </summary>
    public static async Task<PackChoice?> PickAsync(
        IEnumerable<RegistryPack> ranked,
        Func<RegistryPack, CancellationToken, Task<PackCompatibility?>> check,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(check);
        PackChoice? probably = null;
        foreach (var pack in ranked)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compatibility = await check(pack, cancellationToken).ConfigureAwait(false);
            switch (compatibility?.Verdict)
            {
                case PackCompatibilityVerdict.Compatible:
                    return new PackChoice(pack, compatibility);
                case PackCompatibilityVerdict.ProbablyCompatible:
                    probably ??= new PackChoice(pack, compatibility);
                    break;
            }
        }

        return probably;
    }
}
