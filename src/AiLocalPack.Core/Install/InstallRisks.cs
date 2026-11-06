using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Core.Install;

/// <summary>A reason to ask before installing a pack (design §4a): the confirmation dialog lists only the ones that apply.</summary>
public enum InstallRiskKind
{
    /// <summary>The pack is <c>alpha</c>: not yet confirmed by other players.</summary>
    Alpha,

    /// <summary>The pack wasn't made for the player's files; the recipe rebuilds them, new strings stay untranslated.</summary>
    ProbablyCompatible,

    /// <summary>The pack needs a whitelisted executable dependency, downloaded from its official release (task 32).</summary>
    Dependency,

    /// <summary>Another pack is installed for the game: it is uninstalled first.</summary>
    Replaces,
}

/// <summary>One line of the install confirmation.</summary>
/// <param name="Compatibility">The check result (<see cref="InstallRiskKind.ProbablyCompatible"/>).</param>
/// <param name="Dependency">The dependency (<see cref="InstallRiskKind.Dependency"/>).</param>
/// <param name="Replaced">The installed pack (<see cref="InstallRiskKind.Replaces"/>).</param>
/// <param name="ReplacedTitle">Its title from the registry, or its ID when the registry no longer lists it.</param>
public sealed record InstallRisk(
    InstallRiskKind Kind,
    PackCompatibility? Compatibility = null,
    PackDependency? Dependency = null,
    InstalledPack? Replaced = null,
    string? ReplacedTitle = null);

/// <summary>Builds the install confirmation's list (design §4a). Pure.</summary>
public static class InstallRisks
{
    /// <summary>
    /// In this order: alpha, probably compatible, each dependency, replaces the installed pack. Empty (no dialog, the
    /// install starts at once) for a <c>verified</c>, compatible pack without dependencies when nothing is installed.
    /// </summary>
    /// <param name="manifest">The pack's manifest (its dependencies); <c>null</c> = not read, no dependency listed.</param>
    /// <param name="installed">The game's installed pack, whatever its language; the pack itself (same ID and language) doesn't count.</param>
    public static IReadOnlyList<InstallRisk> For(
        RegistryPack pack,
        PackManifest? manifest,
        PackCompatibility compatibility,
        InstalledPack? installed,
        string? installedTitle = null)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(compatibility);
        var risks = new List<InstallRisk>();
        if (pack.Status != PackStatus.Verified)
        {
            risks.Add(new InstallRisk(InstallRiskKind.Alpha));
        }

        if (compatibility.Verdict == PackCompatibilityVerdict.ProbablyCompatible)
        {
            risks.Add(new InstallRisk(InstallRiskKind.ProbablyCompatible, Compatibility: compatibility));
        }

        foreach (var dependency in manifest?.Dependencies ?? [])
        {
            risks.Add(new InstallRisk(InstallRiskKind.Dependency, Dependency: dependency));
        }

        if (installed is not null && (installed.PackId != pack.Id || installed.Language != pack.Language))
        {
            risks.Add(new InstallRisk(InstallRiskKind.Replaces, Replaced: installed, ReplacedTitle: installedTitle ?? installed.PackId));
        }

        return risks;
    }
}
