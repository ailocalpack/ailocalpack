using AiLocalPack.Core.Fonts;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Patches;
using AiLocalPack.Core.Registry;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.ViewModels.GamePage;

namespace AiLocalPack.Desktop.Install;

/// <summary>The install flow's words (design §4a): steps, the plain reason of a failure, the confirmation's risk lines.</summary>
public static class InstallTexts
{
    /// <summary>"Installing «Фонарщики» · step 2 of 6", "Uninstalling «Фонарщики»".</summary>
    public static string Title(InstallOperation operation, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(localizer);
        return operation.Kind switch
        {
            InstallOperationKind.Uninstall => localizer.Format(nameof(Strings.Install_Progress_Uninstalling), operation.Title),
            InstallOperationKind.Reapply => localizer.Format(nameof(Strings.Install_Progress_Reapplying), operation.Title, operation.StepNumber, operation.Steps.Count),
            _ => localizer.Format(nameof(Strings.Install_Progress_Installing), operation.Title, operation.StepNumber, operation.Steps.Count),
        };
    }

    /// <summary>"Downloading the pack", "Backing up the original files"…</summary>
    public static string Step(InstallStep step, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[step switch
        {
            InstallStep.Download => nameof(Strings.Install_Step_Download),
            InstallStep.Dependency => nameof(Strings.Install_Step_Dependency),
            InstallStep.RemovePrevious => nameof(Strings.Install_Step_RemovePrevious),
            InstallStep.Check => nameof(Strings.Install_Step_Check),
            InstallStep.Rebuild => nameof(Strings.Install_Step_Rebuild),
            InstallStep.Patch => nameof(Strings.Install_Step_Patch),
            InstallStep.BackUp => nameof(Strings.Install_Step_BackUp),
            InstallStep.Write => nameof(Strings.Install_Step_Write),
            InstallStep.Verify => nameof(Strings.Install_Step_Verify),
            _ => nameof(Strings.Install_Step_Restore),
        }];
    }

    /// <summary>
    /// The reason in plain words, e.g. "data/Map001.json is in use; the game seems to be running. Close it and retry."
    /// </summary>
    public static string Reason(InstallError error, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(localizer);
        var file = error.File ?? string.Empty;
        return error.Kind switch
        {
            InstallErrorKind.Busy => localizer[nameof(Strings.Install_Why_Busy)],
            InstallErrorKind.Blocked => localizer[nameof(Strings.Install_Why_Blocked)],
            InstallErrorKind.NotInRegistry => localizer[nameof(Strings.Install_Why_NotInRegistry)],
            InstallErrorKind.Fetch => localizer[error.Fetch switch
            {
                PackFetchProblem.Offline => nameof(Strings.Install_Why_Offline),
                PackFetchProblem.Timeout => nameof(Strings.Install_Why_Timeout),
                PackFetchProblem.HttpError => nameof(Strings.Install_Why_HttpError),
                PackFetchProblem.FileMissing => nameof(Strings.Install_Why_FileMissing),
                PackFetchProblem.NewerFormat => nameof(Strings.Install_Why_NewerFormat),
                PackFetchProblem.HashMismatch => nameof(Strings.Install_Why_HashMismatch),
                PackFetchProblem.DiskError => nameof(Strings.Install_Why_CacheError),
                _ => nameof(Strings.Install_Why_Damaged),
            }],
            InstallErrorKind.Incompatible when error.Compatibility is { } compatibility =>
                localizer.Format(nameof(Strings.Install_Why_Incompatible), PackCompatibilityTexts.Explain(compatibility, localizer)),
            InstallErrorKind.Incompatible => localizer.Format(nameof(Strings.Install_Why_Incompatible), string.Empty).TrimEnd(),
            InstallErrorKind.Patch => error.Patch switch
            {
                PatchProblem.ArchiveInvalid => localizer[nameof(Strings.Install_Why_PatchArchive)],
                PatchProblem.SourceUnreadable => localizer.Format(nameof(Strings.Install_Why_PatchUnreadable), file),
                PatchProblem.DiskError => localizer[nameof(Strings.Install_Why_StagingError)],
                _ => localizer.Format(nameof(Strings.Install_Why_Patch), file),
            },
            InstallErrorKind.DependencyUnknown => localizer.Format(
                nameof(Strings.Install_Why_DependencyUnknown),
                string.Join(", ", (error.Dependencies ?? []).Select(d => $"{d.Id} {d.Version}"))),
            InstallErrorKind.DependencyPlatform when error.Platform is null => localizer.Format(nameof(Strings.Install_Why_DependencyPlatformUnknown), Dependency(error)),
            InstallErrorKind.DependencyPlatform => localizer.Format(nameof(Strings.Install_Why_DependencyPlatform), Dependency(error), error.Platform),
            InstallErrorKind.DependencyFetch => localizer.Format(
                error.Fetch switch
                {
                    PackFetchProblem.Offline or PackFetchProblem.Timeout or PackFetchProblem.HttpError => nameof(Strings.Install_Why_DependencyOffline),
                    PackFetchProblem.FileMissing => nameof(Strings.Install_Why_DependencyMissing),
                    PackFetchProblem.HashMismatch => nameof(Strings.Install_Why_DependencyHash),
                    _ => nameof(Strings.Install_Why_CacheError),
                },
                Dependency(error)),
            InstallErrorKind.DependencyArchive => localizer.Format(nameof(Strings.Install_Why_DependencyArchive), Dependency(error)),
            InstallErrorKind.InvalidStrings => localizer[nameof(Strings.Install_Why_InvalidStrings)],
            InstallErrorKind.Recipe => localizer.Format(nameof(Strings.Install_Why_Recipe), file),
            InstallErrorKind.Font => localizer.Format(
                error.Font is FontFetchProblem.Offline or FontFetchProblem.Timeout or FontFetchProblem.HttpError
                    ? nameof(Strings.Install_Why_FontOffline)
                    : nameof(Strings.Install_Why_Font),
                error.FontName ?? string.Empty),
            InstallErrorKind.Transaction when error.Transaction is { } failure => Transaction(failure, localizer),
            _ => localizer[nameof(Strings.Install_Why_Unexpected)],
        };
    }

    /// <summary>"BepInEx 5.4.23.2": the whitelist's name, else the ID.</summary>
    private static string Dependency(InstallError error) =>
        $"{error.DependencyName ?? error.Dependencies?.FirstOrDefault()?.Id} {error.Dependencies?.FirstOrDefault()?.Version}".Trim();

    /// <summary>A transaction's problem in plain words, naming the file when there is one.</summary>
    public static string Transaction(InstallFailure failure, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(localizer);
        var file = failure.File ?? string.Empty;
        return failure.Problem switch
        {
            InstallProblem.GameRunning => localizer[nameof(Strings.Install_Why_GameRunning)],
            InstallProblem.FileInUse when file.Length > 0 => localizer.Format(nameof(Strings.Install_Why_FileInUse), file),
            InstallProblem.FileInUse => localizer[nameof(Strings.Install_Why_GameRunning)],
            InstallProblem.DiskFull => localizer[nameof(Strings.Install_Why_DiskFull)],
            InstallProblem.PermissionDenied => localizer.Format(nameof(Strings.Install_Why_PermissionDenied), file),
            InstallProblem.ReadOnly => localizer.Format(nameof(Strings.Install_Why_ReadOnly), file),
            InstallProblem.GameUnavailable => localizer[nameof(Strings.Install_Why_GameUnavailable)],
            InstallProblem.AlreadyInstalled => localizer[nameof(Strings.Install_Why_AlreadyInstalled)],
            InstallProblem.NotInstalled => localizer[nameof(Strings.Install_Why_NotInstalled)],
            InstallProblem.Busy => localizer[nameof(Strings.Install_Why_Busy)],
            InstallProblem.InterruptedInstall => localizer[nameof(Strings.Install_Why_Interrupted)],
            InstallProblem.VerifyFailed => localizer.Format(nameof(Strings.Install_Why_VerifyFailed), file),
            InstallProblem.BackupDamaged => localizer.Format(nameof(Strings.Install_Why_BackupDamaged), file),
            InstallProblem.DependencyConflict => localizer[nameof(Strings.Install_Why_DependencyConflict)],
            _ => localizer.Format(nameof(Strings.Install_Why_IoError), file),
        };
    }

    /// <summary>A confirmation line: "Alpha: not yet confirmed by other players.", "Replaces the installed «…» rev 3."…</summary>
    public static string Risk(InstallRisk risk, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(risk);
        ArgumentNullException.ThrowIfNull(localizer);
        return risk.Kind switch
        {
            InstallRiskKind.Alpha => localizer[nameof(Strings.Install_Risk_Alpha)],
            InstallRiskKind.ProbablyCompatible => localizer.Format(
                nameof(Strings.Install_Risk_Probably),
                risk.Compatibility is { } compatibility ? PackCompatibilityTexts.Explain(compatibility, localizer) : string.Empty).TrimEnd(),
            InstallRiskKind.Dependency => localizer.Format(
                nameof(Strings.Install_Risk_Dependency), risk.DependencyName ?? risk.Dependency?.Id ?? string.Empty, risk.Dependency?.Version ?? string.Empty),
            _ => localizer.Format(nameof(Strings.Install_Risk_Replaces), risk.ReplacedTitle ?? string.Empty, risk.Replaced?.Revision ?? 0),
        };
    }

    /// <summary>Resource key of a risk line's icon (⚠, ⬇, ↺).</summary>
    public static string RiskIcon(InstallRiskKind kind) => kind switch
    {
        InstallRiskKind.Dependency => "IconDownload",
        InstallRiskKind.Replaces => "IconUndo",
        _ => "IconAlert",
    };

    /// <summary>The done / failed toast of an operation.</summary>
    public static string Toast(InstallOutcome outcome, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(localizer);
        if (outcome.Succeeded)
        {
            return localizer.Format(
                outcome.Kind switch
                {
                    InstallOperationKind.Uninstall => nameof(Strings.Install_Toast_Uninstalled),
                    InstallOperationKind.Reapply => nameof(Strings.Install_Toast_Reapplied),
                    _ => nameof(Strings.Install_Toast_Installed),
                },
                outcome.Title);
        }

        var key = (outcome.Kind, outcome.Error!.FilesRestored) switch
        {
            (InstallOperationKind.Uninstall, _) => nameof(Strings.Install_Toast_UninstallFailed),
            (_, false) => nameof(Strings.Install_Toast_FailedNotRestored),
            (InstallOperationKind.Reapply, _) => nameof(Strings.Install_Toast_ReapplyFailed),
            _ => nameof(Strings.Install_Toast_Failed),
        };
        return localizer[key];
    }
}
