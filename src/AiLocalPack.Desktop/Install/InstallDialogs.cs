using AiLocalPack.Core.Install;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.Install;

/// <summary>What the install confirmation asks about.</summary>
/// <param name="Title">The pack's title.</param>
/// <param name="Risks">The applicable risks (never empty: without risks the install starts at once).</param>
/// <param name="OnConfirm">Runs after the dialog closed, when the player pressed <c>Install</c>.</param>
public sealed record InstallConfirmRequest(string Title, IReadOnlyList<InstallRisk> Risks, Action OnConfirm);

/// <summary>A line of the confirmation: icon, tone and text.</summary>
public sealed record InstallRiskLine(InstallRiskKind Kind, string Icon, string Text)
{
    /// <summary>Alpha and probably compatible are warnings (amber); a download or a replacement is neutral.</summary>
    public bool IsWarn => Kind is InstallRiskKind.Alpha or InstallRiskKind.ProbablyCompatible;
}

/// <summary>
/// Install confirmation (design §4a): "Install «…»?", only the applicable risks (alpha, probably compatible, a
/// dependency download, replaces the installed pack), "Original files are backed up; you can uninstall.",
/// <c>Cancel</c> (focused) / <c>Install</c>.
/// </summary>
public sealed partial class InstallConfirmDialogViewModel(InstallConfirmRequest request, IDialogService dialogs, Localizer localizer) : DialogViewModel
{
    public InstallConfirmRequest Request { get; } = request ?? throw new ArgumentNullException(nameof(request));

    public string Title => localizer.Format(nameof(Strings.Install_Confirm_Title), Request.Title);

    public IReadOnlyList<InstallRiskLine> Lines =>
        [.. Request.Risks.Select(r => new InstallRiskLine(r.Kind, InstallTexts.RiskIcon(r.Kind), InstallTexts.Risk(r, localizer)))];

    [RelayCommand]
    private void Confirm()
    {
        dialogs.Close(this);
        Request.OnConfirm();
    }

    [RelayCommand]
    private void Cancel() => dialogs.Close(this);
}

/// <summary>A failed operation and how to run it again (<c>null</c> = no <c>Retry</c> button).</summary>
public sealed record InstallDetailsRequest(InstallOutcome Outcome, Action? Retry);

/// <summary>
/// A failed install, re-apply or uninstall (design §4a, the failure toast's <c>Details</c>): the reason in plain words,
/// whether the files were restored, the log, <c>Close</c> and <c>Retry</c> (when another try may help).
/// </summary>
public sealed partial class InstallDetailsDialogViewModel : DialogViewModel
{
    private readonly IDialogService _dialogs;
    private readonly Localizer _localizer;
    private readonly Action? _retry;

    public InstallDetailsDialogViewModel(InstallDetailsRequest request, IDialogService dialogs, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (outcome, retry) = request;
        if (outcome.Error is null)
        {
            throw new ArgumentException("Only a failed operation has details.", nameof(outcome));
        }

        Outcome = outcome;
        _retry = outcome.Error.CanRetry ? retry : null;
        _dialogs = dialogs;
        _localizer = localizer;
    }

    public InstallOutcome Outcome { get; }

    /// <summary>"Couldn't install «…»", "Couldn't uninstall «…»"…</summary>
    public string Title => _localizer.Format(
        Outcome.Kind switch
        {
            InstallOperationKind.Uninstall => nameof(Strings.Install_Details_TitleUninstall),
            InstallOperationKind.Reapply => nameof(Strings.Install_Details_TitleReapply),
            _ => nameof(Strings.Install_Details_TitleInstall),
        },
        Outcome.Title);

    public string Reason => InstallTexts.Reason(Outcome.Error!, _localizer);

    /// <summary>"The game files are as they were before." / "Restoring some files failed; the app retries on the next start."</summary>
    public string RestoredText => _localizer[Outcome.Error!.FilesRestored
        ? Outcome.Kind == InstallOperationKind.Uninstall ? nameof(Strings.Install_Details_UninstallStopped) : nameof(Strings.Install_Details_Restored)
        : nameof(Strings.Install_Details_NotRestored)];

    public bool IsRestored => Outcome.Error!.FilesRestored;

    public string LogText => string.Join(Environment.NewLine, Outcome.Log);

    public bool HasLog => Outcome.Log.Count > 0;

    public bool CanRetry => _retry is not null;

    [RelayCommand]
    private void Close() => _dialogs.Close(this);

    [RelayCommand]
    private void Retry()
    {
        _dialogs.Close(this);
        _retry?.Invoke();
    }
}
