using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Core.Library;

/// <summary>Where a game stands for one target language (the library badge, design §1).</summary>
public enum LocalizationStatusKind
{
    /// <summary>No pack list yet (first fetch running, or the registry never answered and nothing is saved).</summary>
    Unknown,

    /// <summary>A pack for the language is installed and still fits the game.</summary>
    Installed,

    /// <summary>The registry lists a pack for the game and language.</summary>
    PackAvailable,

    /// <summary>Nothing for the language; generation is possible.</summary>
    NoPack,

    /// <summary>The game updated and the installed pack no longer fits it.</summary>
    NeedsUpdate,

    /// <summary>A generation job for the language is running.</summary>
    Generating,

    /// <summary>A generation job for the language is paused (stopped, subscription limit).</summary>
    Paused,

    /// <summary>Install and generation are blocked: anti-cheat or the studio opted out.</summary>
    Blocked,
}

/// <summary>Why a game is <see cref="LocalizationStatusKind.Blocked"/>.</summary>
public enum LocalizationBlock
{
    None,

    /// <summary>An anti-cheat the policy blocks and the registry doesn't allow.</summary>
    AntiCheat,

    /// <summary>The registry's opt-out flag: the studio asked not to distribute fan translations.</summary>
    OptOut,
}

/// <summary>A pack installed for the game and language (task 29), as the library needs it.</summary>
/// <param name="BrokenByUpdate">The game updated and the pack no longer fits (task 33).</param>
/// <param name="Revision">The installed revision (the game page offers <c>Update to rev N</c> when the registry has a later one).</param>
public sealed record InstalledPackState(string PackId, bool BrokenByUpdate, int Revision = 1);

/// <summary>A generation job for the game and language (task 47), as the library needs it.</summary>
/// <param name="Percent">Progress, 0–100.</param>
/// <param name="PauseReason">Why it is paused, in the UI language ("subscription limit"); <c>null</c> = paused by the player.</param>
public sealed record GenerationJobState(int Percent, bool IsPaused, TimeSpan? Remaining = null, string? PauseReason = null);

/// <summary>
/// The status of one game for one language with the data the card's context line needs: the registry packs, the
/// one the quick action would install, the anti-cheat name, the job.
/// </summary>
/// <param name="Packs">The registry's packs for the game and language (any status), best first.</param>
/// <param name="AntiCheatName">Name of the blocking anti-cheat (<see cref="LocalizationBlock.AntiCheat"/>).</param>
public sealed record LocalizationStatus(
    LocalizationStatusKind Kind,
    LocalizationBlock Block,
    IReadOnlyList<RegistryPack> Packs,
    string? AntiCheatName = null,
    InstalledPackState? Installed = null,
    GenerationJobState? Job = null)
{
    public static LocalizationStatus Unknown { get; } = new(LocalizationStatusKind.Unknown, LocalizationBlock.None, []);

    /// <summary>The pack the card's <c>Install</c> uses (<see cref="LocalizationStatuses.Rank"/>); <c>null</c> when none.</summary>
    public RegistryPack? BestPack => Packs.Count > 0 ? Packs[0] : null;

    public bool IsBlocked => Kind == LocalizationStatusKind.Blocked;
}

/// <summary>Computes <see cref="LocalizationStatus"/> from the catalog game, the registry index and local state. Pure.</summary>
public static class LocalizationStatuses
{
    /// <summary>
    /// In this order: opt-out, then a blocking anti-cheat (unless the registry allows the game) → blocked
    /// (<see cref="GameBlocks.Resolve(GameId, AntiCheatVerdict, RegistryIndex?)"/>); a generation
    /// job → generating / paused; an installed pack → needs update when an update broke it, else installed; no index →
    /// unknown; packs for the language → pack available; else no pack.
    /// </summary>
    /// <param name="language">An exact <see cref="Languages.GameLanguages.Tags"/> tag.</param>
    /// <param name="antiCheat">What the anti-cheat policy says about the game.</param>
    /// <param name="index">The registry index (fresh or the saved copy); <c>null</c> when there is none.</param>
    public static LocalizationStatus Resolve(
        Game game,
        string language,
        AntiCheatVerdict antiCheat,
        RegistryIndex? index,
        InstalledPackState? installed = null,
        GenerationJobState? job = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentNullException.ThrowIfNull(antiCheat);

        var registryGame = index?.Find(game.Id);
        IReadOnlyList<RegistryPack> packs = registryGame is null ? [] : Rank(registryGame.PacksIn(language), game);

        if (GameBlocks.Resolve(game.Id, antiCheat, index) is { } block)
        {
            return new(LocalizationStatusKind.Blocked, block.Reason, packs, block.AntiCheatName);
        }

        if (job is not null)
        {
            var kind = job.IsPaused ? LocalizationStatusKind.Paused : LocalizationStatusKind.Generating;
            return new(kind, LocalizationBlock.None, packs, Installed: installed, Job: job);
        }

        if (installed is not null)
        {
            var kind = installed.BrokenByUpdate ? LocalizationStatusKind.NeedsUpdate : LocalizationStatusKind.Installed;
            return new(kind, LocalizationBlock.None, packs, Installed: installed);
        }

        if (index is null)
        {
            return LocalizationStatus.Unknown;
        }

        return new(packs.Count > 0 ? LocalizationStatusKind.PackAvailable : LocalizationStatusKind.NoPack, LocalizationBlock.None, packs);
    }

    /// <summary>
    /// Packs best first: made for the game's store build, then <c>verified</c>, more likes, more "works" than "doesn't
    /// work" reports, more recently updated, then by ID so the order is stable.
    /// </summary>
    public static IReadOnlyList<RegistryPack> Rank(IEnumerable<RegistryPack> packs, Game game)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(game);
        var build = game.Version?.BuildId;
        return
        [
            .. packs
                .OrderByDescending(p => build is not null && p.BuildIds.Contains(build, StringComparer.Ordinal))
                .ThenByDescending(p => p.Status == PackStatus.Verified)
                .ThenByDescending(p => p.Rating.Likes)
                .ThenByDescending(p => p.Rating.Works - p.Rating.DoesNotWork)
                .ThenByDescending(p => p.Updated)
                .ThenBy(p => p.Id, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Place in the <c>Important first</c> order (lower first): needs update, paused, generating, pack available,
    /// installed, no pack (and unknown), blocked.
    /// </summary>
    public static int Importance(LocalizationStatusKind kind) => kind switch
    {
        LocalizationStatusKind.NeedsUpdate => 0,
        LocalizationStatusKind.Paused => 1,
        LocalizationStatusKind.Generating => 2,
        LocalizationStatusKind.PackAvailable => 3,
        LocalizationStatusKind.Installed => 4,
        LocalizationStatusKind.NoPack or LocalizationStatusKind.Unknown => 5,
        _ => 6,
    };
}
