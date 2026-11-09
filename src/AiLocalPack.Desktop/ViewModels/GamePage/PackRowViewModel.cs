using System.Globalization;
using AiLocalPack.Core.Fonts;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels.GamePage;

/// <summary>Where a pack's compatibility check stands.</summary>
public enum PackCheckState
{
    /// <summary>Not looked at yet (the manifest is fetched when the pack is selected, then for the rest of the list).</summary>
    NotChecked,

    /// <summary>Fetching the manifest or comparing files.</summary>
    Checking,

    /// <summary><see cref="PackRowViewModel.Compatibility"/> is known.</summary>
    Checked,

    /// <summary>The manifest couldn't be read (<see cref="PackRowViewModel.Failure"/>); <c>Retry</c> fetches again.</summary>
    Failed,
}

/// <summary>What the Packs tab tells each row about the page: the saved list is shown, which pack is installed.</summary>
/// <param name="IsStale">The pack list is the saved copy (registry unreachable): install is unavailable.</param>
/// <param name="Installed">The installed pack of this game and language, if any.</param>
/// <param name="InstalledTitle">Its title («…», or the pack ID when it is gone from the registry).</param>
/// <param name="IsBusy">An install, re-apply or uninstall runs (for any game): every <c>Install</c> is off.</param>
/// <param name="InstallingPackId">The pack of this language being installed into this game, if any.</param>
public sealed record PackRowContext(
    bool IsStale,
    Core.Library.InstalledPackState? Installed,
    string? InstalledTitle,
    bool IsBusy = false,
    string? InstallingPackId = null,
    Core.Dependencies.DependencyWhitelist? Dependencies = null)
{
    public static PackRowContext None { get; } = new(false, null, null);
}

/// <summary>
/// One registry pack in the Packs tab (design §4): its two-line row (title, Installed tag; author · status · 👍 ·
/// compatibility) and, when selected, the details panel: status, revision, likes, the tinted compatibility box with
/// its explanation, author / agent / coverage / reports / updated / fonts / dependencies, the revision history,
/// <c>Install</c> (disabled with the reason when it can't run) and <c>Reviews on GitHub ↗</c>. The index entry is
/// known at once; the manifest (agent, fonts, dependencies, revisions) and the compatibility come with the check.
/// </summary>
public sealed partial class PackRowViewModel : ObservableObject
{
    private readonly Localizer _localizer;
    private PackRowContext _context = PackRowContext.None;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RevisionsToggleIcon))]
    private bool _isRevisionsOpen;

    public PackRowViewModel(
        RegistryPack pack,
        Localizer localizer,
        Action<PackRowViewModel> select,
        Action<PackRowViewModel> retry,
        Action<PackRowViewModel> install,
        Action<PackRowViewModel> uninstall,
        Action translateMissing,
        Action<Uri> openUrl)
    {
        ArgumentNullException.ThrowIfNull(pack);
        Pack = pack;
        _localizer = localizer;
        SelectCommand = new RelayCommand(() => select(this));
        RetryCommand = new RelayCommand(() => retry(this));
        InstallCommand = new RelayCommand(() => install(this), () => CanInstall);
        UninstallCommand = new RelayCommand(() => uninstall(this), () => ShowUninstall && !_context.IsBusy);
        TranslateMissingCommand = new RelayCommand(translateMissing);
        OpenDiscussionCommand = new RelayCommand(() => openUrl(Pack.Discussion!), () => Pack.Discussion is not null);
        ToggleRevisionsCommand = new RelayCommand(() => IsRevisionsOpen = !IsRevisionsOpen);
    }

    /// <summary>The index entry (refreshed with each pack list while the revision stays the same).</summary>
    public RegistryPack Pack { get; private set; }

    public PackCheckState State { get; private set; }

    /// <summary>The manifest, once fetched.</summary>
    public PackManifest? Manifest { get; private set; }

    public PackCompatibility? Compatibility { get; private set; }

    /// <summary>Why the manifest couldn't be read (<see cref="PackCheckState.Failed"/>).</summary>
    public PackManifestFetch? Failure { get; private set; }

    public IRelayCommand SelectCommand { get; }

    public IRelayCommand RetryCommand { get; }

    /// <summary>Raises the tab's <c>InstallRequested</c> (the install flow).</summary>
    public IRelayCommand InstallCommand { get; }

    /// <summary><c>Uninstall (restore originals)</c> under the installed pack's <c>Installed ✓</c>.</summary>
    public IRelayCommand UninstallCommand { get; }

    public IRelayCommand TranslateMissingCommand { get; }

    public IRelayCommand OpenDiscussionCommand { get; }

    public IRelayCommand ToggleRevisionsCommand { get; }

    // ---------- Row ----------

    public string Title => Pack.Title;

    public string AuthorText => Pack.Author.DisplayName;

    public bool IsVerified => Pack.Status == PackStatus.Verified;

    /// <summary>"verified" / "alpha".</summary>
    public string StatusText => _localizer[IsVerified ? nameof(Strings.Library_Card_Verified) : nameof(Strings.Library_Card_Alpha)];

    public string LikesText => Pack.Rating.Likes.ToString("N0", _localizer.Culture);

    /// <summary>The compatibility part of the row: shown once checking started.</summary>
    public bool HasCompat => State != PackCheckState.NotChecked;

    /// <summary>"compatible", "probably compatible", "incompatible", "checking…", "not checked".</summary>
    public string CompatText => State switch
    {
        PackCheckState.Checked => PackCompatibilityTexts.Short(Compatibility!.Verdict, _localizer),
        PackCheckState.Failed => _localizer[nameof(Strings.Packs_Compat_Failed)],
        _ => _localizer[nameof(Strings.Packs_Compat_Checking)],
    };

    /// <summary>Tone class of the compatibility (row and box): <c>ok</c>, <c>warn</c>, <c>bad</c>, or <c>muted</c> while unknown.</summary>
    public string CompatTone => (State, Compatibility?.Verdict) switch
    {
        (PackCheckState.Checked, PackCompatibilityVerdict.Compatible) => "ok",
        (PackCheckState.Checked, PackCompatibilityVerdict.ProbablyCompatible) => "warn",
        (PackCheckState.Checked, _) => "bad",
        (PackCheckState.Failed, _) => "bad",
        _ => "muted",
    };

    public bool IsCompatOk => CompatTone == "ok";

    public bool IsCompatWarn => CompatTone == "warn";

    public bool IsCompatBad => CompatTone == "bad";

    /// <summary>Resource key of the compatibility icon.</summary>
    public string CompatIcon => CompatTone switch
    {
        "ok" => "IconCheck",
        "warn" => "IconCircleDashed",
        "bad" => "IconXCircle",
        _ => "IconLoader",
    };

    /// <summary>An incompatible pack's row is dimmed.</summary>
    public bool IsDimmed => Compatibility is { CanInstall: false };

    public bool IsInstalled => _context.Installed is { Local: false } installed && installed.PackId == Pack.Id;

    /// <summary>A game update broke the installed pack: the tag is amber.</summary>
    public bool IsInstalledBroken => IsInstalled && _context.Installed!.BrokenByUpdate;

    /// <summary>This pack is being installed: the row's <c>installing</c> tag, the button's <c>Installing…</c>.</summary>
    public bool IsInstalling => _context.InstallingPackId == Pack.Id;

    /// <summary>"installing".</summary>
    public string InstallingTag => _localizer[nameof(Strings.Packs_Tag_Installing)];

    /// <summary>"Installed", or "rev 3 · rev 4 available" when an update broke it.</summary>
    public string InstalledTag => IsInstalledBroken && Pack.Revision > _context.Installed!.Revision
        ? _localizer.Format(nameof(Strings.Packs_Tag_Broken), _context.Installed.Revision, Pack.Revision)
        : _localizer[nameof(Strings.Packs_Tag_Installed)];

    // ---------- Details: header and compatibility box ----------

    public string RevisionText => _localizer.Format(nameof(Strings.Packs_Revision), Pack.Revision);

    public bool IsChecking => State is PackCheckState.NotChecked or PackCheckState.Checking;

    public bool IsChecked => State == PackCheckState.Checked;

    public bool IsFailed => State == PackCheckState.Failed;

    public string CompatTitle => IsChecked ? PackCompatibilityTexts.Title(Compatibility!.Verdict, _localizer) : _localizer[nameof(Strings.Packs_Box_Checking)];

    public string CompatExplanation => IsChecked
        ? PackCompatibilityTexts.Explain(Compatibility!, _localizer)
        : _localizer[nameof(Strings.Packs_Box_CheckingText)];

    public string FailureText => Failure is { } failure ? PackCompatibilityTexts.Failure(failure.Problem, _localizer) : string.Empty;

    /// <summary><c>Retry</c> on the failure box: only when another try may help (not for a damaged or newer manifest).</summary>
    public bool CanRetry => Failure is { MayPass: true } or { Problem: PackManifestProblem.FileMissing };

    // ---------- Details: key–value list ----------

    /// <summary>Made with: "Claude Code", "Codex (gpt-5)", "By hand"; empty until the manifest is read.</summary>
    public string AgentText => Manifest switch
    {
        null => string.Empty,
        { Agent: null } => _localizer[nameof(Strings.Packs_Agent_ByHand)],
        { Agent: { } agent } => agent.Model is { } model ? $"{AgentName(agent.Id)} ({model})" : AgentName(agent.Id),
    };

    public bool HasAgent => Manifest is not null;

    /// <summary>0…100 for the bar.</summary>
    public double CoveragePercent => Pack.Coverage.Ratio * 100;

    /// <summary>"98% of strings" (rounded down, so only a full pack says 100%).</summary>
    public string CoverageText => _localizer.Format(
        nameof(Strings.Packs_Coverage),
        Math.Floor(CoveragePercent).ToString("0", _localizer.Culture));

    /// <summary>"works for 18 · doesn't work for 1".</summary>
    public string ReportsText => _localizer.Format(
        nameof(Strings.Packs_Reports),
        Pack.Rating.Works.ToString("N0", _localizer.Culture),
        Pack.Rating.DoesNotWork.ToString("N0", _localizer.Culture));

    public string UpdatedText => Date(Pack.Updated);

    /// <summary>
    /// The whitelisted fonts the pack swaps in when the game's font lacks its letters: "Noto Sans (bundled)",
    /// "Noto Sans JP (downloaded when installing)"; an ID this app's whitelist lacks as is.
    /// </summary>
    public string FontsText => Manifest is { Fonts.Count: > 0 } manifest
        ? string.Join(", ", manifest.Fonts.Select(id => FontWhitelist.Bundled.Find(id) is { } font
            ? _localizer.Format(font.IsBundled ? nameof(Strings.Packs_Font_Bundled) : nameof(Strings.Packs_Font_Download), font.Name)
            : id))
        : string.Empty;

    public bool HasFonts => FontsText.Length > 0;

    /// <summary>
    /// "BepInEx 5.4.23.2 (downloaded from the official release)", by the whitelist's name; one not on the whitelist
    /// "x 1.0 (not on the registry's list of allowed components)".
    /// </summary>
    public string DependenciesText => Manifest is { Dependencies.Count: > 0 } manifest
        ? string.Join(", ", manifest.Dependencies.Select(d => _context.Dependencies?.Find(d.Id) is { } known && known.Find(d.Version) is not null
            ? _localizer.Format(nameof(Strings.Packs_Requires), $"{known.Name} {d.Version}")
            : _localizer.Format(nameof(Strings.Packs_RequiresUnknown), $"{d.Id} {d.Version}")))
        : string.Empty;

    public bool HasDependencies => DependenciesText.Length > 0;

    public bool HasRevisions => Manifest is not null;

    public string RevisionCount => (Manifest?.Revisions.Count ?? 0).ToString("N0", _localizer.Culture);

    public string RevisionsToggleIcon => IsRevisionsOpen ? "IconChevronDown" : "IconChevronRight";

    /// <summary>"rev 4 · 12.10.2026 · fixes from reviews", newest first.</summary>
    public IReadOnlyList<string> RevisionLines => Manifest is { } manifest
        ? [.. manifest.Revisions.Reverse().Select(r => r.Notes is { Length: > 0 } notes
            ? _localizer.Format(nameof(Strings.Packs_RevisionLine), r.Number, Date(r.Date), notes)
            : _localizer.Format(nameof(Strings.Packs_RevisionLine_NoNotes), r.Number, Date(r.Date)))]
        : [];

    public bool HasDiscussion => Pack.Discussion is not null;

    // ---------- Details: actions ----------

    /// <summary>
    /// <c>Install</c> runs for a checked, installable pack while the list is current and the pack isn't the installed one;
    /// otherwise it is disabled with <see cref="InstallHint"/>.
    /// </summary>
    public bool CanInstall => State == PackCheckState.Checked && Compatibility!.CanInstall && !_context.IsStale && !IsInstalled && !_context.IsBusy;

    /// <summary>"Install", "Installing…", or "Installed" for the installed pack.</summary>
    public string InstallText => _localizer[IsInstalling ? nameof(Strings.Packs_Installing) : IsInstalled ? nameof(Strings.Packs_Installed) : nameof(Strings.Packs_Install)];

    public string InstallIcon => IsInstalling ? "IconLoader" : IsInstalled ? "IconCheck" : "IconDownload";

    /// <summary>The installed pack offers <c>Uninstall (restore originals)</c>.</summary>
    public bool ShowUninstall => IsInstalled && !IsInstalling;

    /// <summary>Why Install is off, or what it will do ("Replaces the installed «…»").</summary>
    public string InstallHint => this switch
    {
        { IsInstalled: true } or { IsInstalling: true } => string.Empty,
        { Compatibility.CanInstall: false } => _localizer[nameof(Strings.Packs_Hint_Incompatible)],
        _ when _context.IsStale => _localizer[nameof(Strings.Packs_Hint_Offline)],
        _ when _context.Installed is not null => _localizer.Format(nameof(Strings.Packs_Hint_Replaces), _context.InstalledTitle ?? _context.Installed.PackId),
        _ => string.Empty,
    };

    public bool HasInstallHint => InstallHint.Length > 0;

    /// <summary>An incompatible pack points to translating the missing strings with AI.</summary>
    public bool ShowTranslateMissing => Compatibility is { CanInstall: false } && !IsInstalled;

    // ---------- Updates from the tab ----------

    internal void SetPack(RegistryPack pack)
    {
        Pack = pack;
        RefreshTexts();
    }

    internal void SetContext(PackRowContext context)
    {
        _context = context;
        RefreshTexts();
    }

    internal void BeginCheck() => SetState(PackCheckState.Checking, Manifest, null, null);

    internal void SetManifest(PackManifest manifest) => SetState(PackCheckState.Checking, manifest, null, null);

    internal void SetChecked(PackCompatibility compatibility) => SetState(PackCheckState.Checked, Manifest, compatibility, null);

    internal void SetFailed(PackManifestFetch failure) => SetState(PackCheckState.Failed, null, null, failure);

    /// <summary>The game changed (update, drive unplugged): compare again; the manifest is kept.</summary>
    internal void ResetCheck() => SetState(PackCheckState.NotChecked, Manifest, null, null);

    /// <summary>Re-raises every text (UI language switch).</summary>
    internal void RefreshTexts()
    {
        OnPropertyChanged(string.Empty);
        InstallCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        OpenDiscussionCommand.NotifyCanExecuteChanged();
    }

    private void SetState(PackCheckState state, PackManifest? manifest, PackCompatibility? compatibility, PackManifestFetch? failure)
    {
        State = state;
        Manifest = manifest;
        Compatibility = compatibility;
        Failure = failure;
        RefreshTexts();
    }

    private string Date(DateTimeOffset date) => date.ToLocalTime().ToString("d", _localizer.Culture);

    private static string AgentName(string id) => id switch
    {
        "claude-code" => "Claude Code",
        "codex" => "Codex",
        _ => id,
    };

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Pack.Id} ({State})");
}
