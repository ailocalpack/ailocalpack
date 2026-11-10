using AiLocalPack.Core.Registry;

namespace AiLocalPack.Core.Library;

/// <summary>The one action the game page's header offers (design §3), by the game's status for the chosen language.</summary>
public enum GamePrimaryActionKind
{
    /// <summary>Blocked, or the pack list isn't known yet.</summary>
    None,

    /// <summary>No pack installed and the registry has one: installs the best one.</summary>
    InstallBestPack,

    /// <summary>Nothing in the registry for the language: opens the Generation tab.</summary>
    Generate,

    /// <summary>A pack is installed and fits the game.</summary>
    Play,

    /// <summary>An update broke the installed pack and the registry has a later revision of it.</summary>
    UpdatePack,

    /// <summary>An update broke the installed pack and no later revision exists: generation in update mode.</summary>
    TranslateNewStrings,

    /// <summary>
    /// An update put back the files of the installed pack, which still fits, and it wasn't re-applied (automatic re-apply
    /// off or failed) and no later revision exists: writes it again.
    /// </summary>
    Reapply,

    /// <summary>A generation job runs: opens the Generation tab.</summary>
    ViewProgress,

    /// <summary>The job is paused (limit or player) or was interrupted: resumes it from its stage and chunk (task 53).</summary>
    ResumeGeneration,

    /// <summary>A stage of the job failed: runs that stage again.</summary>
    RetryStage,

    /// <summary>The glossary waits for the player's review: opens the Generation tab.</summary>
    ReviewGlossary,

    /// <summary>
    /// The last generation's local pack (task 55) waits, nothing is installed and no job runs: installs it and launches
    /// the game (<c>Install &amp; play</c>).
    /// </summary>
    InstallLocalPack,
}

/// <summary>The header's primary action with what its caption names.</summary>
/// <param name="Pack">The pack it installs, plays with or updates to (from the registry); <c>null</c> when none or unknown.</param>
/// <param name="Installed">The installed pack (<see cref="GamePrimaryActionKind.Play"/>, update kinds).</param>
/// <param name="Job">The generation job (<see cref="GamePrimaryActionKind.ViewProgress"/> and the other job kinds).</param>
/// <param name="Result">The local pack <see cref="GamePrimaryActionKind.InstallLocalPack"/> installs.</param>
public sealed record GamePrimaryAction(
    GamePrimaryActionKind Kind,
    RegistryPack? Pack = null,
    InstalledPackState? Installed = null,
    GenerationJobState? Job = null,
    GenerationResultState? Result = null)
{
    public static GamePrimaryAction None { get; } = new(GamePrimaryActionKind.None);
}

/// <summary>Picks the game page's primary action and default tab from a <see cref="LocalizationStatus"/>. Pure.</summary>
public static class GamePrimaryActions
{
    /// <summary>
    /// Blocked or unknown → none (a waiting local pack with nothing installed and no job → install it and play, task 55); a job → by its phase (<see cref="JobKind"/>); a pack broken by an update → update to the registry's later
    /// revision of the same pack, else re-apply a pack that still fits, else translate the new strings; installed → play; packs → install the best; else
    /// generate.
    /// </summary>
    public static GamePrimaryAction Resolve(LocalizationStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var installedPack = status.Installed is { Local: false } installed
            ? status.Packs.FirstOrDefault(p => p.Id == installed.PackId)
            : null;
        if (status.Result is { } result && status.Installed is null && status.Job is null && !status.IsBlocked)
        {
            // Installed in another language than the page's target: the status doesn't count it, the result does.
            return new(result.Installed ? GamePrimaryActionKind.Play : GamePrimaryActionKind.InstallLocalPack, Result: result);
        }

        if (status is { Kind: LocalizationStatusKind.NeedsUpdate or LocalizationStatusKind.Installed, Job: null, Result: { Installed: false } update, Installed: { NeedsReapply: false } outdated }
            && update.PackId == outdated.PackId && (outdated.BrokenByUpdate || outdated.ForOtherVersion || outdated.Local))
        {
            // An update job's new revision (task 56) waits for the pack the game update broke or left partly untranslated
            // (a local pack's waiting result of the same ID is always its next revision: it replaced the installed one).
            return new(GamePrimaryActionKind.InstallLocalPack, Installed: outdated, Result: update);
        }

        return status.Kind switch
        {
            LocalizationStatusKind.Blocked or LocalizationStatusKind.Unknown => GamePrimaryAction.None,
            LocalizationStatusKind.Generating or LocalizationStatusKind.Paused =>
                new(JobKind(status.Job), Installed: status.Installed, Job: status.Job),
            LocalizationStatusKind.NeedsUpdate when installedPack is not null && installedPack.Revision > status.Installed!.Revision =>
                new(GamePrimaryActionKind.UpdatePack, installedPack, status.Installed),
            LocalizationStatusKind.NeedsUpdate when status.Installed is { NeedsReapply: true } => new(GamePrimaryActionKind.Reapply, installedPack, status.Installed),
            LocalizationStatusKind.NeedsUpdate => new(GamePrimaryActionKind.TranslateNewStrings, installedPack, status.Installed),
            LocalizationStatusKind.Installed => new(GamePrimaryActionKind.Play, installedPack, status.Installed),
            LocalizationStatusKind.PackAvailable => new(GamePrimaryActionKind.InstallBestPack, status.BestPack),
            _ => new(GamePrimaryActionKind.Generate),
        };
    }

    /// <summary>Running → view progress; paused or interrupted → resume; failed → retry the stage; waiting → review the glossary.</summary>
    public static GamePrimaryActionKind JobKind(GenerationJobState? job) => job?.Phase switch
    {
        GenerationJobPhase.PausedByLimit or GenerationJobPhase.PausedByPlayer or GenerationJobPhase.Interrupted => GamePrimaryActionKind.ResumeGeneration,
        GenerationJobPhase.Failed => GamePrimaryActionKind.RetryStage,
        GenerationJobPhase.WaitingForReview => GamePrimaryActionKind.ReviewGlossary,
        _ => GamePrimaryActionKind.ViewProgress,
    };

    /// <summary>
    /// The tab the page opens on: Generation while a job exists or its result waits to be tried (task 55), otherwise Packs.
    /// </summary>
    public static bool OpensOnGeneration(LocalizationStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return !status.IsBlocked && (status.Job is not null || status.Result is { Tried: false });
    }
}
