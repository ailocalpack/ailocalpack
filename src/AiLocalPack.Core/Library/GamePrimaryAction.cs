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

    /// <summary>A generation job exists (running or paused): opens the Generation tab.</summary>
    ViewProgress,
}

/// <summary>The header's primary action with what its caption names.</summary>
/// <param name="Pack">The pack it installs, plays with or updates to (from the registry); <c>null</c> when none or unknown.</param>
/// <param name="Installed">The installed pack (<see cref="GamePrimaryActionKind.Play"/>, update kinds).</param>
/// <param name="Job">The generation job (<see cref="GamePrimaryActionKind.ViewProgress"/>).</param>
public sealed record GamePrimaryAction(
    GamePrimaryActionKind Kind,
    RegistryPack? Pack = null,
    InstalledPackState? Installed = null,
    GenerationJobState? Job = null)
{
    public static GamePrimaryAction None { get; } = new(GamePrimaryActionKind.None);
}

/// <summary>Picks the game page's primary action and default tab from a <see cref="LocalizationStatus"/>. Pure.</summary>
public static class GamePrimaryActions
{
    /// <summary>
    /// Blocked or unknown → none; a job → view progress; a pack broken by an update → update to the registry's later
    /// revision of the same pack, else translate the new strings; installed → play; packs → install the best; else
    /// generate.
    /// </summary>
    public static GamePrimaryAction Resolve(LocalizationStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var installedPack = status.Installed is { } installed
            ? status.Packs.FirstOrDefault(p => p.Id == installed.PackId)
            : null;
        return status.Kind switch
        {
            LocalizationStatusKind.Blocked or LocalizationStatusKind.Unknown => GamePrimaryAction.None,
            LocalizationStatusKind.Generating or LocalizationStatusKind.Paused =>
                new(GamePrimaryActionKind.ViewProgress, Installed: status.Installed, Job: status.Job),
            LocalizationStatusKind.NeedsUpdate when installedPack is not null && installedPack.Revision > status.Installed!.Revision =>
                new(GamePrimaryActionKind.UpdatePack, installedPack, status.Installed),
            LocalizationStatusKind.NeedsUpdate => new(GamePrimaryActionKind.TranslateNewStrings, installedPack, status.Installed),
            LocalizationStatusKind.Installed => new(GamePrimaryActionKind.Play, installedPack, status.Installed),
            LocalizationStatusKind.PackAvailable => new(GamePrimaryActionKind.InstallBestPack, status.BestPack),
            _ => new(GamePrimaryActionKind.Generate),
        };
    }

    /// <summary>The tab the page opens on: Generation while a job exists, otherwise Packs.</summary>
    public static bool OpensOnGeneration(LocalizationStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Job is not null && !status.IsBlocked;
    }
}
