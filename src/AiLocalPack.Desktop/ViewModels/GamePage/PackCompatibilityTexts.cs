using System.Globalization;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;

namespace AiLocalPack.Desktop.ViewModels.GamePage;

/// <summary>
/// The Packs tab's words for a <see cref="PackCompatibility"/> (design §4): the row's short status, the details box
/// title, and the explanation from the reason and its data ("Made for build 1 234 500, you have 1 234 567. Changed
/// files: 2 of 41; …").
/// </summary>
public static class PackCompatibilityTexts
{
    /// <summary>"compatible", "probably compatible", "incompatible".</summary>
    public static string Short(PackCompatibilityVerdict verdict, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[verdict switch
        {
            PackCompatibilityVerdict.Compatible => nameof(Strings.Packs_Compat_Compatible),
            PackCompatibilityVerdict.ProbablyCompatible => nameof(Strings.Packs_Compat_Probably),
            _ => nameof(Strings.Packs_Compat_Incompatible),
        }];
    }

    /// <summary>"Compatible with your version", "Probably compatible", "Incompatible with your version".</summary>
    public static string Title(PackCompatibilityVerdict verdict, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[verdict switch
        {
            PackCompatibilityVerdict.Compatible => nameof(Strings.Packs_Box_Compatible),
            PackCompatibilityVerdict.ProbablyCompatible => nameof(Strings.Packs_Box_Probably),
            _ => nameof(Strings.Packs_Box_Incompatible),
        }];
    }

    /// <summary>Which builds or files match, how many touched files changed, or why the pack can't be installed.</summary>
    public static string Explain(PackCompatibility compatibility, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(compatibility);
        ArgumentNullException.ThrowIfNull(localizer);
        var culture = localizer.Culture;
        var changed = compatibility.ChangedFiles.Count.ToString("N0", culture);
        var touched = compatibility.TouchedFileCount.ToString("N0", culture);
        return compatibility.Reason switch
        {
            PackCompatibilityReason.BuildListed => localizer.Format(nameof(Strings.Packs_Why_BuildListed), Build(compatibility.GameBuildId, culture)),
            PackCompatibilityReason.FilesMatch => localizer.Format(nameof(Strings.Packs_Why_FilesMatch), touched),
            PackCompatibilityReason.FilesChanged when compatibility is { ReferenceBuildId: { } reference, GameBuildId: { } game } =>
                localizer.Format(nameof(Strings.Packs_Why_FilesChanged), Build(reference, culture), Build(game, culture), changed, touched),
            PackCompatibilityReason.FilesChanged => localizer.Format(nameof(Strings.Packs_Why_FilesChangedNoBuild), changed, touched),
            PackCompatibilityReason.WrongGame => localizer[nameof(Strings.Packs_Why_WrongGame)],
            PackCompatibilityReason.GameNotAvailable => localizer[nameof(Strings.Packs_Why_GameNotAvailable)],
            PackCompatibilityReason.FilesMissing => localizer.Format(
                nameof(Strings.Packs_Why_FilesMissing),
                compatibility.MissingFiles.Count.ToString("N0", culture),
                compatibility.MissingFiles.FirstOrDefault() ?? string.Empty),
            PackCompatibilityReason.FileUnreadable => localizer.Format(nameof(Strings.Packs_Why_FileUnreadable), compatibility.UnreadableFile ?? string.Empty),
            PackCompatibilityReason.PatchFilesChanged => localizer.Format(nameof(Strings.Packs_Why_PatchFilesChanged), changed, touched),
            PackCompatibilityReason.RecipeToolUnknown => localizer.Format(nameof(Strings.Packs_Why_RecipeToolUnknown), compatibility.RecipeTool?.Tool ?? string.Empty),
            _ => localizer.Format(nameof(Strings.Packs_Why_RecipeToolTooNew), compatibility.RecipeTool?.Tool ?? string.Empty, compatibility.RecipeTool?.Version ?? string.Empty),
        };
    }

    /// <summary>Why the manifest couldn't be read, in the player's words.</summary>
    public static string Failure(PackManifestProblem problem, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[problem switch
        {
            PackManifestProblem.Offline or PackManifestProblem.Timeout or PackManifestProblem.HttpError => nameof(Strings.Packs_Failed_Offline),
            PackManifestProblem.FileMissing => nameof(Strings.Packs_Failed_Missing),
            PackManifestProblem.NewerFormat => nameof(Strings.Packs_Failed_NewerFormat),
            _ => nameof(Strings.Packs_Failed_Invalid),
        }];
    }

    /// <summary>A Steam build ID with grouped digits ("14 882 031"); other IDs as they are.</summary>
    public static string Build(string? buildId, CultureInfo culture) =>
        long.TryParse(buildId, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number.ToString("N0", culture) : buildId ?? string.Empty;
}
