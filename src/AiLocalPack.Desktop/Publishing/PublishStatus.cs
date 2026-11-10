using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Desktop.Publishing;

/// <summary>Where a local pack stands with the registry (design §7 strip states).</summary>
public enum PublishPhase
{
    /// <summary>Never sent: <c>Publish</c> is offered (once tried in game).</summary>
    NotPublished,

    /// <summary>Being sent now: the mini-steps Fork → Commit → Pull request.</summary>
    Sending,

    /// <summary>The last try stopped before a PR (offline, rate limit, refused): the reason and <c>Publish again</c>.</summary>
    Failed,

    /// <summary>The PR is open: registry CI checks the pack and merges it as <c>alpha</c> on its own.</summary>
    Checking,

    /// <summary>The registry index lists the sent revision: published as <c>alpha</c> (or later <c>verified</c>).</summary>
    Published,
}

/// <summary>The publish now running.</summary>
/// <param name="Key">The local pack (<see cref="PublishStatuses.KeyOf"/>).</param>
/// <param name="Step">The step it is at; <c>null</c> before the first (checking the account).</param>
/// <param name="TargetTitle">The registry pack's title: the existing pack's for a new revision, the local pack's otherwise.</param>
/// <param name="TargetRevision">The revision a new revision becomes (the existing one + 1); <c>null</c> for a new pack.</param>
public sealed record PublishRun(string Key, PublishStep? Step, string TargetTitle, int? TargetRevision);

/// <summary>A local pack's publish state for the strip, Packs → Mine and Activity.</summary>
/// <param name="Run">The publish running for this pack (<see cref="PublishPhase.Sending"/>).</param>
/// <param name="Failure">The last try's failure (<see cref="PublishPhase.Failed"/>).</param>
/// <param name="Publication">The PR the pack was sent as (<see cref="PublishPhase.Checking"/>, <see cref="PublishPhase.Published"/>).</param>
/// <param name="Live">The registry's pack once the index lists the sent revision.</param>
public sealed record PublishStatus(
    PublishPhase Phase,
    PublishRun? Run = null,
    PublishResult.Failed? Failure = null,
    LocalPackPublication? Publication = null,
    RegistryPack? Live = null)
{
    public static PublishStatus NotPublished { get; } = new(PublishPhase.NotPublished);
}

/// <summary>The strip state of a local pack (pure; design §7).</summary>
public static class PublishStatuses
{
    /// <summary>Players (not the author) who confirm "works" before a pack becomes <c>verified</c> (design §7; the registry's N, task 64).</summary>
    public const int VerifiedConfirmations = 3;

    /// <summary>A local pack's key: game, language and local ID.</summary>
    public static string KeyOf(LocalPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return $"{pack.Manifest.Game}/{pack.Manifest.Language}/{pack.Manifest.Id}";
    }

    /// <summary>
    /// The publish running for the pack → <see cref="PublishPhase.Sending"/>; else the last try's failure (kept until the
    /// next try) → <see cref="PublishPhase.Failed"/>; else a PR in the pack's report → <see cref="PublishPhase.Published"/>
    /// when the index lists its pack at the sent revision or later, otherwise <see cref="PublishPhase.Checking"/> (CI
    /// running; a rejected PR is task 62's); else <see cref="PublishPhase.NotPublished"/>.
    /// </summary>
    public static PublishStatus Of(LocalPack pack, RegistryIndex? index, PublishRun? running, PublishResult.Failed? failure)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (running is not null && running.Key == KeyOf(pack))
        {
            return new PublishStatus(PublishPhase.Sending, Run: running);
        }

        if (failure is not null)
        {
            return new PublishStatus(PublishPhase.Failed, Failure: failure, Publication: pack.Report.Published);
        }

        if (pack.Report.Published is not { } publication)
        {
            return PublishStatus.NotPublished;
        }

        var live = index?.PacksFor(pack.Manifest.Game, pack.Manifest.Language)
            .FirstOrDefault(p => p.Id == publication.PackId && p.Revision >= publication.Revision);
        return live is null
            ? new PublishStatus(PublishPhase.Checking, Publication: publication)
            : new PublishStatus(PublishPhase.Published, Publication: publication, Live: live);
    }

    /// <summary><c>Publish</c> is offered: tried in game (design §7) and not sent yet, or the last try failed.</summary>
    public static bool CanPublish(LocalPack pack, PublishStatus status)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(status);
        return pack.Report.IsTried && status.Phase is PublishPhase.NotPublished or PublishPhase.Failed;
    }
}
